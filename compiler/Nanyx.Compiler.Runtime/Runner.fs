namespace Nanyx.Runtime

open System
open Wasmtime

type RunError =
    /// The WebAssembly text could not be parsed or validated.
    | InvalidModule of string
    /// The program trapped (for example `unreachable` from a match with no matching arm) or failed to instantiate.
    | Trapped of string
    /// The program ended itself with a non-zero exit code, through WASI `proc_exit`: a deferred error does, once it
    /// has written itself to stderr.
    | Exited of int

/// Where a program's standard streams are.
type ProgramIo =
    /// This process's own stdin, stdout and stderr.
    | Inherited
    /// Stdin is the file `input`. What the program writes to stdout, `dbg` of a tag included, goes to `output`, and
    /// what it writes to stderr goes to `error`, each in the order it was written. Tests use this to run a program in
    /// their own process.
    | Redirected of input: string * output: IO.Stream * error: IO.Stream

/// Runs WebAssembly text emitted by the Nanyx backend on an embedded Wasmtime engine.
///
/// The backend turns top-level expressions into a WebAssembly start function, so instantiating the module
/// runs the program. The program's output goes to its stdout (see `ProgramIo`): through WASI `fd_write` for
/// `nanyx/console` (the `__nanyx_write_stdout` intrinsic) and `dbg` of strings and integers, and through
/// host functions for the `env.dbg_tag_*` imports that `dbg(#tag)` emits.
module WasmRunner =
    let private stripImportPrefix (prefix: string) (name: string) = name.Substring prefix.Length

    /// Writes `line` to the program's stdout, ended with a bare "\n" to match WASI's output byte for byte, never the
    /// platform newline.
    let private writeLine (io: ProgramIo) (line: string) =
        match io with
        | Inherited ->
            Console.Out.Write(line + "\n")
            Console.Out.Flush()
        | Redirected(_, output, _) -> output.Write(Text.Encoding.UTF8.GetBytes(line + "\n"))

    /// Defines a host function for each `env` import the module declares.
    let private defineHostImports (io: ProgramIo) (linker: Linker) (module': Module) =
        for import in module'.Imports do
            match import with
            | :? FunctionImport as fn when fn.ModuleName = "env" ->
                let name = fn.Name
                if name.StartsWith "dbg_tag_payload_" then
                    let tag = stripImportPrefix "dbg_tag_payload_" name
                    linker.DefineFunction(fn.ModuleName, name, Action<int>(fun payload -> writeLine io $"dbg: #{tag}({payload})"))
                elif name.StartsWith "dbg_tag_" then
                    let tag = stripImportPrefix "dbg_tag_" name
                    linker.DefineFunction(fn.ModuleName, name, Action(fun () -> writeLine io $"dbg: #{tag}"))
                else
                    failwith $"The program imports an unknown host function: env.{name}"
            | _ -> ()

    /// Writes the buffers of the `count` WASI iovecs at `iovecs` in `memory` to `stream`, and gives how many bytes that
    /// was.
    let private writeIovecs (memory: Memory) (iovecs: int) (count: int) (stream: IO.Stream) =
        let mutable total = 0
        for index in 0 .. count - 1 do
            let iovec = int64 (iovecs + index * 8)
            let length = memory.ReadInt32(iovec + 4L)
            stream.Write(Span.op_Implicit(memory.GetSpan(int64 (memory.ReadInt32 iovec), length)): ReadOnlySpan<byte>)
            total <- total + length
        total

    /// WASI's `errno` for a file descriptor that is not open.
    let private badFileDescriptor = 8

    /// Sets up the program's standard streams. Redirected output is written by a `fd_write` of our own, in place of
    /// WASI's, so that it lands in the same stream as the `dbg` host functions' output and in the same order.
    let private configureIo (io: ProgramIo) (linker: Linker) (store: Store) =
        match io with
        | Inherited ->
            store.SetWasiConfiguration(
                WasiConfiguration()
                    .WithInheritedStandardInput()
                    .WithInheritedStandardOutput()
                    .WithInheritedStandardError())
        | Redirected(input, output, error) ->
            store.SetWasiConfiguration(WasiConfiguration().WithStandardInput(input))
            linker.AllowShadowing <- true
            linker.DefineFunction(
                "wasi_snapshot_preview1",
                "fd_write",
                CallerFunc<int, int, int, int, int>(fun caller fd iovecs count written ->
                    match fd with
                    | 1 | 2 ->
                        let memory = caller.GetMemory "memory"
                        memory.WriteInt32(int64 written, writeIovecs memory iovecs count (if fd = 1 then output else error))
                        0
                    | _ -> badFileDescriptor))

    /// Whether `ex` is the program ending itself through WASI `proc_exit` with exit code 0.
    let private exitedCleanly (ex: WasmtimeException) = ex.ExitCode.HasValue && ex.ExitCode.Value = 0

    /// Instantiates the module described by `wat`, which runs its start function, then calls its export `start` if it
    /// is given and the module exports it, and gives what `read` makes of the instance afterwards: `None` when the program ended itself while it
    /// was being instantiated, so there is no instance.
    let private execute (io: ProgramIo) (wat: string) (start: string option) (read: Instance option -> 'a) : Result<'a, RunError> =
        use engine = new Engine()
        let moduleResult =
            try Ok (Module.FromText(engine, "nanyx", wat))
            with :? WasmtimeException as ex -> Error (InvalidModule ex.Message)
        match moduleResult with
        | Error err -> Error err
        | Ok module' ->
            use module' = module'
            use linker = new Linker(engine)
            use store = new Store(engine)
            linker.DefineWasi()
            configureIo io linker store
            try
                defineHostImports io linker module'
                let instance = linker.Instantiate(store, module')
                // A program with no top-level code has no start function to export.
                match start |> Option.map instance.GetAction with
                | Some null
                | None -> ()
                | Some action ->
                    try action.Invoke()
                    with :? WasmtimeException as ex when exitedCleanly ex -> ()
                Ok (read (Some instance))
            with
            | :? WasmtimeException as ex when exitedCleanly ex -> Ok (read None)
            | :? WasmtimeException as ex when ex.ExitCode.HasValue -> Error (Exited ex.ExitCode.Value)
            | :? WasmtimeException as ex -> Error (Trapped ex.Message)

    /// Instantiates (and therefore runs) the module described by `wat`, with its standard streams `io`.
    let run (io: ProgramIo) (wat: string) : Result<unit, RunError> = execute io wat None ignore

    /// Runs the module described by `wat`, which exports its start function as `start` rather than running it when
    /// instantiated, and gives the values of its exported `i32` globals `globals` once `start` has returned. A global
    /// the module does not export counts as 0: a module that never allocates has no allocation counters.
    let runReading (io: ProgramIo) (start: string) (globals: string array) (wat: string) : Result<int array, RunError> =
        execute io wat (Some start) (fun instance ->
            globals |> Array.map (fun name ->
                match instance |> Option.map _.GetGlobal(name) with
                | Some null | None -> 0
                | Some global' -> global'.GetValue() :?> int))
