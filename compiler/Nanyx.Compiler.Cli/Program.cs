using Consolo;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Nanyx.Runtime;
using NyxCompiler;
using Transpiler.Wasm;

enum Target
{
    Parse,
    Check,
    Wasm,
    Run
}

/// <summary>
/// Where a command runs: the directory it reads relative paths from and shows paths relative to, the writers it reports
/// to, and the standard streams of a program it runs. The CLI's is the process's own (see <see cref="Process"/>); the
/// Wasm tests give their own, to run commands in their process.
/// </summary>
internal sealed record Shell(string Directory, TextWriter Out, TextWriter Error, ProgramIo Io)
{
    /// <summary>The process's current directory and its standard streams.</summary>
    public static Shell Process => new(System.IO.Directory.GetCurrentDirectory(), Console.Out, Console.Error, ProgramIo.Inherited);

    /// <summary>A path the user gave, made full from <see cref="Directory"/>.</summary>
    public string FullPath(string path) => Path.GetFullPath(path, Directory);

    /// <summary>A diagnostic's file, relative to <see cref="Directory"/> when it is under it.</summary>
    public string DisplayPath(string file)
    {
        if (string.IsNullOrEmpty(file) || !Path.IsPathRooted(file))
        {
            return file;
        }

        var relative = Path.GetRelativePath(Directory, file);
        return relative.StartsWith("..", StringComparison.Ordinal) ? file : relative;
    }
}

internal static class CliCommands
{
    /// <summary>
    /// Parse a Nanyx source file and print its syntax tree, without checking it.
    /// </summary>
    /// <param name="inputFile">The .nyx file to parse.</param>
    [Consolo("parse")]
    public static int Parse(string inputFile) => Execute(Shell.Process, inputFile, Target.Parse, outputPath: null);

    /// <summary>
    /// Check a Nanyx source file, or every source file of a package, and write nothing.
    /// </summary>
    /// <param name="path">A .nyx file, or a package's directory or .nyxp manifest; none means the package the current directory is in.</param>
    [Consolo("check")]
    public static int Check(params string[] path) => CheckIn(Shell.Process, path);

    /// <summary>What <c>nanyx check</c> does, run in <paramref name="shell"/>.</summary>
    internal static int CheckIn(Shell shell, string[] path)
    {
        if (!TrySinglePath(shell, path, out var single))
        {
            return UsageError;
        }

        if (single is not null && !IsPackagePath(shell, single))
        {
            return Execute(shell, single, Target.Check, outputPath: null);
        }

        if (!TryFindPackage(shell, single, out var package, out var failed))
        {
            return failed;
        }

        var build = BuildPackage(shell, package);
        PrintDiagnostics(shell, build.Diagnostics, shell.Out);
        shell.Out.WriteLine($"{Checked(package)}.");
        return HasErrors(build.Diagnostics) ? 1 : 0;
    }

    /// <summary>
    /// Make a new package: a .nyxp manifest and src/main.nyx, whose @entry prints a greeting.
    /// </summary>
    /// <param name="name">The package's name: lower case, with words separated by dots, as in acme.web.</param>
    /// <param name="directory">Where to make it, which must be new or empty; the default is the name's last segment.</param>
    [Consolo("new")]
    public static int New(string name, params string[] directory) => NewIn(Shell.Process, name, directory);

    /// <summary>What <c>nanyx new</c> does, run in <paramref name="shell"/>.</summary>
    internal static int NewIn(Shell shell, string name, string[] directory)
    {
        if (!TrySinglePath(shell, directory, out var single))
        {
            return UsageError;
        }

        var created = NewPackage.create(name, shell.FullPath(single ?? NewPackage.defaultDirectory(name)));
        if (created.IsError)
        {
            shell.Error.WriteLine(created.ErrorValue);
            return 1;
        }

        shell.Out.WriteLine($"Created {name}: {shell.DisplayPath(created.ResultValue)}");
        return 0;
    }

    /// <summary>
    /// Compile a Nanyx source file to WebAssembly text.
    /// </summary>
    /// <param name="inputFile">The .nyx file to compile.</param>
    /// <param name="outputPath">Where to write the WebAssembly text; the default is the input's name with .wat, beside it.</param>
    /// <param name="release">Refuse any error. Without it, type and name errors are deferred: the program is written, and reports one if it reaches it.</param>
    [Consolo("wasm")]
    public static int Wasm(
        string inputFile,
        [Consolo("out", Alias = "o")] string? outputPath = null,
        [Consolo("release")] bool release = false)
    {
        return WasmIn(Shell.Process, inputFile, outputPath, release);
    }

