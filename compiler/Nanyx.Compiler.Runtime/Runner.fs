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
    /// The program ran out of stack, in the function named here when there is one: the innermost on the stack that the
    /// source names, usually one that recurses in a call that is not in tail position.
    | StackOverflow of functionName: string option
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

    /// The names of the functions on the stack when `trap` happened, innermost first.
    let private frameNames (trap: TrapException) =
        if isNull trap.Frames then [] else trap.Frames |> Seq.map _.FunctionName |> List.ofSeq

    /// The name a function has in the source, from the name the backend gave it: `count`; `list_fold` for a library
    /// module's `fold`; `go` for a local `rec go` (`__rec_go_16`, see `LambdaLifting`); without the type arguments of a
    /// specialisation (`map__int`) or the suffix of a closure's owning entry (`__owning`, see `CoreIrClosureEntries`).
    /// None for a lambda (`__lambda_3`), a runtime helper (`__nanyx_alloc`) and the other functions the backend makes,
    /// whose names all start with `__`.
    let private sourceName (name: string) =
        // The name without what follows its first `__`, the type arguments of a specialisation or `__owning`.
        let own (name: string) =
            match name.IndexOf "__" with
            | -1 -> name
            | index -> name.Substring(0, index)
        if String.IsNullOrEmpty name then None
        elif name.StartsWith "__rec_" then
            let numbered = own (name.Substring "__rec_".Length)
            match numbered.LastIndexOf '_' with
            | index when index > 0 -> Some(numbered.Substring(0, index))
            | _ -> None
        elif name.StartsWith "__fn_" then Some(own (name.Substring "__fn_".Length))
        elif name.StartsWith "__" then None
        else Some(own name)

    /// What the trap `ex` means: out of memory when it is an `unreachable` in the allocator itself, and out of stack
    /// when the call stack is exhausted, in the innermost function on it that the source names.
    let private trapped (ex: WasmtimeException) =
        match ex with
        | :? TrapException as trap when trap.Type = TrapCode.Unreachable && List.tryHead (frameNames trap) = Some allocator ->
            OutOfMemory
        | :? TrapException as trap when trap.Type = TrapCode.StackOverflow -> StackOverflow(frameNames trap |> List.tryPick sourceName)
        | _ -> Trapped ex.Message

    /// Instantiates the module described by `wat`, which runs its start function, then calls its export `start` if it
    /// is given and the module exports it, and gives what `read` makes of the instance afterwards: `None` when the program
    /// ended itself, through `proc_exit` with exit code 0, so there is no instance or it stopped without freeing what it
    /// held. Also gives what `inspect` makes of the instance once the program has stopped, however it stopped: `None` only
    /// when it was never instantiated.
    let private executeInspecting
        (io: ProgramIo)
        (arguments: string list)
        (wat: string)
        (start: string option)
        (read: Instance option -> 'a)
        (inspect: Instance -> 'b)
        : Result<'a, RunError> * 'b option =
        use engine = new Engine()
        let moduleResult =
            try Ok (Module.FromText(engine, "nanyx", wat))
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

    let private execute (io: ProgramIo) (arguments: string list) (wat: string) (start: string option) (read: Instance option -> 'a) : Result<'a, RunError> =
        executeInspecting io arguments wat start read ignore |> fst

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
    let run (io: ProgramIo) (arguments: string list) (wat: string) : Result<unit, RunError> = execute io arguments wat None ignore

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
        executeInspecting
            io
            arguments
            wat
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
    /// instantiated, and gives the values of its exported `i32` globals `globals` once `start` has returned. A global
    /// the module does not export counts as 0: a module that never allocates has no allocation counters. So does every
    /// global of a program that ended itself with exit code 0, which leaves what it allocated as it is.
    let runReading (io: ProgramIo) (arguments: string list) (start: string) (globals: string array) (wat: string) : Result<int array, RunError> =
        runCounting io arguments start globals wat |> fst
