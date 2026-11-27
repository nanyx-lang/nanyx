using Consolo;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Nanyx.Runtime;
using NyxCompiler;
using System.Globalization;
using System.Text.RegularExpressions;
using Transpiler.Wasm;

/// <summary>
/// <c>nanyx test</c>: builds the tests of a package or a script as a test build, under the test host, and runs each in
/// an instance of its own (see <see cref="WasmTester"/>), reporting each as it ends.
/// </summary>
internal static partial class CliCommands
{
    /// <summary>
    /// Run the tests of a package, the defs marked @test, beside its code and in the files its manifest's tests field selects, or of a script, whose top-level statements are not run.
    /// </summary>
    /// <param name="path">A .nyx script, or a package's directory or .nyxp manifest; none means the package the current directory is in.</param>
    /// <param name="filter">Run only the tests whose full name, as geometry.`adds points`, contains this text.</param>
    /// <param name="seed">The seed of each test's Random, as random.seeded takes one. Without it, a seed is chosen at random, and printed when a test fails, so that a run can be repeated.</param>
    /// <param name="timeout">How many seconds a test may run before it is stopped, and fails; 10 by default.</param>
    /// <param name="release">Refuse any error. Without it, type and name errors are deferred, as nanyx run defers them.</param>
    /// <param name="timings">Write how long each module took to check to stderr, as a tree of the modules each one imported. On a terminal, the tree is shown live while the compile runs.</param>
    [Consolo("test")]
    public static int Test(
        [Consolo("filter")] string? filter = null,
        [Consolo("seed")] string? seed = null,
        [Consolo("timeout")] string? timeout = null,
        [Consolo("release")] bool release = false,
        [Consolo("timings")] bool timings = false,
        params string[] path) =>
        TestIn(Shell.Process, path, filter, seed, timeout, release, timings);