    /// <summary>What <c>nanyx wasm</c> does, run in <paramref name="shell"/>.</summary>
    internal static int WasmIn(Shell shell, string inputFile, string? outputPath, bool release) =>
        Execute(shell, inputFile, Target.Wasm, outputPath, release);

    /// <summary>
    /// Run a Nanyx program: a .nyx file, or the @entry of a package.
    /// </summary>
    /// <param name="path">A .nyx file to run as a script, or a package's directory or .nyxp manifest; none means the package the current directory is in.</param>
    /// <param name="checkMemory">Check the program's reference counting as it runs: stop at a block freed twice or an object used after it was freed, and fail if anything is left on the heap at the end.</param>
    [Consolo("run")]
    public static int Run(
        [Consolo("check-memory")] bool checkMemory = false,
        params string[] path)
    {
        return RunIn(Shell.Process, checkMemory, path);
    }

    /// <summary>What <c>nanyx run</c> does, run in <paramref name="shell"/>.</summary>
    internal static int RunIn(Shell shell, bool checkMemory, string[] path)
    {
        if (!TrySinglePath(shell, path, out var single))
        {
            return UsageError;
        }

        if (single is not null && !IsPackagePath(shell, single))
        {
            return Execute(shell, single, Target.Run, outputPath: null, checkMemory: checkMemory);
        }

        if (!TryFindPackage(shell, single, out var package, out var failed))
        {
            return failed;
        }

        // As for a script, stdout belongs to the program, so the build's diagnostics go to stderr.
        var build = BuildPackage(shell, package);
        PrintDiagnostics(shell, WorthStoppingFor(build.Diagnostics), shell.Error);
        var deferred = ErrorsDeferred(build);
        if (HasErrors(build.Diagnostics) && !deferred)
        {
            return 1;
        }

        var entry = PackageModule.entryResult(build);
        if (entry is null)
        {
            shell.Error.WriteLine($"The package {package.Manifest.Name} has no @entry, so there is nothing to run.");
            return 1;
        }

        ReportDeferredRun(shell, deferred, build.Diagnostics);
        return RunProgram(shell, entry.Value, deferred, checkMemory);
    }

    /// <summary>
    /// Build a Nanyx package: check every source file the manifest selects, and write its @entry program as WebAssembly text.
    /// </summary>
    /// <param name="path">The package's directory or .nyxp manifest; none means the package the current directory is in.</param>
    /// <param name="outputPath">Where to write the program; the default is out/&lt;package name&gt;.wat beside the manifest.</param>
    /// <param name="release">Refuse any error. Without it, type and name errors are deferred: the program is written, and reports one if it reaches it.</param>
    [Consolo("build")]
    public static int Build(
        [Consolo("out", Alias = "o")] string? outputPath = null,
        [Consolo("release")] bool release = false,
        params string[] path)
    {
        return BuildIn(Shell.Process, outputPath, release, path);
    }

    /// <summary>What <c>nanyx build</c> does, run in <paramref name="shell"/>.</summary>
    internal static int BuildIn(Shell shell, string? outputPath, bool release, string[] path)
    {
        if (!TrySinglePath(shell, path, out var single))
        {
            return UsageError;
        }

        if (!TryFindPackage(shell, single, out var package, out var failed))
        {
            return failed;
        }

        var build = BuildPackage(shell, package);
        PrintDiagnostics(shell, build.Diagnostics, shell.Out);
        var deferred = !release && ErrorsDeferred(build);
        if (HasErrors(build.Diagnostics) && !deferred)
        {
            return 1;
        }

        var entry = PackageModule.entryResult(build);
        if (entry is null)
        {
            // With no program to write, the build is only a check, and errors fail it.
            shell.Out.WriteLine($"{Checked(package)}; it has no @entry, so no program was written.");
            return deferred ? 1 : 0;
        }

        if (!TryEmitWat(shell, entry.Value, shell.Out, out var wat, CodeGen.WasmOptions.Default, deferred))
        {
            return 1;
        }

        var written = WriteProgram(shell, wat, string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(package.Manifest.Root, "out", package.Manifest.Name + ".wat")
            : outputPath!);
        shell.Out.WriteLine($"Built {package.Manifest.Name} ({FileCount(package)}){WithDeferred(deferred, build.Diagnostics)}: {written}");
        return 0;
    }

