using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Nanyx.Runtime;
using NyxCompiler;
using Transpiler.Wasm;

/// <summary>
/// Running a compiled program for <c>nanyx run</c>: lowering it, running or serving it, and saying what stopped it.
/// </summary>
internal static partial class CliCommands
{
    /// <summary>
    /// Runs a compiled program with the shell's standard streams and the command-line arguments
    /// <paramref name="arguments"/>, its name first, reporting what stops it to the shell's stderr, and gives the exit
    /// code. The program may read and write the shell's directory and each of <paramref name="allowedDirectories"/>, full
    /// paths. A program whose host answers HTTP requests is served on <paramref name="port"/> instead (see
    /// <see cref="Serve"/>). A package's program names its files from <paramref name="packageRoot"/> where it says
    /// where it is, as <c>dbg</c> does. With <paramref name="profile"/>, Wasmtime's guest profiler samples the run and the
    /// profile is written to that file, in the Firefox profiler's format.
    /// </summary>
    private static int RunProgram(Shell shell, CompileResult result, bool errorsDeferred = false, MemoryCheck memoryCheck = MemoryCheck.Off, bool timings = false, string[]? arguments = null, bool inlineCalls = true, string[]? allowedDirectories = null, int port = 3000, string? packageRoot = null, string? profile = null, bool yieldAtEveryWait = false)
    {
        var programArguments = ListModule.OfSeq(arguments ?? ["nanyx"]);
        var directories = ListModule.OfSeq<string>([shell.Directory, .. allowedDirectories ?? []]);
        var errors = shell.Error;
        // Without debug probes: nothing listens to them on a run, and they made most of a program's code. The debugger
        // will compile with them when it attaches. A dbg still gives what it prints to the runtime, which the console
        // runner leaves to the program and a server writes to its log.
        var options = new CodeGen.WasmOptions(memoryCheck != MemoryCheck.Off, inlineCalls, false, SourceRoot(packageRoot), true, false, crashToRuntime: true, dbgToEmbedder: true, exportStart: false, component: null, componentExports: FSharpList<Tuple<string, ComponentFunction>>.Empty, yieldAtEveryWait: yieldAtEveryWait, frameDrops: false);
        if (!TryEmitWat(shell, result, errors, out var wat, options, errorsDeferred, timings))
        {
            return 1;
        }

        // The program runs as the compiler assembled it, so that the frames of a trap are where its table of places
        // says (see `SpanTable`).
        if (!TryAssemble(errors, wat, out var wasm))
        {
            return 1;
        }

        // The program's output follows the run's box, unmarked, and so does what the run reports after it.
        (errors as RunBox.Writer)?.Box.Close("output");
        if (WasmServer.serves(wat))
        {
            if (profile is not null)
            {
                errors.WriteLine("error: --profile profiles a program that runs once, and this one is served: profiling a served program isn't supported yet.");
                return UsageError;
            }

            return Serve(shell, programArguments, wasm, port, errors);
        }

        if (profile is not null)
        {
            var profiled = WasmRunner.runProfiled(shell.Io, programArguments, directories, wasm, FuncConvert.FromAction<FSharpResult<byte[], string>>(written => WriteProfile(shell, profile, written, errors)));
            return profiled.IsOk ? 0 : ReportRunError(profiled.ErrorValue, errors);
        }

        if (memoryCheck == MemoryCheck.Off)
        {
            var outcome = WasmRunner.runBinary(shell.Io, programArguments, directories, wasm);
            return outcome.IsOk ? 0 : ReportRunError(outcome.ErrorValue, errors);
        }

        var (counted, counters) = WasmRunner.runCountingBinary(shell.Io, programArguments, directories, CodeGen.CheckedExports.start, MemoryReport.Counters, wasm);
        var exitCode = counted.IsError ? ReportRunError(counted.ErrorValue, errors) : CheckNothingLeft(counted.ResultValue, errors);
        // The report follows what stopped the program, or the leak the check found.
        if (memoryCheck == MemoryCheck.Report && counters is not null)
        {
            MemoryReport.Write(errors, counters.Value);
        }

        return exitCode;
    }

    /// <summary>The root a program names its files from (see <c>WasmOptions.SourceRoot</c>): none for a script.</summary>
    private static FSharpOption<string> SourceRoot(string? packageRoot) =>
        packageRoot is null ? FSharpOption<string>.None : FSharpOption<string>.Some(packageRoot);

