namespace Nanyx.Runtime

open System
open Wasmtime

type RunError =
    /// The WebAssembly text could not be parsed or validated.
    | InvalidModule of string
    /// The program trapped (for example `unreachable` from a match with no matching arm) or failed to instantiate.
    | Trapped of string
    /// The program asked for more memory than there is: its allocator trapped because a new block would end past
    /// 4 GB, or because the host would not grow its memory.
    | OutOfMemory
    /// The program ended itself with a non-zero exit code, through WASI `proc_exit`: a deferred error does, once it
    /// has written itself to stderr.
    | Exited of int

/// Where a program's standard streams are, and which environment variables it sees.
type ProgramIo =
    /// This process's own stdin, stdout, stderr and environment variables.
    | Inherited
    /// Stdin is the file `input`. What the program writes to stdout goes to `output`, and
    /// what it writes to stderr goes to `error`, each in the order it was written. The program's environment variables
    /// are `variables`, each a name and its value. Tests use this to run a program in their own process.
    | Redirected of input: string * output: IO.Stream * error: IO.Stream * variables: (string * string) list

/// What a program compiled with memory checks left once it stopped: the values of the exported globals asked for, in
/// order (0 for one the module does not export), and how big its memory had grown, in bytes (0 with none).
type HeapCounters = { Globals: int64 array; MemoryBytes: int64 }

/// A module to run: WebAssembly text, or the binary format.
type Program =
    | Text of string
    | Binary of byte array