    /// <summary>What <c>nanyx test</c> does, run in <paramref name="shell"/>.</summary>
    internal static int TestIn(Shell shell, string[] path, string? filter = null, string? seed = null, string? timeout = null, bool release = false, bool timings = false)
    {
        if (!TrySinglePath(shell, path, out var single))
        {
            return UsageError;
        }

        long? givenSeed = null;
        if (seed is not null)
        {
            if (!long.TryParse(seed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
            {
                shell.Error.WriteLine($"error: --seed is a whole number, as a failed run prints it, not '{seed}'");
                return UsageError;
            }

            givenSeed = parsed;
        }

        var limit = WasmTester.defaultTimeout;
        if (timeout is not null)
        {
            if (!double.TryParse(timeout, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
            {
                shell.Error.WriteLine($"error: --timeout is how many seconds a test may run, a number more than 0, not '{timeout}'");
                return UsageError;
            }

            limit = TimeSpan.FromSeconds(seconds);
        }

        string name;
        string? packageRoot;
        string directory;
        FSharpList<Diagnostic> diagnostics;
        bool deferred;
        FSharpOption<CompileResult> root;
        if (single is not null && !IsPackagePath(shell, single))
        {
            if (!File.Exists(shell.FullPath(single)))
            {
                shell.Error.WriteLine($"There is no file or directory '{single}'");
                return UsageError;
            }

            if (!TryPullScript(shell, single))
            {
                return 1;
            }

            name = shell.DisplayPath(shell.FullPath(single));
            packageRoot = null;
            directory = Path.GetDirectoryName(shell.FullPath(single))!;
            using var progress = CompileProgress.Start(shell.Error, $"{name} tests", shell.DisplayPath, timings);
            var built = Script.testBuild(shell.Packages, progress.Listener, shell.FullPath(single), File.ReadAllText(shell.FullPath(single)));
            progress.Finish();
            if (built.IsError)
            {
                PrintDiagnostics(shell, built.ErrorValue, shell.Error);
                return 1;
            }

            var (script, scriptRoot) = built.ResultValue;
            root = scriptRoot;
            diagnostics = root is null ? script.Diagnostics : root.Value.Diagnostics;
            deferred = ErrorsDeferred(root is null ? script : root.Value);
        }
        else
        {
            if (!TryUpdateLockfile(shell, single, frozen: false, out var failed) || !TryFindPackage(shell, single, out var package, out failed))
            {
                return failed;
            }

            var built = PackageModule.testBuild(shell.Packages, package);
            if (built.IsError)
            {
                shell.Error.WriteLine($"error: the test host can't be read: {built.ErrorValue}");
                return 1;
            }

            name = package.Manifest.Name;
            packageRoot = package.Manifest.Root;
            directory = package.Manifest.Root;
            using var progress = CompileProgress.Start(shell.Error, $"{name} tests", shell.DisplayPath, timings);
            var build = built.ResultValue;
            root = PackageModule.testResult(build);
            progress.Finish();
            // A module's errors are reported once, though the root, which imports it, reports them too (see PackageModule.buildReporting).
            diagnostics = root is null ? build.Diagnostics : ListModule.Distinct(ListModule.Map(FuncConvert.FromFunc<Diagnostic, Diagnostic>(NyxCompiler.Diagnostics.inItsModule), ListModule.Append(build.Diagnostics, root.Value.Diagnostics)));
            deferred = ErrorsDeferred(build);
        }

        PrintDiagnostics(shell, WorthStoppingFor(diagnostics), shell.Error);
        if (HasErrors(diagnostics) && (release || !deferred || root is { Value.Typed: null }))
        {
            return 1;
        }

        if (root is null)
        {
            shell.Out.WriteLine($"{name} has no tests: mark a def @test to make it one");
            return 0;
        }

        // As nanyx run compiles a program, with probes for a debug build's stacks; the test build gives its dbgs and
        // crashes to the runtime itself (see TestBuild.assemble).
        var options = new CodeGen.WasmOptions(false, true, release, SourceRoot(packageRoot), true, !release, crashToRuntime: true, dbgToEmbedder: true, exportStart: false, component: null, componentExports: FSharpList<Tuple<string, ComponentFunction>>.Empty, yieldAtEveryWait: false, frameDrops: false);
        var wasm = TestBuild.assemble(options, root.Value.Typed.Value);
        var chosenSeed = givenSeed ?? Random.Shared.NextInt64();
        var tests = TestTable.read(wasm);
        var selected = tests.Where(test => filter is null || Shown(test).Contains(filter, StringComparison.Ordinal)).ToList();
        shell.Out.WriteLine($"Running {Count(selected.Count, "test")} in {name}{WithDeferred(HasErrors(diagnostics), diagnostics)}");
        shell.Out.WriteLine();
        var passed = 0;
        var runs = WasmTester.run(
            wasm,
            directory,
            EnvironmentOf(shell),
            FuncConvert.FromFunc<ListedTest, bool>(selected.Contains),
            chosenSeed,
            limit,
            FuncConvert.FromAction<TestRun>(run =>
            {
                if (run.Outcome.IsPassed)
                {
                    passed++;
                }

                Report(shell.Out, run);
            }));
        if (runs.IsError)
        {
            return ReportRunError(runs.ErrorValue, shell.Error);
        }

        var failedCount = selected.Count - passed;
        shell.Out.WriteLine();
        shell.Out.WriteLine(failedCount == 0 ? $"{passed} passed" : $"{passed} passed, {failedCount} failed");
        if (failedCount > 0)
        {
            shell.Out.WriteLine($"Seed {chosenSeed}: run them again with the same random numbers with --seed {chosenSeed}");
        }

        return failedCount == 0 ? 0 : 1;
    }

    /// <summary>"1 test", "3 tests".</summary>
    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>
    /// What a name is written as in a test's full name: a name that is a plain identifier, letters, digits and
    /// underscores that don't start with a digit and aren't a reserved word, as it is, and any other, such as a sentence,
    /// in backticks, as it is written in the source.
    /// </summary>
    private static string WrittenName(string name) =>
        PlainIdentifier.IsMatch(name) && !Parser.Program.Keywords.reserved.Contains(name) ? name : $"`{name}`";

    private static readonly Regex PlainIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$");

    /// <summary>A test's full name as the runner shows it and filters it: its module's name, then its own, as written:
    /// geometry.`adds points`.</summary>
    private static string Shown(ListedTest test) => $"{test.Module}.{WrittenName(test.Name)}";

    /// <summary>The environment variables a test marked <c>@test(#io)</c> sees: the shell's.</summary>
    private static FSharpList<Tuple<string, string>> EnvironmentOf(Shell shell) =>
        shell.Io is ProgramIo.Redirected redirected
            ? redirected.variables
            : ListModule.OfSeq(Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().Select(entry => Tuple.Create((string)entry.Key, (string?)entry.Value ?? "")));

    /// <summary>" (io)" after the name of a test marked <c>@test(#io)</c>, which ran with the console host's real
    /// contexts; nothing after any other's.</summary>
    private static string Io(ListedTest test) => test.Io ? " (io)" : "";

    /// <summary>" (crashes)" after the name of a test marked <c>@test(#crashes)</c> that passed, by crashing as it
    /// expected (#3078); nothing after any other's.</summary>
    private static string Crashes(TestRun run) => run.Outcome.IsPassed && FSharpOption<Nanyx.Runtime.ExpectedCrash>.get_IsSome(run.Test.Crashes) ? " (crashes)" : "";

    /// <summary>
    /// Writes how <paramref name="run"/> ended to <paramref name="writer"/>: PASS and its name, or FAIL, its name and
    /// where it is written, then why it failed, and what it printed, indented under it.
    /// </summary>
    private static void Report(TextWriter writer, TestRun run)
    {
        if (run.Outcome.IsPassed)
        {
            writer.WriteLine($"PASS {Shown(run.Test)}{Io(run.Test)}{Crashes(run)}");
            return;
        }

        writer.WriteLine($"FAIL {Shown(run.Test)}{Io(run.Test)}  {run.Test.File}:{run.Test.Line}:{run.Test.Column}");
        var why = new StringWriter();
        if (run.Outcome is TestOutcome.CrashedOtherwise expectedOther)
        {
            why.WriteLine($"It was expected to crash with a message that contains \"{expectedOther.expected}\", and crashed with another:");
        }

        why.Write(CrashPlace(run));
        why.Write(run.Errors);
        switch (run.Outcome)
        {
            case TestOutcome.TimedOut timedOut:
                why.WriteLine($"It ran for longer than {timedOut.Item.TotalSeconds:0.###} seconds, and was stopped: give it longer with --timeout");
                break;
            case TestOutcome.Failed failed:
                ReportRunError(failed.Item, why);
                break;
            case var outcome when outcome.IsDidNotCrash:
                why.WriteLine("It finished, and was expected to crash");
                break;
            case TestOutcome.CrashedOtherwise otherwise:
                ReportRunError(otherwise.error, why);
                break;
        }

        WriteIndented(writer, why.ToString(), "  ");
        if (run.Output.Length > 0)
        {
            writer.WriteLine("  Output:");
            WriteIndented(writer, run.Output, "    ");
        }
    }

    /// <summary>
    /// Where the program's code that <paramref name="run"/> crashed in is, <c>src/geometry.nyx:14:5: </c>, to start what
    /// it wrote with, as a crash's report gives it (#2950); nothing for a run that didn't crash with a place, or one whose
    /// message starts with a place already, as an <c>assert</c>'s does, and a deferred error's, which names where the
    /// error is, though its trap may be in code inlined where it was called.
    /// </summary>
    private static string CrashPlace(TestRun run)
    {
        var crashed = run.Outcome switch
        {
            TestOutcome.Failed { Item: RunError.Crashed crash } => crash,
            TestOutcome.CrashedOtherwise { error: RunError.Crashed crash } => crash,
            _ => null
        };
        if (crashed is { place: { } place } && FSharpOption<Tuple<string, int, int>>.get_IsSome(place) && !StartsWithPlace.IsMatch(run.Errors))
        {
            var (file, line, column) = place.Value;
            return column > 0 ? $"{file}:{line}:{column}: " : $"{file}:{line}: ";
        }

        return "";
    }

    /// <summary>A message that starts with a place, <c>src/geometry.nyx:14:5: </c>.</summary>
    private static readonly Regex StartsWithPlace = new(@"^[^\s:]*(:\d+){1,2}: ");

    /// <summary>Writes each line of <paramref name="text"/> to <paramref name="writer"/>, after <paramref name="indent"/>.</summary>
    private static void WriteIndented(TextWriter writer, string text, string indent)
    {
        foreach (var line in text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'))
        {
            if (line.Length > 0)
            {
                writer.WriteLine(indent + line);
            }
        }
    }
}