    /// <summary>
    /// Writes the profile <paramref name="written"/> of a run to <paramref name="path"/>, making its directory if need be,
    /// and says where to <paramref name="errors"/>, or why there is none.
    /// </summary>
    private static void WriteProfile(Shell shell, string path, FSharpResult<byte[], string> written, TextWriter errors)
    {
        if (written.IsError)
        {
            errors.WriteLine($"error: the run's profile couldn't be written: {written.ErrorValue}");
            return;
        }

        var fullPath = shell.FullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, written.ResultValue);
        errors.WriteLine($"Profile written: {shell.DisplayPath(fullPath)} (open it at https://profiler.firefox.com)");
    }

    /// <summary>
    /// The exit code of a program run with memory checks that returned, given its <see cref="MemoryReport.Counters"/>:
    /// 1 when it left blocks on the heap, which it says to <paramref name="errors"/>.
    /// </summary>
    private static int CheckNothingLeft(int[] counters, TextWriter errors)
    {
        int Counter(string name) => counters[Array.IndexOf(MemoryReport.Counters, name)];
        var live = Counter(CodeGen.CheckedExports.liveBlocks);
        if (live == 0)
        {
            return 0;
        }

        errors.WriteLine(
            $"Memory check failed: {live} of the {Counter(CodeGen.CheckedExports.allocations)} blocks the program allocated were never freed");
        return 1;
    }

    /// <summary>
    /// Serves the program <paramref name="wasm"/>, whose host answers HTTP requests, on <paramref name="port"/> until the
    /// shell's <see cref="Shell.Stop"/> is cancelled or, for the process's own, Ctrl+C is pressed. Each request is
    /// answered by a fresh instance of it; one that stops without answering is a 500, which is said on
    /// <paramref name="errors"/>, and the server carries on.
    /// </summary>
    private static int Serve(Shell shell, FSharpList<string> arguments, byte[] wasm, int port, TextWriter errors)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(shell.Stop);
        ConsoleCancelEventHandler interrupted = (_, pressed) =>
        {
            pressed.Cancel = true;
            stopping.Cancel();
        };
        var own = shell.Io.IsInherited;
        if (own)
        {
            Console.CancelKeyPress += interrupted;
        }

        try
        {
            var gate = new object();
            void Report(string line)
            {
                lock (gate)
                {
                    errors.WriteLine(line);
                }
            }

            var outcome = WasmServer.serve(
                shell.Io,
                arguments,
                Program.NewBinary(wasm),
                port,
                stopping.Token,
                FuncConvert.FromAction<string>(address => Report($"Serving {address} (Ctrl+C to stop)")),
                FuncConvert.FromAction<string>(Report));
            return outcome.IsOk ? 0 : ReportRunError(outcome.ErrorValue, errors);
        }
        finally
        {
            if (own)
            {
                Console.CancelKeyPress -= interrupted;
            }
        }
    }

    /// <summary>The tour's section on tail recursion, which a program that ran out of stack is pointed to.</summary>
    private const string TailRecursionDocs = "https://nanyx.dev/docs/tour/functions#tail-recursion";

    private static int ReportRunError(RunError error, TextWriter errors)
    {
        switch (error)
        {
            case RunError.InvalidModule invalid:
                errors.WriteLine($"Internal error in the compiler, whose WebAssembly is invalid ({invalid.Item}): report it at https://github.com/nanyx-lang/nanyx/issues");
                return 1;
            case RunError.Trapped trapped:
                errors.WriteLine($"Runtime error: {trapped.message}{WasmRunner.stackText(trapped.stack)}");
                return 1;
            // The program said why it stopped before it trapped, so only the stack follows.
            case RunError.Crashed crashed:
                if (!crashed.stack.IsEmpty)
                {
                    errors.WriteLine(WasmRunner.stackText(crashed.stack).TrimStart('\n'));
                }

                return 1;
            case var _ when error.IsOutOfMemory:
                errors.WriteLine("Runtime error: out of memory");
                return 1;
            case RunError.StackOverflow overflow:
                var where = FSharpOption<string>.get_IsSome(overflow.functionName) ? $" in '{overflow.functionName.Value}'" : "";
                errors.WriteLine(
                    $"Runtime error: the program ran out of stack{where}: a call that isn't a tail call keeps its caller's stack until it returns, so a function that calls itself that way uses stack for every call (see {TailRecursionDocs})");
                return 1;
            case RunError.ArithmeticError arithmetic:
                // The stack names the function, when there is one.
                var function = arithmetic.stack.IsEmpty ? arithmetic.functionName : FSharpOption<string>.None;
                errors.WriteLine($"Runtime error: {WasmRunner.describeFault(arithmetic.fault, function)}{WasmRunner.stackText(arithmetic.stack)}");
                return 1;
            case RunError.CannotServe cannot:
                errors.WriteLine($"error: the program can't be served on port {cannot.port}: {cannot.reason}. Give the host another `port` in its `settings`");
                return 1;
            // The program said why it stopped (a deferred error writes itself to stderr) before it exited.
            case RunError.Exited exited:
                return exited.Item;
            default:
                errors.WriteLine($"Run failed: {error}");
                return 1;
        }
    }
}