    /// <summary>
    /// The exit code of a command used wrongly: arguments missing or too many, an option's value not one it takes, or a
    /// path that does not exist. A command exits with 1 when the code it was given has errors or its program fails.
    /// </summary>
    private const int UsageError = 2;

    /// <summary>
    /// Writes <paramref name="wat"/> to <paramref name="outputPath"/>, making its directory if need be, and gives the path
    /// as the shell shows it.
    /// </summary>
    private static string WriteProgram(Shell shell, string wat, string outputPath)
    {
        var fullPath = shell.FullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, wat);
        return shell.DisplayPath(fullPath);
    }

    /// <summary>"Checked greeter (3 files)": what a package build that wrote nothing says.</summary>
    private static string Checked(Package package) => $"Checked {package.Manifest.Name} ({FileCount(package)})";

    /// <summary>
    /// The one path a command was given, or <c>null</c> for none. Paths are taken as a <c>params</c> array only so that
    /// the path can be left out and still be positional.
    /// </summary>
    private static bool TrySinglePath(Shell shell, string[] paths, out string? path)
    {
        path = paths.FirstOrDefault();
        if (paths.Length > 1)
        {
            shell.Error.WriteLine($"Give one file, directory or manifest, not {paths.Length}.");
            return false;
        }

        return true;
    }

    /// <summary>"1 file", "3 files": how many source files the package has.</summary>
    private static string FileCount(Package package) =>
        package.Sources.Length == 1 ? "1 file" : $"{package.Sources.Length} files";

    /// <summary>
    /// Builds the package, with a stopwatch on stderr naming the file the build is on while stderr is a terminal (see
    /// <see cref="CompileProgress"/>).
    /// </summary>
    private static PackageBuild BuildPackage(Shell shell, Package package)
    {
        using var progress = CompileProgress.Start(shell.Error, $"{package.Manifest.Name} ({FileCount(package)})");
        var build = PackageModule.buildWith(FuncConvert.FromAction<string>(file => progress.Compiling(shell.DisplayPath(file))), package);
        progress.Finish();
        return build;
    }

    /// <summary>
    /// Compiles a script, as <see cref="Script.compileFile"/> does, with a stopwatch on the shell's stderr while that is
    /// a terminal (see <see cref="CompileProgress"/>).
    /// </summary>
    private static CompileResult CompileScript(Shell shell, string inputFile)
    {
        using var progress = CompileProgress.Start(shell.Error, shell.DisplayPath(shell.FullPath(inputFile)));
        var result = Script.compileFile(shell.FullPath(inputFile));
        progress.Finish();
        return result;
    }

    /// <summary>Whether a path names a package (its directory or manifest) rather than a file to run as a script.</summary>
    private static bool IsPackagePath(Shell shell, string path) =>
        Directory.Exists(shell.FullPath(path)) || path.EndsWith(ManifestModule.extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The package a path names, or the one the shell's directory is in; otherwise reports why there is none, and gives
    /// the exit code in <paramref name="failed"/>.
    /// </summary>
    private static bool TryFindPackage(Shell shell, string? path, out Package package, out int failed)
    {
        package = null!;
        failed = UsageError;
        if (path is not null && !File.Exists(shell.FullPath(path)) && !Directory.Exists(shell.FullPath(path)))
        {
            shell.Error.WriteLine($"Not found: {path}");
            return false;
        }

        var found = PackageModule.find(path is null ? shell.Directory : shell.FullPath(path));
        if (found.IsError)
        {
            foreach (var error in found.ErrorValue)
            {
                shell.Error.WriteLine(error);
            }

            failed = 1;
            return false;
        }

        package = found.ResultValue;
        return true;
    }

    /// <summary>
    /// Compile and run a built-in "hello world" program, to check that the compiler works.
    /// </summary>
    [Consolo("hello")]
    public static int Hello()
    {
        var shell = Shell.Process;
        var result = Compiler.compile(HelloWorldSource);
        PrintDiagnostics(shell, result.Diagnostics, shell.Error);
        return RunProgram(shell, result);
    }

    /// <summary>
    /// Format Nanyx source files in the standard style.
    /// </summary>
    /// <param name="check">Write nothing; list the files that are not formatted, and exit with 1 if there are any.</param>
    /// <param name="write">Rewrite the files that are not formatted, which is what happens without --check too.</param>
    /// <param name="paths">Files or directories to format; a directory means every .nyx file under it, and none means the current directory.</param>
    [Consolo("format")]
    public static int Format(bool check = false, bool write = false, params string[] paths)
    {
        if (check && write)
        {
            Console.Error.WriteLine("Use --check or --write, not both.");
            return UsageError;
        }

        var shell = Shell.Process;
        var found = Nanyx.Formatter.FormatCommand.findFiles(shell.Directory, ListModule.OfSeq(paths));
        if (found.IsError)
        {
            Console.Error.WriteLine(found.ErrorValue);
            return UsageError;
        }

        var mode = check ? Nanyx.Formatter.FormatCommand.Mode.Check : Nanyx.Formatter.FormatCommand.Mode.Write;
        var results = Nanyx.Formatter.FormatCommand.run(Nanyx.Formatter.Settings.cached(), mode, found.ResultValue);

        // Every file under a manifest with bad settings fails the same way, so each message is printed once.
        var settingsErrors = new HashSet<string>();
        foreach (var (file, result) in results)
        {
            if (result.IsChanged && check)
            {
                Console.WriteLine(shell.DisplayPath(file));
            }
            else if (result is Nanyx.Formatter.FormatCommand.FileResult.Failed failed)
            {
                switch (failed.Item)
                {
                    case Nanyx.Formatter.FormatCommand.Formatting.NotParsed notParsed:
                        PrintDiagnostics(shell, ListModule.OfSeq([Compiler.syntaxError(notParsed.Item)]), Console.Error);
                        break;
                    case Nanyx.Formatter.FormatCommand.Formatting.NotSafe notSafe:
                        Console.Error.WriteLine($"{shell.DisplayPath(file)}: internal error: {notSafe.Item}; the file was left as it is.");
                        break;
                    // A directive's error is the file's own, and starts with where in it the directive is.
                    case Nanyx.Formatter.FormatCommand.Formatting.BadSettings badSettings when badSettings.Item.StartsWith(file + ":", StringComparison.Ordinal):
                        Console.Error.WriteLine($"{shell.DisplayPath(file)}{badSettings.Item[file.Length..]}; the file was left as it is.");
                        break;
                    case Nanyx.Formatter.FormatCommand.Formatting.BadSettings badSettings:
                        if (settingsErrors.Add(badSettings.Item))
                        {
                            Console.Error.WriteLine($"{badSettings.Item}; no file under it was formatted.");
                        }
                        break;
                }
            }
        }

        return Nanyx.Formatter.FormatCommand.exitCode(mode, results);
    }

    private const string HelloWorldSource = """
        import nanyx/console

        println("Hello, World!")
        """;

    /// <summary>
    /// Run the Nanyx language server over standard input and output (for editors; see docs/src/content/work-in-progress/language-server.md).
    /// </summary>
    /// <param name="stdio">Accepted and ignored: stdio is the only transport, but LSP clients conventionally pass it.</param>
    /// <param name="ascii">Write the types it shows in ASCII only: type variables a, b, ... rather than α, β, …, and a record's rest as ... rather than ….</param>
    /// <param name="topLevelTypeHints">Where to show top-level defs' inferred types: lens (above them, the default), inlay (after their names) or off.</param>
    /// <param name="noLocalTypeHints">Show no inlay hints for local defs' inferred types.</param>
    /// <param name="noParameterTypeHints">Show no inlay hints for lambda parameters' inferred types.</param>
    /// <param name="noPatternVariableTypeHints">Show no inlay hints for pattern variables' inferred types.</param>
    [Consolo("lsp")]
    public static int Lsp(
        [Consolo("stdio")] bool stdio = false,
        [Consolo("ascii")] bool ascii = false,
        [Consolo("top-level-type-hints")] string topLevelTypeHints = "lens",
        [Consolo("no-local-type-hints")] bool noLocalTypeHints = false,
        [Consolo("no-parameter-type-hints")] bool noParameterTypeHints = false,
        [Consolo("no-pattern-variable-type-hints")] bool noPatternVariableTypeHints = false)
    {
        Nanyx.LanguageServer.TopLevelTypeHints? topLevel = topLevelTypeHints switch
        {
            "lens" => Nanyx.LanguageServer.TopLevelTypeHints.Lens,
            "inlay" => Nanyx.LanguageServer.TopLevelTypeHints.Inlay,
            "off" => Nanyx.LanguageServer.TopLevelTypeHints.Off,
            _ => null
        };
        if (topLevel is null)
        {
            Console.Error.WriteLine($"--top-level-type-hints must be lens, inlay or off, not '{topLevelTypeHints}'.");
            return UsageError;
        }

        var settings = new Nanyx.LanguageServer.ServerSettings(
            ascii ? TypeNotation.Ascii : TypeNotation.Unicode,
            topLevel.Value,
            LocalTypeHints: !noLocalTypeHints,
            ParameterTypeHints: !noParameterTypeHints,
            PatternVariableTypeHints: !noPatternVariableTypeHints);
        Nanyx.LanguageServer.NanyxLanguageServer.RunStdioAsync(settings).GetAwaiter().GetResult();
        return 0;
    }

    /// <summary>
    /// Whether a debug build runs or writes a file's program in spite of its errors: it has errors, and each is a type
    /// or name error the compiler deferred, to be reported if the program reaches it.
    /// </summary>
    private static bool ErrorsDeferred(CompileResult result) =>
        result.Typed is not null && !result.TypeChecked && result.ErrorsDeferred;

    /// <summary>Whether a debug build runs or writes a package's program in spite of its errors, as for a file.</summary>
    private static bool ErrorsDeferred(PackageBuild build) => HasErrors(build.Diagnostics) && build.ErrorsDeferred;

    /// <summary>" with 2 deferred errors" when <paramref name="deferred"/>, to follow what a debug build says it did.</summary>
    private static string WithDeferred(bool deferred, FSharpList<Diagnostic> diagnostics) =>
        deferred ? $" with {DeferredErrors(diagnostics)}" : "";

    /// <summary>Says on stderr, before a debug run in spite of errors, how many there are.</summary>
    private static void ReportDeferredRun(Shell shell, bool deferred, FSharpList<Diagnostic> diagnostics)
    {
        if (deferred)
        {
            shell.Error.WriteLine($"Running{WithDeferred(deferred, diagnostics)}");
        }
    }

    /// <summary>"2 deferred errors": the errors among <paramref name="diagnostics"/>, every one of them deferred.</summary>
    private static string DeferredErrors(FSharpList<Diagnostic> diagnostics)
    {
        var count = diagnostics.Count(diagnostic => diagnostic.Severity.IsError);
        return $"{count} deferred error{(count == 1 ? "" : "s")}";
    }

    private static int Execute(Shell shell, string inputFile, Target target, string? outputPath, bool release = false, bool checkMemory = false)
    {
        if (string.IsNullOrWhiteSpace(inputFile))
        {
            shell.Error.WriteLine("Give a .nyx file.");
            return UsageError;
        }

        if (!File.Exists(shell.FullPath(inputFile)))
        {
            shell.Error.WriteLine($"File not found: {inputFile}");
            return UsageError;
        }

        if (target == Target.Parse)
        {
            return RunParse(shell, inputFile);
        }

        if (target == Target.Run)
        {
            return RunScript(shell, inputFile, checkMemory);
        }

        // Script mode: the file's :: directives say what a package's manifest would.
        var result = CompileScript(shell, inputFile);

        shell.Out.WriteLine($"Phase: {result.Phase}");
        PrintDiagnostics(shell, result.Diagnostics, shell.Out);

        return target switch
        {
            Target.Check => RunCheck(shell, result, inputFile),
            Target.Wasm => RunWasm(shell, result, inputFile, outputPath, release),
            _ => UnsupportedTarget(shell, target.ToString())
        };
    }

    /// <summary>
    /// What <c>nanyx run</c> does with the script <paramref name="inputFile"/>, which exists: compiles and runs it in
    /// <paramref name="shell"/>, and gives the exit code. The compiler's diagnostics and the run's own errors go to the
    /// shell's stderr. The Wasm tests call this to run programs in their own process.
    /// </summary>
    internal static int RunScript(Shell shell, string inputFile, bool checkMemory)
    {
        // Script mode: the file's :: directives say what a package's manifest would.
        var result = CompileScript(shell, inputFile);

        // `run` is the user-facing command: its stdout belongs to the program, so compiler chatter is kept off it.
        PrintDiagnostics(shell, WorthStoppingFor(result.Diagnostics), shell.Error);
        // A debug run: type and name errors the compiler deferred do not stop it. Each is reported again if the
        // program reaches it.
        var deferred = ErrorsDeferred(result);
        ReportDeferredRun(shell, deferred, result.Diagnostics);
        return RunProgram(shell, result, deferred, checkMemory);
    }

    private static int RunParse(Shell shell, string inputFile)
    {
        var parsed = Parser.Program.Api.parseModuleFile(shell.FullPath(inputFile), File.ReadAllText(shell.FullPath(inputFile)));

        if (parsed.IsError)
        {
            shell.Out.WriteLine($"Parse error: {parsed.ErrorValue}");
            return 1;
        }

        shell.Out.WriteLine("Parsed successfully:");
        foreach (var item in parsed.ResultValue)
        {
            shell.Out.WriteLine($"  {item}");
        }

        return 0;
    }

    /// <summary>
    /// The diagnostics `run` shows: errors and warnings. Info and hints are suggestions for when the code is being
    /// worked on, so `check` and the editor show them, but running a program does not.
    /// </summary>
    private static FSharpList<Diagnostic> WorthStoppingFor(FSharpList<Diagnostic> diagnostics) =>
        ListModule.OfSeq(diagnostics.Where(diagnostic => !diagnostic.Severity.IsInfo && !diagnostic.Severity.IsHint));

    /// <summary>
    /// Writes <paramref name="diagnostics"/> to <paramref name="writer"/>, one of <paramref name="shell"/>'s, with their
    /// files shown as <see cref="Shell.DisplayPath"/> shows them. None is said in so many words only on stdout.
    /// </summary>
    private static void PrintDiagnostics(Shell shell, FSharpList<Diagnostic> diagnostics, TextWriter writer)
    {
        if (diagnostics.IsEmpty)
        {
            if (writer == shell.Out)
            {
                writer.WriteLine("No diagnostics.");
            }

            return;
        }

        // As compilers usually write them, so editors and terminals can link to the place:
        // `path/to/file.nyx:3:9: error NYX0201: message`.
        foreach (var diagnostic in diagnostics)
        {
            var severity = diagnostic.Severity.ToString().ToLowerInvariant();
            var location = diagnostic.Span is null ? string.Empty : $"{FormatLocation(shell, diagnostic.Span.Value)}: ";
            writer.WriteLine($"{location}{severity} {diagnostic.Code}: {diagnostic.Message}");

            foreach (var related in diagnostic.Related)
            {
                writer.WriteLine($"  {FormatLocation(shell, related.Item1)}: {related.Item2}");
            }
        }
    }

    /// <summary>"path/to/file.nyx:3:9": where a span starts, its path as the shell shows it, with lines and columns from 1.</summary>
    private static string FormatLocation(Shell shell, Parser.Program.SourceSpan span) =>
        $"{shell.DisplayPath(span.File)}:{span.StartLine + 1}:{span.StartColumn + 1}";

    /// <summary>
    /// The type of each top-level def <paramref name="inputFile"/> declares, not the prelude's or an import's, in source
    /// order, written as hover writes it, with type variables <c>a</c>, <c>b</c>, ... since this is a terminal.
    /// </summary>
    private static void PrintTypes(Shell shell, string inputFile, CompileResult result)
    {
        var types = TypeHints.topLevelTypes(TypeNotation.Ascii, shell.FullPath(inputFile), result);
        if (types.IsEmpty)
        {
            shell.Out.WriteLine("(no definitions)");
            return;
        }

        foreach (var type in types)
        {
            shell.Out.WriteLine($"{type.Name} : {type.Type}");
        }
    }

    private static int RunCheck(Shell shell, CompileResult result, string inputFile)
    {
        if (result.Typed is null || !result.TypeChecked)
        {
            return 1;
        }

        shell.Out.WriteLine("Types:");
        PrintTypes(shell, inputFile, result);

        // The module type-checked, but a linter rule configured as an error fails the check all the same.
        return HasErrors(result.Diagnostics) ? 1 : 0;
    }

    private static int RunWasm(Shell shell, CompileResult result, string inputFile, string? outputPath, bool release)
    {
        var deferred = !release && ErrorsDeferred(result);
        if (!TryEmitWat(shell, result, shell.Out, out var wat, CodeGen.WasmOptions.Default, deferred))
        {
            return 1;
        }

        var written = WriteProgram(shell, wat, string.IsNullOrWhiteSpace(outputPath) ? DefaultWasmOutputPath(inputFile) : outputPath!);
        shell.Out.WriteLine($"WASM (WAT) emitted{WithDeferred(deferred, result.Diagnostics)}: {written}");
        return 0;
    }

    /// <summary>
    /// Runs a compiled program with the shell's standard streams, reporting what stops it to the shell's stderr, and
    /// gives the exit code.
    /// </summary>
    private static int RunProgram(Shell shell, CompileResult result, bool errorsDeferred = false, bool checkMemory = false)
    {
        var errors = shell.Error;
        var options = new CodeGen.WasmOptions(checkMemory);
        if (!TryEmitWat(shell, result, errors, out var wat, options, errorsDeferred))
        {
            return 1;
        }

        if (!checkMemory)
        {
            var outcome = WasmRunner.run(shell.Io, wat);
            return outcome.IsOk ? 0 : ReportRunError(outcome.ErrorValue, errors);
        }

        var counted = WasmRunner.runReading(
            shell.Io,
            CodeGen.CheckedExports.start,
            new[] { CodeGen.CheckedExports.liveBlocks, CodeGen.CheckedExports.allocations },
            wat);
        if (counted.IsError)
        {
            return ReportRunError(counted.ErrorValue, errors);
        }

        var (live, allocated) = (counted.ResultValue[0], counted.ResultValue[1]);
        if (live == 0)
        {
            return 0;
        }

        errors.WriteLine(
            $"Memory check failed: {live} of the {allocated} blocks the program allocated were never freed.");
        return 1;
    }

    private static int ReportRunError(RunError error, TextWriter errors)
    {
        switch (error)
        {
            case RunError.InvalidModule invalid:
                errors.WriteLine($"Internal compiler error: the generated WebAssembly is invalid: {invalid.Item}");
                return 1;
            case RunError.Trapped trapped:
                errors.WriteLine($"Runtime error: {trapped.Item}");
                return 1;
            // The program said why it stopped (a deferred error writes itself to stderr) before it exited.
            case RunError.Exited exited:
                return exited.Item;
            default:
                errors.WriteLine($"Run failed: {error}");
                return 1;
        }
    }

    /// <summary>
    /// Lowers a successfully type-checked program to WebAssembly text with <paramref name="options"/>, reporting failures
    /// to <paramref name="errors"/>. With <paramref name="errorsDeferred"/>, a program whose errors were all deferred to
    /// traps is lowered too.
    /// </summary>
    private static bool TryEmitWat(
        Shell shell, CompileResult result, TextWriter errors, out string wat, CodeGen.WasmOptions options, bool errorsDeferred = false)
    {
        wat = string.Empty;

        if (result.Typed is null || (HasErrors(result.Diagnostics) && !errorsDeferred))
        {
            return false;
        }

        // What the backend cannot compile is reported where it is written, like any other error.
        var emitted = CodeGen.tryTranspileTypedModuleWith(options, result.Typed.Value);
        if (emitted.IsError)
        {
            PrintDiagnostics(shell, ListModule.OfSeq(new[] { emitted.ErrorValue }), errors);
            return false;
        }

        wat = emitted.ResultValue;
        return true;
    }

    private static bool HasErrors(FSharpList<Diagnostic> diagnostics)
    {
        return diagnostics.Any(Diagnostics.isError);
    }

    private static string DefaultWasmOutputPath(string inputFile)
    {
        var directory = Path.GetDirectoryName(inputFile) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(inputFile);
        return Path.Combine(directory, stem + ".wat");
    }

    private static int UnsupportedTarget(Shell shell, string target)
    {
        shell.Error.WriteLine($"Unsupported target: {target}");
        return UsageError;
    }
}
