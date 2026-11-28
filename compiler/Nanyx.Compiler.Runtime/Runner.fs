namespace Nanyx.Runtime

open System
open Wasmtime

/// Arithmetic a program can't finish, which stops it.
type ArithmeticFault =
    /// `+`, `-`, `*` or `/` on an integer type whose result is out of the type's range: the type's name (`int`, `I8`)
    /// when the helper that stopped names it.
    | IntegerOverflow of typeName: string option
    /// `/` or `%` on integers by zero.
    | DivisionByZero
    /// A float made an `int` that is NaN, infinite or out of the range of `int`.
    | FloatNotAnInt
    /// An int made a sized integer of the type named here, which it doesn't fit.
    | OutOfRange of typeName: string

type RunError =
    /// The WebAssembly text could not be parsed or validated.
    | InvalidModule of string
    /// The program trapped or failed to instantiate: why, and the stack where it trapped, innermost first.
    | Trapped of message: string * stack: Frame list
    /// The program crashed, or reached an error deferred to run time: it wrote why to stderr, and stopped with the stack
    /// given here, innermost first. The message is what it wrote, when it reported it to the runtime too (see
    /// `CrashImport`), as a module the command line runs does. The place is the file, line and column of the program's
    /// own code it crashed in: the innermost frame of the stack outside the standard library, so that a `crash` or an
    /// `orCrash` is placed at the column where the program called it (#2950). The column is 0 where the table has none.
    | Crashed of message: string option * place: (string * int * int) option * stack: Frame list
    /// The program asked for more memory than there is: its allocator trapped because a new block would end past
    /// 4 GB, or because the host would not grow its memory.
    | OutOfMemory
    /// The program ran out of stack, in the function named here when there is one: the innermost on the stack that the
    /// source names, usually one that recurses in a call that is not in tail position.
    | StackOverflow of functionName: string option
    /// The program stopped at arithmetic it can't finish, in the function named here when there is one: the innermost
    /// on the stack that the source names. The stack follows, innermost first.
    | ArithmeticError of fault: ArithmeticFault * functionName: string option * stack: Frame list
    /// The program ended itself with a non-zero exit code, through WASI `proc_exit`: a deferred error does, once it
    /// has written itself to stderr.
    | Exited of int
    /// The program answers HTTP requests, and the port it was to be served on can't be listened on, for the reason
    /// given.
    | CannotServe of port: int * reason: string

