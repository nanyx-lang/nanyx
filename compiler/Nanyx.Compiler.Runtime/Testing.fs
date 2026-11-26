namespace Nanyx.Runtime

open System
open System.Text
open System.Threading
open Wasmtime

/// How a test ended.
type TestOutcome =
    /// It finished.
    | Passed
    /// It stopped as a program stops (see `RunError`): it crashed, as an `assert` that doesn't hold does, it reached an
    /// error deferred to run time, which exits, or it trapped.
    | Failed of RunError
    /// It was still running when its time ran out, and was stopped.
    | TimedOut of TimeSpan

/// What running one test of a test build did.
type TestRun =
    { /// The test's index in the build (see `TestTable`).
      Index: int
      Test: ListedTest
      Outcome: TestOutcome
      /// What it printed through its `Console`, and each `dbg` it reached, in the order it wrote them.
      Output: string
      /// What it wrote to its standard error itself, as a crash's message and a deferred error are.
      Errors: string }

/// Running the tests of a test build, whose host imports the runtime's own `nanyx:test` (the test host,
/// `nanyx.test.host`). Each test runs in a fresh instance of the build, which runs the test at the index `nanyx:test`
/// gives it in its start function: a test that crashes ends only its own instance, and no state leaks from one test to
/// the next. The functions of `nanyx:test`:
///
/// - `index() -> i32`, the index of the test to run;
/// - `seed() -> i64`, the seed of the test's `Random`;
/// - `write(text: i32, length: i32)` and `writeError(text: i32, length: i32)`, the `length` bytes of UTF-8 at `text`
///   that the test writes to its output and its error output.
module WasmTester =
    /// The import module's name.
    [<Literal>]
    let moduleName = "nanyx:test"

    /// How long a test may run before it is stopped, unless the runner is given another limit.
    let defaultTimeout = TimeSpan.FromSeconds 10.0

    /// Defines `nanyx:test` in `linker`, for the test at `index` with `seed`, adding what it writes to `output`.
    let private define (linker: Linker) (index: int) (seed: int64) (output: StringBuilder) =
        linker.DefineFunction(moduleName, "index", Func<int>(fun () -> index))
        linker.DefineFunction(moduleName, "seed", Func<int64>(fun () -> seed))
        let write =
            CallerAction<int, int>(fun caller text length ->
                let written = (caller.GetMemory "memory").ReadString(int64 text, length)
                lock output (fun () -> output.Append(written) |> ignore))
        linker.DefineFunction(moduleName, "write", write)
        linker.DefineFunction(moduleName, "writeError", write)

    /// `stack`, the stack of a test that failed, innermost first, without the frames that ran the test rather than being
    /// it: those after the last frame in the file the test is written in, which are the build's entry point and the test
    /// host's. A stack with no frame in the test's file is left whole.
    let private withinTest (test: ListedTest) (stack: Frame list) =
        match stack |> List.tryFindIndexBack (fun frame -> frame.Place |> Option.exists (fun (file, _) -> file = test.File)) with
        | Some last -> List.take (last + 1) stack
        | None -> stack

    /// `error`, the reason a test failed, with its stack within the test (see `withinTest`).
    let private failedWithin (test: ListedTest) (error: RunError) =
        match error with
        | Crashed(message, place, stack) -> Crashed(message, place, withinTest test stack)
        | ArithmeticError(fault, functionName, stack) -> ArithmeticError(fault, functionName, withinTest test stack)
        | Trapped(message, stack) -> Trapped(message, withinTest test stack)
        | other -> other

    /// Runs the test at `index`, `test`, of the build `module'`, compiled for `engine` with the epoch interruption that
    /// stops it after `timeout`, its places in `table`, with `seed`, on a thread with the stack a program runs with.
    let private runOne (engine: Engine) (module': Module) (table: SpanTable option) (seed: int64) (timeout: TimeSpan) (index: int) (test: ListedTest) =
        ProgramStack.run <| fun () ->
            let output = StringBuilder()
            let input = IO.Path.GetTempFileName()
            let errors = new IO.MemoryStream()
            let mutable finish = ignore
            // The store holds the files WASI writes the test's standard streams to until it is disposed, and Windows
            // won't let a file that is open be read or deleted, so they are read and deleted once it is, after the try.
            let outcome =
                try
                    use linker = new Linker(engine)
                    use store = new Store(engine)
                    linker.DefineWasi()
                    define linker index seed output
                    Probing.define linker table None
                    Probing.defineDbg linker (fun hit ->
                        lock output (fun () -> output.Append(string hit).Append('\n') |> ignore)
                        true)
                    let reported = ref None
                    CrashImport.define linker reported
                    finish <- WasmRunner.configureIo (Redirected(input, IO.Stream.Null, errors, [])) [ test.FullName ] [] store
                    // The store is interrupted at the next epoch, which the timer starts once the test has had its time.
                    store.SetEpochDeadline 1UL
                    use _ = new Timer((fun _ -> engine.IncrementEpoch()), null, timeout, Timeout.InfiniteTimeSpan)
                    try
                        linker.Instantiate(store, module') |> ignore
                        Passed
                    with
                    | :? WasmtimeException as ex when WasmRunner.exitedCleanly ex -> Passed
                    | :? TrapException as trap when trap.Type = TrapCode.Interrupt -> TimedOut timeout
                    | :? WasmtimeException as ex when ex.ExitCode.HasValue -> Failed(Exited ex.ExitCode.Value)
                    | :? WasmtimeException as ex -> Failed(failedWithin test (WasmRunner.trapped table reported.Value ex))
                finally
                    finish ()
                    IO.File.Delete input
            { Index = index
              Test = test
              Outcome = outcome
              Output = lock output (fun () -> output.ToString())
              Errors = Encoding.UTF8.GetString(errors.ToArray()) }

    /// Runs the tests of the test build `wasm` (see `TestTable`) that `selected` chooses, one at a time and each in an
    /// instance of its own, with `seed` for their `Random`, stopping each that runs for longer than `timeout`, and gives
    /// each to `report` as it ends. Gives the runs, in the order of their indexes, or why the build can't be run.
    let run (wasm: byte array) (selected: ListedTest -> bool) (seed: int64) (timeout: TimeSpan) (report: TestRun -> unit) : Result<TestRun list, RunError> =
        use engine = ProgramStack.engine (fun config -> config.WithEpochInterruption true)
        match WasmRunner.compile engine (Binary wasm) with
        | Error error -> Error error
        | Ok module' ->
            use module' = module'
            let table = SpanTable.read wasm
            TestTable.read wasm
            |> List.indexed
            |> List.filter (snd >> selected)
            |> List.map (fun (index, test) ->
                let run = runOne engine module' table seed timeout index test
                report run
                run)
            |> Ok
