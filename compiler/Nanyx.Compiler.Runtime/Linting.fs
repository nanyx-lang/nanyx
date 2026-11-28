namespace Nanyx.Runtime

open System
open System.Threading
open Wasmtime

/// Running a lint build, whose host imports the runtime's own `nanyx:lint` (the linter host, `nanyx.analysis.host`):
/// each run lints one module, in an instance of its own, asking the compiler about it. The data that crosses is in the
/// compiler's binary form (`NyxCompiler.LintData`), which this module only moves. The functions of `nanyx:lint`:
///
/// - `ask(question: i32, length: i32) -> i32`, the `length` bytes at `question` the question, and the length of its
///   answer back;
/// - `answer(at: i32)`, which writes the answer to the last question at `at`;
/// - `report(reports: i32, length: i32)`, the `length` bytes at `reports` the linter's reports.
module WasmLinter =
    /// The import module's name.
    [<Literal>]
    let moduleName = "nanyx:lint"

    /// How long a linter may take over one module before it is stopped.
    let defaultTimeout = TimeSpan.FromSeconds 10.0

    /// Defines `nanyx:lint` in `linker`, answering with `answer` and keeping what it reports in `reports`. A question
    /// `answer` can't answer traps, with why.
    let private define (linker: Linker) (answer: byte array -> Result<byte array, string>) (reports: byte array option ref) =
        let pending = ref Array.empty<byte>
        let read (memory: Memory) (address: int) (length: int) = memory.GetSpan(int64 address, length).ToArray()
        linker.DefineFunction(
            moduleName,
            "ask",
            CallerFunc<int, int, int>(fun caller address length ->
                match answer (read (caller.GetMemory "memory") address length) with
                | Ok bytes ->
                    pending.Value <- bytes
                    bytes.Length
                | Error message -> raise (WasmtimeException message))
        )
        linker.DefineFunction(
            moduleName,
            "answer",
            CallerAction<int>(fun caller address -> pending.Value.CopyTo((caller.GetMemory "memory").GetSpan(int64 address, pending.Value.Length)))
        )
        linker.DefineFunction(moduleName, "report", CallerAction<int, int>(fun caller address length -> reports.Value <- Some(read (caller.GetMemory "memory") address length)))

    /// Runs the lint build `module'`, compiled for `engine` with the epoch interruption that stops it after `timeout`,
    /// its places in `table`, on a thread with the stack a program runs with, answering its questions with `answer`.
    /// Gives the bytes of what it reports, or why it gave none.
    let runIn (engine: Engine) (module': Module) (table: SpanTable option) (timeout: TimeSpan) (answer: byte array -> Result<byte array, string>) : Result<byte array, RunError> =
        ProgramStack.run <| fun () ->
            let input = IO.Path.GetTempFileName()
            let printed = new IO.MemoryStream()
            let errors = new IO.MemoryStream()
            let mutable finish = ignore
            let reports = ref None
            let outcome =
                try
                    use linker = new Linker(engine)
                    use store = new Store(engine)
                    linker.DefineWasi()
                    define linker answer reports
                    AsyncImport.define linker (AsyncImport.Waits())
                    Probing.define linker table None
                    let reported = ref None
                    CrashImport.define linker reported (ref false)
                    finish <- WasmRunner.configureIo (Redirected(input, printed, errors, [])) [ "lint" ] [] store
                    store.SetEpochDeadline 1UL
                    use _ = new Timer((fun _ -> engine.IncrementEpoch()), null, timeout, Timeout.InfiniteTimeSpan)
                    try
                        linker.Instantiate(store, module') |> ignore
                        Ok ()
                    with
                    | :? WasmtimeException as ex when WasmRunner.exitedCleanly ex -> Ok ()
                    | :? TrapException as trap when trap.Type = TrapCode.Interrupt ->
                        Error(Trapped($"it took longer than {timeout.TotalSeconds} seconds", []))
                    | :? WasmtimeException as ex when ex.ExitCode.HasValue -> Error(Exited ex.ExitCode.Value)
                    | :? WasmtimeException as ex -> Error(WasmRunner.trapped table reported.Value ex)
                finally
                    finish ()
                    IO.File.Delete input
            match outcome, reports.Value with
            | Ok (), Some bytes -> Ok bytes
            | Ok (), None -> Error(Trapped("it finished without reporting", []))
            | Error error, _ -> Error error

    /// The lint build `wasm`, compiled once, as a function that lints a module: given the answers to its questions,
    /// it runs the build in an instance of its own, as `runIn` does, with `defaultTimeout`. Or why the build can't be run.
    let compile (wasm: byte array) : Result<(byte array -> Result<byte array, string>) -> Result<byte array, RunError>, RunError> =
        let engine = ProgramStack.engine (fun config -> config.WithEpochInterruption true)
        match WasmRunner.compile engine (Binary wasm) with
        | Error error -> Error error
        | Ok module' ->
            let table = SpanTable.read wasm
            Ok(fun answer -> runIn engine module' table defaultTimeout answer)