/// Runs WebAssembly text emitted by the Nanyx backend on an embedded Wasmtime engine.
///
/// The backend turns top-level expressions into a WebAssembly start function, so instantiating the module
/// runs the program. The program's output goes to its stdout (see `ProgramIo`) through WASI `fd_write`, from
/// the console host (which declares it with `@extern`) and from `dbg`.
module WasmRunner =
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

    /// Sets up the program's command-line arguments (`arguments`, its name first), standard streams and environment
    /// variables. Redirected output is written by a `fd_write` of our own, in place of WASI's, so that it lands in the
    /// caller's streams.
    let private configureIo (io: ProgramIo) (arguments: string list) (linker: Linker) (store: Store) =
        let wasi = WasiConfiguration().WithArgs(arguments)
        match io with
        | Inherited ->
            store.SetWasiConfiguration(
                wasi
                    .WithInheritedStandardInput()
                    .WithInheritedStandardOutput()
                    .WithInheritedStandardError()
                    .WithInheritedEnvironment())
        | Redirected(input, output, error, variables) ->
            store.SetWasiConfiguration(wasi.WithStandardInput(input).WithEnvironmentVariables(variables |> List.map (fun (name, value) -> struct (name, value))))
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

    /// The allocator's function, which the backend names `WasmAllocator.Names.alloc`. It traps with `unreachable` only
    /// when the program is out of memory.
    let private allocator = "__nanyx_alloc"

    /// What the trap `ex` means: out of memory when it is an `unreachable` in the allocator itself.
    let private trapped (ex: WasmtimeException) =
        match ex with
        | :? TrapException as trap when
            trap.Type = TrapCode.Unreachable
            && not (isNull trap.Frames)
            && trap.Frames.Count > 0
            && trap.Frames[0].FunctionName = allocator ->
            OutOfMemory
        | _ -> Trapped ex.Message

    /// Instantiates the module described by `wat`, which runs its start function, then calls its export `start` if it
    /// is given and the module exports it, and gives what `read` makes of the instance afterwards: `None` when the program
    /// ended itself, through `proc_exit` with exit code 0, so there is no instance or it stopped without freeing what it
    /// held. Also gives what `inspect` makes of the instance once the program has stopped, however it stopped: `None` only
    /// when it was never instantiated.
    let private executeInspecting
        (io: ProgramIo)
        (arguments: string list)
        (program: Program)
        (start: string option)
        (read: Instance option -> 'a)
        (inspect: Instance -> 'b)
        : Result<'a, RunError> * 'b option =
        use engine = new Engine()
        let moduleResult =
            try
                match program with
                | Text wat -> Ok (Module.FromText(engine, "nanyx", wat))
                | Binary bytes -> Ok (Module.FromBytes(engine, "nanyx", bytes))
            with :? WasmtimeException as ex -> Error (InvalidModule ex.Message)
        match moduleResult with
        | Error err -> Error err, None
        | Ok module' ->
            use module' = module'
            use linker = new Linker(engine)
            use store = new Store(engine)
            linker.DefineWasi()
            configureIo io arguments linker store
            let mutable instantiated = None
            let outcome =
                try
                    let instance = linker.Instantiate(store, module')
                    instantiated <- Some instance
                    // A program with no top-level code has no start function to export.
                    match start |> Option.map instance.GetAction with
                    | Some null
                    | None -> Ok (read (Some instance))
                    | Some action ->
                        try
                            action.Invoke()
                            Ok (read (Some instance))
                        with :? WasmtimeException as ex when exitedCleanly ex -> Ok (read None)
                with
                | :? WasmtimeException as ex when exitedCleanly ex -> Ok (read None)
                | :? WasmtimeException as ex when ex.ExitCode.HasValue -> Error (Exited ex.ExitCode.Value)
                | :? WasmtimeException as ex -> Error (trapped ex)
            outcome, instantiated |> Option.map inspect

    let private execute (io: ProgramIo) (arguments: string list) (program: Program) (start: string option) (read: Instance option -> 'a) : Result<'a, RunError> =
        executeInspecting io arguments program start read ignore |> fst

    /// The value of the exported global `name` of `instance`, an `i32` or an `i64`: 0 when it does not export it.
    let private globalValue (instance: Instance) (name: string) : int64 =
        match instance.GetGlobal name with
        | null -> 0L
        | global' ->
            match global'.GetValue() with
            | :? int64 as value -> value
            | value -> int64 (value :?> int)

    /// Instantiates (and therefore runs) the module described by `wat`, with its standard streams `io` and its
    /// command-line arguments `arguments`, its name first.
    let run (io: ProgramIo) (arguments: string list) (wat: string) : Result<unit, RunError> = execute io arguments (Text wat) None ignore

    /// `run` for a module in the binary format, `wasm`.
    let runBinary (io: ProgramIo) (arguments: string list) (wasm: byte array) : Result<unit, RunError> =
        execute io arguments (Binary wasm) None ignore

    /// What `runCounting` gives, for any `Program`.
    let private counting (io: ProgramIo) (arguments: string list) (start: string) (globals: string array) (program: Program) =
        executeInspecting
            io
            arguments
            program
            (Some start)
            (fun instance ->
                globals |> Array.map (fun name -> instance |> Option.map (fun instance -> int (globalValue instance name)) |> Option.defaultValue 0))
            (fun instance ->
                { Globals = globals |> Array.map (globalValue instance)
                  MemoryBytes =
                    match instance.GetMemory "memory" with
                    | null -> 0L
                    | memory -> memory.GetSize() * 65536L })

    /// Runs the module described by `wat`, which exports its start function as `start` rather than running it when
    /// instantiated, and gives the values of its exported `i32` globals `globals` once `start` has returned, as
    /// `runReading` does, and the `HeapCounters` the program left, with `globals` read from them, however it stopped:
    /// `None` only when the module was never instantiated.
    let runCounting
        (io: ProgramIo)
        (arguments: string list)
        (start: string)
        (globals: string array)
        (wat: string)
        : Result<int array, RunError> * HeapCounters option =
        counting io arguments start globals (Text wat)

    /// Runs the module described by `wat`, which exports its start function as `start` rather than running it when
    /// instantiated, and gives the values of its exported `i32` globals `globals` once `start` has returned. A global
    /// the module does not export counts as 0: a module that never allocates has no allocation counters. So does every
    /// global of a program that ended itself with exit code 0, which leaves what it allocated as it is.
    let runReading (io: ProgramIo) (arguments: string list) (start: string) (globals: string array) (wat: string) : Result<int array, RunError> =
        runCounting io arguments start globals wat |> fst

    /// `runReading` for a module in the binary format, `wasm`.
    let runBinaryReading (io: ProgramIo) (arguments: string list) (start: string) (globals: string array) (wasm: byte array) : Result<int array, RunError> =
        counting io arguments start globals (Binary wasm) |> fst
