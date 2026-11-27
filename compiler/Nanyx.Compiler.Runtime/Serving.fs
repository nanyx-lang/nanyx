namespace Nanyx.Runtime

open System
open System.IO
open System.Net
open System.Text
open System.Threading
open System.Threading.Tasks
open Wasmtime

/// Serving a program whose host answers HTTP requests, as the HTTP host (`nanyx.http.host`) does, for local development.
/// Such a host imports the runtime's own `nanyx:serve`: each request is answered by a fresh instance of the program,
/// whose start function runs the host's `run`, which takes the request through `nanyx:serve` and gives back the
/// response. Nothing is shared between requests, so they are answered at the same time, each in its own store. An
/// instance that stops without answering, because it trapped or exited, is answered with a 500, and the server carries
/// on. The functions of `nanyx:serve`:
///
/// - `requestLength() -> i32`, the length in bytes of the request the instance answers, written as
///   `nanyx.http/wire` reads it;
/// - `request(destination: i32)`, which copies it to `destination`;
/// - `respond(response: i32, length: i32)`, which gives the response written in the `length` bytes at `response`.
module WasmServer =
    /// The import module's name.
    [<Literal>]
    let moduleName = "nanyx:serve"

    /// Whether the program in the WebAssembly text `wat` answers HTTP requests: its host imports `nanyx:serve`.
    let serves (wat: string) = wat.Contains $"(import \"{moduleName}\""

    /// The headers the listener writes itself, which a response's own are not copied over.
    let private ownHeaders = set [ "content-length"; "transfer-encoding"; "connection"; "keep-alive" ]

    /// Writes `status`, `headers` and `body` as the answer to `context`.
    let private reply (context: HttpListenerContext) (status: int) (headers: (string * string) list) (body: byte array) =
        let response = context.Response
        response.StatusCode <- status
        for name, value in headers do
            if not (ownHeaders.Contains(name.ToLowerInvariant())) then
                response.Headers.Add(name, value)
        response.ContentLength64 <- int64 body.Length
        response.OutputStream.Write(body, 0, body.Length)
        response.Close()

    /// The request `context` holds, written as the program's host reads it.
    let private incoming (context: HttpListenerContext) =
        let request = context.Request
        use body = new MemoryStream()
        request.InputStream.CopyTo body
        let headers = [ for name in request.Headers.AllKeys -> name.ToLowerInvariant(), request.Headers.[name] ]
        HttpImport.writtenRequest request.HttpMethod (request.Url.ToString()) headers (body.ToArray())

    /// Answers the request `context` holds with a fresh instance of `module'`, run with the standard streams `io` and
    /// the command-line arguments `arguments`; a request it doesn't answer is a 500, and why is given to `report`, as is
    /// what its `dbg`s print.
    /// `gate` is held while what the instance wrote to redirected streams is copied to them.
    let private answer engine (module': Module) (table: SpanTable option) io arguments (gate: obj) (report: string -> unit) (context: HttpListenerContext) =
        let request = incoming context
        let what = $"{context.Request.HttpMethod} {context.Request.Url.PathAndQuery}"
        let response = ref None
        let mutable finish = ignore
        let outcome =
            try
                use linker = new Linker(engine)
                use store = new Store(engine)
                linker.DefineWasi()
                let waits = AsyncImport.Waits()
                AsyncImport.define linker waits
                HttpImport.define linker waits
                linker.DefineFunction(moduleName, "requestLength", Func<int>(fun () -> request.Length))
                linker.DefineFunction(
                    moduleName,
                    "request",
                    CallerAction<int>(fun caller destination ->
                        request.AsSpan().CopyTo((caller.GetMemory "memory").GetSpan(int64 destination, request.Length))))
                linker.DefineFunction(
                    moduleName,
                    "respond",
                    CallerAction<int, int>(fun caller address length ->
                        response.Value <- Some((caller.GetMemory "memory").GetSpan(int64 address, length).ToArray())))
                Probing.define linker table None
                let reported = ref None
                CrashImport.define linker reported
                // What the handler's `dbg` prints goes to the server's log, with the request, and never into the response.
                Probing.defineDbg linker (fun hit ->
                    report $"dbg ({what}): {hit}"
                    true)
                finish <- WasmRunner.configureIo io arguments [] store
                try
                    linker.Instantiate(store, module') |> ignore
                    Ok()
                with
                | :? WasmtimeException as ex when WasmRunner.exitedCleanly ex -> Ok()
                | :? WasmtimeException as ex when ex.ExitCode.HasValue -> Error $"it exited with code {ex.ExitCode.Value}"
                | :? WasmtimeException as ex ->
                    match WasmRunner.trapped table reported.Value ex with
                    | Trapped(message, stack) -> Error(message + WasmRunner.stackText stack)
                    | Crashed(message, place, stack) ->
                        let at = place |> Option.map (fun (file, line, column) -> $" at {file}:{line}:{column}") |> Option.defaultValue ""
                        let why = message |> Option.map (fun message -> $": {message}") |> Option.defaultValue ""
                        Error($"it crashed{at}{why}" + WasmRunner.stackText stack)
                    | OutOfMemory -> Error "it ran out of memory"
                    | StackOverflow _ -> Error "it ran out of stack"
                    | ArithmeticError(fault, functionName, stack) -> Error(WasmRunner.describeFault fault functionName + WasmRunner.stackText stack)
                    | other -> Error(string other)
            finally
                lock gate finish
        match outcome, response.Value |> Option.bind HttpImport.responseOf with
        | Ok(), Some(status, headers, body) -> reply context status headers body
        | Ok(), None ->
            report $"error: the program gave no response to {what}"
            reply context 500 [ "Content-Type", "text/plain; charset=utf-8" ] (Encoding.UTF8.GetBytes "Internal Server Error")
        | Error why, _ ->
            report $"error: the program stopped while answering {what}: {why}"
            reply context 500 [ "Content-Type", "text/plain; charset=utf-8" ] (Encoding.UTF8.GetBytes "Internal Server Error")

    /// Serves `program` on `port` of this machine until `stop` is cancelled, answering each request with a fresh
    /// instance of it, with the standard streams `io` and the command-line arguments `arguments`, its name first. Once it
    /// is listening, it calls `listening` with where; it gives what goes wrong to `report`, and what each request's `dbg`s
    /// print, with the request (see `Probing.defineDbg`).
    let serve
        (io: ProgramIo)
        (arguments: string list)
        (program: Program)
        (port: int)
        (stop: CancellationToken)
        (listening: string -> unit)
        (report: string -> unit)
        : Result<unit, RunError> =
        use engine = ProgramStack.engine id
        match WasmRunner.compile engine program with
        | Error error -> Error error
        | Ok module' ->
            use module' = module'
            use listener = new HttpListener()
            let address = $"http://localhost:{port}/"
            listener.Prefixes.Add address
            match (try listener.Start(); None with :? HttpListenerException as ex -> Some ex.Message) with
            | Some reason -> Error(CannotServe(port, reason))
            | None ->
                listening address
                let gate = obj ()
                let table = WasmRunner.spanTable program
                let answering = Collections.Concurrent.ConcurrentDictionary<Task, unit>()
                try
                    try
                        while not stop.IsCancellationRequested do
                            let context = listener.GetContextAsync().WaitAsync(stop).GetAwaiter().GetResult()
                            // Each instance runs on a thread of its own, so that one whose handler waits, on an
                            // outbound request say, holds only that thread, not one the listener needs, and with
                            // the stack a program runs with (see `ProgramStack`).
                            let task =
                                Task.Factory.StartNew(
                                    (fun () -> ProgramStack.run (fun () -> answer engine module' table io arguments gate report context)),
                                    TaskCreationOptions.LongRunning)
                            answering.TryAdd(task, ()) |> ignore
                            task.ContinueWith(fun (finished: Task) -> answering.TryRemove finished |> ignore) |> ignore
                    with :? OperationCanceledException -> ()
                finally
                    Task.WaitAll(answering.Keys |> Array.ofSeq, TimeSpan.FromSeconds 5.0) |> ignore
                    listener.Stop()
                Ok()