/// Where a program's standard streams are, and which environment variables it sees.
type ProgramIo =
    /// This process's own stdin, stdout, stderr and environment variables.
    | Inherited
    /// Stdin is the file `input`. What the program writes to stdout goes to `output`, and
    /// what it writes to stderr goes to `error`, each in the order it was written. The program's environment variables
    /// are `variables`, each a name and its value. Tests use this to run a program in their own process.
    | Redirected of input: string * output: IO.Stream * error: IO.Stream * variables: (string * string) list
    /// Stdin is the file `input`, and what the program writes to stdout and stderr goes to the files `output` and
    /// `error` as it writes it, for a caller to read while it runs, as the debugger does. The environment is
    /// `variables`.
    | ToFiles of input: string * output: string * error: string * variables: (string * string) list

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
/// the console host (which declares it with `@extern`); what `dbg` prints goes to its stderr (see `Probing.defineDbg`).
module WasmRunner =
    /// The name a program knows the directory `directory` by: its absolute path, written with `/`.
    let private guestName (directory: string) =
        let full = IO.Path.GetFullPath(directory).Replace('\\', '/')
        if full.Length > 1 && full.EndsWith "/" && not (full.EndsWith ":/") then full.TrimEnd '/' else full

    /// `wasi` with the directories the program may read and write opened for it (WASI's preopens): the first of
    /// `directories`, its current directory, as `.` and by its absolute path, and each of the rest by its absolute path.
    /// The console host finds a relative path in `.`, and an absolute one in the directory whose name it starts with.
    let private withDirectories (directories: string list) (wasi: WasiConfiguration) =
        let opened =
            match directories with
            | current :: _ -> wasi.WithPreopenedDirectory(current, ".", true)
            | [] -> wasi
        directories |> List.distinct |> List.fold (fun (wasi: WasiConfiguration) directory -> wasi.WithPreopenedDirectory(directory, guestName directory, true)) opened

    /// Sets up the program's command-line arguments (`arguments`, its name first), standard streams, environment
    /// variables and the directories it may reach (`directories`, see `withDirectories`), and gives what to do once
    /// the program has stopped and its store is gone. Redirected output is written by WASI to files of its own, which
    /// are then copied to the caller's streams and deleted.
    let internal configureIo (io: ProgramIo) (arguments: string list) (directories: string list) (store: Store) : unit -> unit =
        let wasi = WasiConfiguration().WithArgs(arguments) |> withDirectories directories
        match io with
        | Inherited ->
            store.SetWasiConfiguration(
                wasi
                    .WithInheritedStandardInput()
                    .WithInheritedStandardOutput()
                    .WithInheritedStandardError()
                    .WithInheritedEnvironment())
            ignore
        | Redirected(input, output, error, variables) ->
            let outputFile = IO.Path.GetTempFileName()
            let errorFile = IO.Path.GetTempFileName()
            store.SetWasiConfiguration(
                wasi
                    .WithStandardInput(input)
                    .WithStandardOutput(outputFile)
                    .WithStandardError(errorFile)
                    .WithEnvironmentVariables(variables |> List.map (fun (name, value) -> struct (name, value))))
            fun () ->
                for file, stream in [ outputFile, output; errorFile, error ] do
                    stream.Write(IO.File.ReadAllBytes file)
                    IO.File.Delete file
        | ToFiles(input, output, error, variables) ->
            store.SetWasiConfiguration(
                wasi
                    .WithStandardInput(input)
                    .WithStandardOutput(output)
                    .WithStandardError(error)
                    .WithEnvironmentVariables(variables |> List.map (fun (name, value) -> struct (name, value))))
            ignore

    /// Whether `ex` is the program ending itself through WASI `proc_exit` with exit code 0.
    let internal exitedCleanly (ex: WasmtimeException) = ex.ExitCode.HasValue && ex.ExitCode.Value = 0

    /// The allocator's function, which the backend names `WasmAllocator.Names.alloc`. It traps with `unreachable` only
    /// when the program is out of memory.
    let private allocator = "__nanyx_alloc"

    /// The names of the functions on the stack when `trap` happened, innermost first.
    let private frameNames (trap: TrapException) =
        if isNull trap.Frames then [] else trap.Frames |> Seq.map _.FunctionName |> List.ofSeq


    /// The integer type and the operation of a runtime helper on integers that the backend names `__nanyx_<type>_<op>`:
    /// `int` and `add` for `__nanyx_int_add`, `I8` and `from_int` for `__nanyx_i8_from_int` (see `SizedIntegerCode`).
    let private integerHelper (name: string) =
        let m = Text.RegularExpressions.Regex.Match(name, "^__nanyx_(int|[iu](?:8|16|32|64))_([a-z_]+)$")
        if not m.Success then None
        else
            let ty = m.Groups.[1].Value
            Some((if ty = "int" then ty else ty.ToUpperInvariant()), m.Groups.[2].Value)

    /// The runtime helper that makes a float an int, with `i64.trunc_f64_s`, which traps as a bad conversion on NaN and
    /// as an overflow on an infinity or a float out of the range of `int`.
    let private floatToInt = "__nanyx_float_to_int"

    /// The arithmetic fault the trap `trap` is, if it is one, by its trap code and the helper it happened in.
    let private arithmeticFault (trap: TrapException) =
        let innermost = frameNames trap |> List.tryHead
        let helper = innermost |> Option.bind integerHelper
        if trap.Type = TrapCode.IntegerDivisionByZero then Some DivisionByZero
        elif trap.Type = TrapCode.BadConversionToInteger || (trap.Type = TrapCode.IntegerOverflow && innermost = Some floatToInt) then
            Some FloatNotAnInt
        elif trap.Type = TrapCode.IntegerOverflow then
            match helper with
            | Some(ty, "from_int") -> Some(OutOfRange ty)
            | Some(ty, _) -> Some(IntegerOverflow(Some ty))
            | None -> Some(IntegerOverflow None)
        else None

    /// The helper a crash, or an error deferred to run time, traps in once it has written why (see
    /// `Transpiler.Wasm.WasmRuntime.Helpers.crashed`).
    let private crashed = "__nanyx_crashed"


    /// The program's stack where it trapped, innermost first, each frame with the file and line `table` gives its code,
    /// if there is a table. A frame whose code was inlined from other functions is shown as the frames the calls would
    /// have had, innermost first. A frame of a function the stack leaves out (see `FunctionNames.shown`) is left out.
    let private stackOf (table: SpanTable option) (trap: TrapException) =
        if isNull trap.Frames then []
        else
            trap.Frames
            |> Seq.collect (fun frame ->
                let place = table |> Option.map (fun table -> SpanTable.placeAt table (int frame.ModuleOffset)) |> Option.defaultValue []
                match place with
                | [] -> SpanTable.shownName table frame.FunctionName |> Option.map (fun name -> { Name = name; Place = None; Column = 0 }) |> Option.toList
                | inlined ->
                    inlined
                    |> List.choose (fun (functionName, file, line, column) ->
                        SpanTable.shownName table (defaultArg functionName frame.FunctionName)
                        |> Option.map (fun name -> { Name = name; Place = Some(file, line); Column = column })))
            |> List.ofSeq

    /// What the trap `ex` means, with the stack from `table`: out of memory when it is an `unreachable` in the allocator
    /// itself, a crash when it is the trap in `crashed`, out of stack when the call stack is exhausted, and an arithmetic
    /// fault (see `ArithmeticFault`), each of the last two in the innermost function on the stack that the source names.
    /// A function of the runtime's that threw, such as one of `HttpImport`'s, is a bug in the runtime, not the program:
    /// its exception is given whole, with its .NET stack, which the trap's message alone would lose.
    let internal trapped (table: SpanTable option) (reported: string option) (ex: WasmtimeException) =
        match ex with
        | _ when not (isNull ex.InnerException) -> Trapped(ex.InnerException.ToString(), [])
        | :? TrapException as trap when trap.Type = TrapCode.Unreachable && List.tryHead (frameNames trap) = Some allocator ->
            OutOfMemory
        | :? TrapException as trap when trap.Type = TrapCode.Unreachable && List.tryHead (frameNames trap) = Some crashed ->
            let stack = stackOf table trap
            let isLibrary (file: string) = table |> Option.exists (fun table -> SpanTable.isLibrary table file)
            let place =
                stack
                |> List.tryPick (fun frame ->
                    frame.Place |> Option.filter (fst >> isLibrary >> not) |> Option.map (fun (file, line) -> file, line, frame.Column))
            Crashed(reported, place, stack)
        | :? TrapException as trap when trap.Type = TrapCode.StackOverflow -> StackOverflow(frameNames trap |> List.tryPick (SpanTable.sourceName table))
        | :? TrapException as trap ->
            match arithmeticFault trap with
            | Some fault -> ArithmeticError(fault, frameNames trap |> List.tryPick (SpanTable.sourceName table), stackOf table trap)
            | None -> Trapped(ex.Message, stackOf table trap)
        | _ -> Trapped(ex.Message, [])

    /// The program's table of places, if it is in the binary format and has one (see `SpanTable`).
    let internal spanTable (program: Program) =
        match program with
        | Binary wasm -> SpanTable.read wasm
        | Text _ -> None

    /// What went wrong in the arithmetic fault `fault`, in the function `functionName` when it is known, as a
    /// sentence for the user.
    let describeFault (fault: ArithmeticFault) (functionName: string option) =
        let where = functionName |> Option.map (fun name -> $" in '{name}'") |> Option.defaultValue ""
        match fault with
        | IntegerOverflow(Some ty) -> $"integer overflow{where}: the result is out of the range of {ty}"
        | IntegerOverflow None -> $"integer overflow{where}"
        | DivisionByZero -> $"division by zero{where}"
        | FloatNotAnInt ->
            $"a float that is NaN, infinite or out of the range of int converted to an int{where} (float.tryToInt gives #outOfRange for one)"
        | OutOfRange ty -> $"an int out of the range of {ty} converted to {ty}{where}"

    /// The stack `stack` as the lines that follow what stopped the program, each frame on a line of its own after a
    /// line break and two spaces, innermost first; nothing for no frames.
    let stackText (stack: Frame list) =
        stack |> List.map (fun frame -> $"\n  {frame}") |> String.concat ""

    /// `program` compiled for `engine`, or why it can't be.
    let internal compile (engine: Engine) (program: Program) : Result<Module, RunError> =
        try
            match program with
            | Text wat -> Ok (Module.FromText(engine, "nanyx", wat))
            | Binary bytes -> Ok (Module.FromBytes(engine, "nanyx", bytes))
        with :? WasmtimeException as ex -> Error (InvalidModule ex.Message)

    /// How often a profiled run is sampled: what the Wasmtime command line's guest profiler samples at by default.
    let private profileInterval = TimeSpan.FromMilliseconds 10.0

    /// Instantiates `program` in a store that `configure` sets up, with its debug probes, if it has any, answered by
    /// `listener`, and sampled by the guest profiler if `profiled` is given, which it gives the profile once the program
    /// has stopped, which runs its start function, then calls its export
    /// `start` if it is given and the module exports it, and gives what `read` makes of the instance afterwards: `None`
    /// when the program ended itself, through `proc_exit` with exit code 0, so there is no instance or it stopped without
    /// freeing what it held. Also gives what `inspect` makes of the instance once the program has stopped, however it
    /// stopped: `None` only when it was never instantiated. The store is gone once it returns.
    let private executeIn
        (configure: Store -> unit)
        (program: Program)
        (start: string option)
        (read: Instance option -> 'a)
        (inspect: Instance -> 'b)
        (listener: IProbeListener option)
        (profiled: (Result<byte array, string> -> unit) option)
        (dbg: DbgHit -> bool)
        : Result<'a, RunError> * 'b option =
        // On a thread of its own, so that the guest's stack limit comes before the end of the thread's (see `ProgramStack`).
        ProgramStack.run <| fun () ->
        use engine = ProgramStack.engine (if profiled.IsSome then GuestProfiler.Configure else id)
        match compile engine program with
        | Error err -> Error err, None
        | Ok module' ->
            use module' = module'
            use linker = new Linker(engine)
            use store = new Store(engine)
            let profiler = profiled |> Option.map (fun _ -> GuestProfiler(engine, module', profileInterval))
            profiler |> Option.iter (fun profiler -> profiler.Watch store)
            linker.DefineWasi()
            let waits = AsyncImport.Waits()
            AsyncImport.define linker waits
            HttpImport.define linker waits
            Probing.define linker (spanTable program) listener
            Probing.defineDbg linker dbg
            let reported = ref None
            CrashImport.defineWith linker reported (ref false) (fun message -> listener |> Option.iter (fun listener -> listener.Crashed message))
            configure store
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
                | :? WasmtimeException as ex -> Error(trapped (spanTable program) reported.Value ex)
            // The profile is of the whole run, however it ended.
            match profiled, profiler with
            | Some give, Some profiler -> give (profiler.Finish() |> Result.map (ProfilePlaces.withDefinitions (spanTable program)))
            | _ -> ()
            outcome, instantiated |> Option.map inspect

    /// `executeIn`, with the program's standard streams `io`, its command-line arguments `arguments`, its name first, and
    /// the directories it may reach, `directories`, its current directory first (see `configureIo`).
    let private executeInspecting
        (io: ProgramIo)
        (arguments: string list)
        (directories: string list)
        (program: Program)
        (start: string option)
        (read: Instance option -> 'a)
        (inspect: Instance -> 'b)
        (listener: IProbeListener option)
        (profiled: (Result<byte array, string> -> unit) option)
        (dbg: DbgHit -> bool)
        : Result<'a, RunError> * 'b option =
        let mutable finish = ignore
        try
            executeIn (fun store -> finish <- configureIo io arguments directories store) program start read inspect listener profiled dbg
        finally
            finish ()

    let private execute (io: ProgramIo) (arguments: string list) (directories: string list) (program: Program) (start: string option) (read: Instance option -> 'a) : Result<'a, RunError> =
        executeInspecting io arguments directories program start read ignore None None Probing.leftToProgram |> fst

    /// The value of the exported global `name` of `instance`, an `i32` or an `i64`: 0 when it does not export it.
    let private globalValue (instance: Instance) (name: string) : int64 =
        match instance.GetGlobal name with
        | null -> 0L
        | global' ->
            match global'.GetValue() with
            | :? int64 as value -> value
            | value -> int64 (value :?> int)

    /// Instantiates (and therefore runs) the module described by `wat`, with its standard streams `io`, its
    /// command-line arguments `arguments`, its name first, and the directories it may read and write, `directories`, its
    /// current directory first: none for a program that reaches no files.
    let run (io: ProgramIo) (arguments: string list) (directories: string list) (wat: string) : Result<unit, RunError> =
        execute io arguments directories (Text wat) None ignore

    /// `run` for a module in the binary format, `wasm`.
    let runBinary (io: ProgramIo) (arguments: string list) (directories: string list) (wasm: byte array) : Result<unit, RunError> =
        execute io arguments directories (Binary wasm) None ignore

    /// `runBinary`, with each `dbg` the program reaches given to `dbg`, which says whether it dealt with it: one it didn't,
    /// the program writes to its stderr (see `Probing.defineDbg`). `nanyx run --dbg-to` writes them to a channel.
    let runBinaryWithDbg (io: ProgramIo) (arguments: string list) (directories: string list) (wasm: byte array) (dbg: DbgHit -> bool) : Result<unit, RunError> =
        executeInspecting io arguments directories (Binary wasm) None ignore ignore None None dbg |> fst

    /// `runBinary`, with the program's debug probes answered by `listener` (see `IProbeListener`).
    let runListening (io: ProgramIo) (arguments: string list) (directories: string list) (wasm: byte array) (listener: IProbeListener) : Result<unit, RunError> =
        executeInspecting io arguments directories (Binary wasm) None ignore ignore (Some listener) None Probing.leftToProgram |> fst

    /// `runBinary`, with Wasmtime's guest profiler sampling the run every `profileInterval`, giving `profiled` the profile,
    /// in the Firefox profiler's format, once it has stopped, or why it couldn't be written (see `GuestProfiler`).
    let runProfiled (io: ProgramIo) (arguments: string list) (directories: string list) (wasm: byte array) (profiled: Result<byte array, string> -> unit) (dbg: DbgHit -> bool) : Result<unit, RunError> =
        executeInspecting io arguments directories (Binary wasm) None ignore ignore None (Some profiled) dbg |> fst

    /// What `runCounting` gives, for any `Program`.
    let private counting (io: ProgramIo) (arguments: string list) (directories: string list) (start: string) (globals: string array) (dbg: DbgHit -> bool) (program: Program) =
        executeInspecting
            io
            arguments
            directories
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
            None
            None
            dbg

    /// Runs the module described by `wat`, which exports its start function as `start` rather than running it when
    /// instantiated, and gives the values of its exported `i32` globals `globals` once `start` has returned, as
    /// `runReading` does, and the `HeapCounters` the program left, with `globals` read from them, however it stopped:
    /// `None` only when the module was never instantiated.
    let runCounting
        (io: ProgramIo)
        (arguments: string list)
        (directories: string list)
        (start: string)
        (globals: string array)
        (wat: string)
        : Result<int array, RunError> * HeapCounters option =
        counting io arguments directories start globals Probing.leftToProgram (Text wat)

    /// `runCounting` for a module in the binary format, `wasm`, with each `dbg` it reaches given to `dbg` (see
    /// `runBinaryWithDbg`).
    let runCountingBinary
        (io: ProgramIo)
        (arguments: string list)
        (directories: string list)
        (start: string)
        (globals: string array)
        (dbg: DbgHit -> bool)
        (wasm: byte array)
        : Result<int array, RunError> * HeapCounters option =
        counting io arguments directories start globals dbg (Binary wasm)

    /// Runs the module described by `wat`, which exports its start function as `start` rather than running it when
    /// instantiated, and gives the values of its exported `i32` globals `globals` once `start` has returned. A global
    /// the module does not export counts as 0: a module that never allocates has no allocation counters. So does every
    /// global of a program that ended itself with exit code 0, which leaves what it allocated as it is.
    let runReading (io: ProgramIo) (arguments: string list) (directories: string list) (start: string) (globals: string array) (wat: string) : Result<int array, RunError> =
        runCounting io arguments directories start globals wat |> fst

    /// `runReading` for a module in the binary format, `wasm`.
    let runBinaryReading (io: ProgramIo) (arguments: string list) (directories: string list) (start: string) (globals: string array) (wasm: byte array) : Result<int array, RunError> =
        counting io arguments directories start globals Probing.leftToProgram (Binary wasm) |> fst
