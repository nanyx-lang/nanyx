namespace Nanyx.Runtime

open System
open System.Net.Http
open System.Text
open System.Threading
open Wasmtime

/// The runtime's own import for outbound HTTP, `nanyx:http`, on which the console host implements `Http` (see the
/// Foreign functions page of the docs). WASI preview 1 has no networking, so until the backend emits components and
/// can use `wasi:http`, a program makes requests through these functions, backed by .NET's `HttpClient`:
///
/// - `start(request, length) -> i32` starts the request written in the `length` bytes at `request`, and gives a handle
///   to it at once. When it is answered, it signals the store's `AsyncImport.Waits`, so that a task waiting on it runs
///   while it is in flight;
/// - `poll(handle, kind) -> i32` gives -1 while the request is in flight; once it is answered, it keeps the answer,
///   writes what kind of answer it is at `kind` (see `Answer`), gives its length in bytes, and is done with the handle;
/// - `forget(handle)` is done with the request of `handle` without its answer, as a cancelled task is: it is answered,
///   but nothing keeps the answer;
/// - `send(request, length, kind) -> i32` makes the request, blocking until it is answered, and keeps the answer as
///   `poll` does, for a module built before `start`;
/// - `receive(destination)` copies the kept answer to `destination`, where the program has made room for it.
///
/// A request is written as lines, each ended by `\n`: the method, the URL, the timeout in milliseconds, the number of
/// headers, then each header's name and value on lines of their own; the body follows, as it is. A response is written
/// in the same way: the status, the number of headers, each header's name (in lower case) and value, then the body.
module HttpImport =
    /// The import module's name.
    [<Literal>]
    let moduleName = "nanyx:http"

    /// What kind of answer `send` kept, as it writes it for the program: a response, or why there is none. With
    /// `ConnectionFailed` and `Other` the answer is a message saying why.
    type Answer =
        | Response = 0
        | Timeout = 1
        | ConnectionFailed = 2
        | InvalidUrl = 3
        | Other = 4

    /// One client for every request the process makes, as `HttpClient` is meant to be used. Each request has a timeout
    /// of its own.
    let private client = new HttpClient(Timeout = Timeout.InfiniteTimeSpan)

    /// Reads `bytes` a line at a time, each ended by `\n`, and then what is left.
    type private Lines(bytes: byte array) =
        let mutable position = 0

        /// The next line, without its `\n`.
        member _.Next() =
            let start = position
            let stop = Array.IndexOf(bytes, byte '\n', start)
            let stop = if stop < 0 then bytes.Length else stop
            position <- min bytes.Length (stop + 1)
            Encoding.UTF8.GetString(bytes, start, stop - start)

        /// The headers that follow: how many there are, then each one's name and value.
        member lines.Headers() = [ for _ in 1 .. int (lines.Next()) -> let name = lines.Next() in name, lines.Next() ]

        /// What is left after the lines read.
        member _.Rest = bytes.[position..]

    /// `headers`, how many there are and then each one's name and value, each on a line of its own, after `text`.
    let private appendHeaders (text: StringBuilder) (headers: (string * string) list) =
        text.Append(headers.Length).Append('\n') |> ignore
        for name, value in headers do
            text.Append(name).Append('\n').Append(value).Append('\n') |> ignore

    /// A request to the program, as its host reads it (see the module's comment): with no timeout, since it is the
    /// program that answers it.
    let internal writtenRequest (methodName: string) (url: string) (headers: (string * string) list) (body: byte array) =
        let text = StringBuilder()
        text.Append(methodName).Append('\n').Append(url).Append("\n0\n") |> ignore
        appendHeaders text headers
        Array.append (Encoding.UTF8.GetBytes(text.ToString())) body

    /// The status, headers and body of the response written in `bytes`, or none when it isn't one.
    let internal responseOf (bytes: byte array) : (int * (string * string) list * byte array) option =
        try
            let lines = Lines bytes
            let status = int (lines.Next())
            let headers = lines.Headers()
            Some(status, headers, lines.Rest)
        with :? FormatException -> None

    /// The request written in `bytes` (see the module's comment), with its timeout, or why it isn't one.
    let private requestOf (bytes: byte array) : Result<HttpRequestMessage * TimeSpan, Answer * string> =
        let lines = Lines bytes
        let methodName = lines.Next()
        let url = lines.Next()
        let timeout = TimeSpan.FromMilliseconds(float (int (lines.Next())))
        let headers = lines.Headers()
        let body = lines.Rest
        match Uri.TryCreate(url, UriKind.Absolute) with
        | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps ->
            let request = new HttpRequestMessage(HttpMethod(methodName), uri)
            if body.Length > 0 || methodName = "POST" || methodName = "PUT" || methodName = "PATCH" then
                request.Content <- new ByteArrayContent(body)
            for name, value in headers do
                // Some headers, such as `Content-Type`, are the content's rather than the request's.
                if not (request.Headers.TryAddWithoutValidation(name, value)) && not (isNull request.Content) then
                    request.Content.Headers.TryAddWithoutValidation(name, value) |> ignore
            Ok(request, timeout)
        | _ -> Error(Answer.InvalidUrl, url)

    /// `response` written as the program reads it (see the module's comment).
    let private written (response: HttpResponseMessage) (body: byte array) =
        let headers =
            Seq.append response.Headers response.Content.Headers
            |> Seq.map (fun header -> header.Key.ToLowerInvariant(), String.Join(", ", header.Value))
            |> List.ofSeq
        let text = StringBuilder()
        text.Append(int response.StatusCode).Append('\n') |> ignore
        appendHeaders text headers
        Array.append (Encoding.UTF8.GetBytes(text.ToString())) body

    /// Makes the request written in `bytes`, and gives what kind of answer it had, with the answer, once it has one.
    let private answer (bytes: byte array) : Tasks.Task<Answer * byte array> =
        task {
            match requestOf bytes with
            | Error(kind, message) -> return kind, Encoding.UTF8.GetBytes message
            | Ok(request, timeout) ->
                use request = request
                use cancel = new CancellationTokenSource(timeout)
                try
                    use! response = client.SendAsync(request, cancel.Token)
                    let! body = response.Content.ReadAsByteArrayAsync(cancel.Token)
                    return Answer.Response, written response body
                with
                // A timeout that ends the request while it connects can fail it as a connection that failed.
                | _ when cancel.IsCancellationRequested -> return Answer.Timeout, [||]
                | :? OperationCanceledException -> return Answer.Timeout, [||]
                | :? HttpRequestException as ex -> return Answer.ConnectionFailed, Encoding.UTF8.GetBytes ex.Message
                | ex -> return Answer.Other, Encoding.UTF8.GetBytes ex.Message
        }

    /// Defines `start`, `poll`, `send` and `receive` on `linker`, for one store: the requests in flight and the answer
    /// kept are this store's, and a request that is answered signals `waits`.
    let define (linker: Linker) (waits: AsyncImport.Waits) =
        let kept = ref [||]
        let inFlight = Collections.Generic.Dictionary<int, Tasks.Task<Answer * byte array>>()
        let mutable lastHandle = 0
        // Keeps `answer` for `receive`, and writes its kind at `kind`: what `poll` and `send` give back.
        let keep (memory: Memory) (kind: int) (answerKind: Answer, answer: byte array) =
            kept.Value <- answer
            memory.WriteInt32(int64 kind, int answerKind)
            answer.Length
        linker.DefineFunction(
            moduleName,
            "start",
            CallerFunc<int, int, int>(fun caller request length ->
                let bytes = (caller.GetMemory "memory").GetSpan(int64 request, length).ToArray()
                let answered = answer bytes
                answered.ContinueWith(fun (_: Tasks.Task<Answer * byte array>) -> waits.Signal()) |> ignore
                lastHandle <- lastHandle + 1
                inFlight.[lastHandle] <- answered
                lastHandle))
        linker.DefineFunction(
            moduleName,
            "poll",
            CallerFunc<int, int, int>(fun caller handle kind ->
                match inFlight.TryGetValue handle with
                | true, answered when answered.IsCompleted ->
                    inFlight.Remove handle |> ignore
                    keep (caller.GetMemory "memory") kind answered.Result
                | _ -> -1))
        linker.DefineFunction(moduleName, "forget", Action<int>(fun handle -> inFlight.Remove handle |> ignore))
        linker.DefineFunction(
            moduleName,
            "send",
            CallerFunc<int, int, int, int>(fun caller request length kind ->
                let memory = caller.GetMemory "memory"
                keep memory kind ((answer (memory.GetSpan(int64 request, length).ToArray())).GetAwaiter().GetResult())))
        linker.DefineFunction(
            moduleName,
            "receive",
            CallerAction<int>(fun caller destination ->
                let memory = caller.GetMemory "memory"
                kept.Value.AsSpan().CopyTo(memory.GetSpan(int64 destination, kept.Value.Length))
                kept.Value <- [||]))
