using Consolo;
using Microsoft.FSharp.Collections;
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

internal static class CliCommands
{
    /// <summary>
    /// Compile a Nanyx source file.
    /// </summary>
    /// <param name="inputFile">The path to the .nyx file to compile.</param>
    /// <param name="outputPath">Optional output path for generated artifacts.</param>
    [Consolo("parse")]
    public static int Parse(
        string inputFile,
        [Consolo("out", Alias = "o")] string? outputPath = null)
    {
        return Execute(inputFile, Target.Parse, outputPath);
    }

    /// <summary>
    /// Check a Nanyx source file, or every source file of a package, and write nothing.
    /// </summary>
    /// <param name="path">A .nyx file, or a package's directory or .nyxp manifest; none means the package the current directory is in.</param>
    [Consolo("check")]
    public static int Check(params string[] path)
    {
        if (!TrySinglePath(path, out var single))
        {
            return 2;
        }

        if (single is not null && !IsPackagePath(single))
        {
            return Execute(single, Target.Check, outputPath: null);
        }

        if (!TryFindPackage(single, out var package))
        {
            return 1;
        }

        var build = PackageModule.build(package);
        PrintDiagnostics(build.Diagnostics, Console.Out);
        Console.WriteLine($"Checked {package.Manifest.Name} ({FileCount(package)}).");
        return HasErrors(build.Diagnostics) ? 1 : 0;
    }

    /// <summary>
    /// Make a new package: a .nyxp manifest and src/main.nyx, whose @entry prints a greeting.
    /// </summary>
    /// <param name="name">The package's name: lower case, with words separated by dots, as in acme.web.</param>
    /// <param name="directory">Where to make it, which must be new or empty; the default is the name's last segment.</param>
    [Consolo("new")]
    public static int New(string name, params string[] directory)
    {
        if (!TrySinglePath(directory, out var single))
        {
            return 2;
        }

        var created = NewPackage.create(name, single ?? NewPackage.defaultDirectory(name));
        if (created.IsError)
        {
            Console.Error.WriteLine(created.ErrorValue);
            return 1;
        }

        Console.WriteLine($"Created {name}: {DisplayPath(created.ResultValue)}");
        return 0;
    }

    /// <summary>
    /// Compile a Nanyx source file.
    /// </summary>
    /// <param name="inputFile">The path to the .nyx file to compile.</param>
    /// <param name="outputPath">Optional output path for generated artifacts.</param>
    /// <param name="release">Refuse any error. Without it, type and name errors are deferred: the program is written, and reports one if it reaches it.</param>
    [Consolo("wasm")]
    public static int Wasm(
        string inputFile,
        [Consolo("out", Alias = "o")] string? outputPath = null,
        [Consolo("release")] bool release = false)
    {
        return Execute(inputFile, Target.Wasm, outputPath, release);
    }

    /// <summary>
    /// Run a Nanyx program: a .nyx file, or the @entry of a package.
    /// </summary>
    /// <param name="path">A .nyx file to run as a script, or a package's directory or .nyxp manifest; none means the package the current directory is in.</param>
    /// <param name="outputPath">Optional output path for generated artifacts.</param>
    /// <param name="checkMemory">Check the program's reference counting as it runs: stop at a block freed twice or an object used after it was freed, and fail if anything is left on the heap at the end.</param>
    [Consolo("run")]
    public static int Run(
        [Consolo("out", Alias = "o")] string? outputPath = null,
        [Consolo("check-memory")] bool checkMemory = false,
        params string[] path)
    {
        if (!TrySinglePath(path, out var single))
        {
            return 2;
        }

        if (single is not null && !IsPackagePath(single))
        {
            return Execute(single, Target.Run, outputPath, checkMemory: checkMemory);
        }

        if (!TryFindPackage(single, out var package))
        {
            return 1;
        }

        // As for a script, stdout belongs to the program, so the build's diagnostics go to stderr.
        var build = PackageModule.build(package);
        PrintDiagnostics(WorthStoppingFor(build.Diagnostics), Console.Error);
        var deferred = HasErrors(build.Diagnostics) && build.ErrorsDeferred;
        if (HasErrors(build.Diagnostics) && !deferred)
        {
            return 1;
        }

        var entry = PackageModule.entryResult(build);
        if (entry is null)
        {
            Console.Error.WriteLine($"The package {package.Manifest.Name} has no @entry, so there is nothing to run.");
            return 1;
        }

        if (deferred)
        {
            Console.Error.WriteLine($"Running with {DeferredErrors(build.Diagnostics)}");
        }

        return RunProgram(entry.Value, Console.Error, ProgramIo.Inherited, deferred, checkMemory);
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
        if (!TrySinglePath(path, out var single) || !TryFindPackage(single, out var package))
        {
            return 1;
        }

        var build = PackageModule.build(package);
        PrintDiagnostics(build.Diagnostics, Console.Out);
        var deferred = !release && HasErrors(build.Diagnostics) && build.ErrorsDeferred;
        if (HasErrors(build.Diagnostics) && !deferred)
        {
            return 1;
        }

        var entry = PackageModule.entryResult(build);
        var files = FileCount(package);
        if (entry is null)
        {
            // With no program to write, the build is only a check, and errors fail it.
            Console.WriteLine($"Checked {package.Manifest.Name} ({files}); it has no @entry, so no program was written.");
            return deferred ? 1 : 0;
        }

        if (!TryEmitWat(entry.Value, Console.Out, out var wat, CodeGen.WasmOptions.Default, deferred))
        {
            return 1;
        }

        var finalOutputPath = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(package.Manifest.Root, "out", package.Manifest.Name + ".wat")
            : outputPath!;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(finalOutputPath))!);
        File.WriteAllText(finalOutputPath, wat);
        var withDeferred = deferred ? $" with {DeferredErrors(build.Diagnostics)}" : "";
        Console.WriteLine($"Built {package.Manifest.Name} ({files}){withDeferred}: {DisplayPath(Path.GetFullPath(finalOutputPath))}");
        return 0;
    }

    /// <summary>
    /// The one path a command was given, or <c>null</c> for none. Paths are taken as a <c>params</c> array only so that
    /// the path can be left out and still be positional.
    /// </summary>
    private static bool TrySinglePath(string[] paths, out string? path)
    {
        path = paths.FirstOrDefault();
        if (paths.Length > 1)
        {
            Console.Error.WriteLine($"Give one file, directory or manifest, not {paths.Length}.");
            return false;
        }

        return true;
    }

    /// <summary>"1 file", "3 files": how many source files the package has.</summary>
    private static string FileCount(Package package) =>
        package.Sources.Length == 1 ? "1 file" : $"{package.Sources.Length} files";

    /// <summary>Whether a path names a package (its directory or manifest) rather than a file to run as a script.</summary>
    private static bool IsPackagePath(string path) =>
        Directory.Exists(path) || path.EndsWith(ManifestModule.extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The package a path names, or the one the current directory is in; otherwise reports why there is none.</summary>
    private static bool TryFindPackage(string? path, out Package package)
    {
        var found = PackageModule.find(path ?? Directory.GetCurrentDirectory());
        if (found.IsError)
        {
            foreach (var error in found.ErrorValue)
            {
                Console.Error.WriteLine(error);
            }

            package = null!;
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
        var result = Compiler.compile(HelloWorldSource);
        PrintDiagnostics(result.Diagnostics, Console.Error);
        return RunProgram(result, Console.Error, ProgramIo.Inherited);
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
            return 2;
        }

        var found = Nanyx.Formatter.FormatCommand.findFiles(Directory.GetCurrentDirectory(), ListModule.OfSeq(paths));
        if (found.IsError)
        {
            Console.Error.WriteLine(found.ErrorValue);
            return 2;
        }

        var mode = check ? Nanyx.Formatter.FormatCommand.Mode.Check : Nanyx.Formatter.FormatCommand.Mode.Write;
        var results = Nanyx.Formatter.FormatCommand.run(Nanyx.Formatter.Settings.cached(), mode, found.ResultValue);

        // Every file under a manifest with bad settings fails the same way, so each message is printed once.
        var settingsErrors = new HashSet<string>();
        foreach (var (file, result) in results)
        {
            if (result.IsChanged && check)
            {
                Console.WriteLine(DisplayPath(file));
            }
            else if (result is Nanyx.Formatter.FormatCommand.FileResult.Failed failed)
            {
                switch (failed.Item)
                {
                    case Nanyx.Formatter.FormatCommand.Formatting.NotParsed notParsed:
                        PrintDiagnostics(ListModule.OfSeq([Compiler.syntaxError(notParsed.Item)]), Console.Error);
                        break;
                    case Nanyx.Formatter.FormatCommand.Formatting.NotSafe notSafe:
                        Console.Error.WriteLine($"{DisplayPath(file)}: internal error: {notSafe.Item}; the file was left as it is.");
                        break;
                    // A directive's error is the file's own, and starts with where in it the directive is.
                    case Nanyx.Formatter.FormatCommand.Formatting.BadSettings badSettings when badSettings.Item.StartsWith(file + ":", StringComparison.Ordinal):
                        Console.Error.WriteLine($"{DisplayPath(file)}{badSettings.Item[file.Length..]}; the file was left as it is.");
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
            return 2;
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
    /// Whether a debug build can run a file's program in spite of its errors: each is a type or name error the compiler
    /// deferred, to be reported if the program reaches it.
    /// </summary>
    private static bool ErrorsDeferred(CompileResult result) =>
        result.Typed is not null && !result.TypeChecked && result.ErrorsDeferred;

    /// <summary>"2 deferred errors": the errors among <paramref name="diagnostics"/>, every one of them deferred.</summary>
    private static string DeferredErrors(FSharpList<Diagnostic> diagnostics)
    {
        var count = diagnostics.Count(diagnostic => diagnostic.Severity.IsError);
        return $"{count} deferred error{(count == 1 ? "" : "s")}";
    }

    private static int Execute(string inputFile, Target target, string? outputPath, bool release = false, bool checkMemory = false)
    {
        if (string.IsNullOrWhiteSpace(inputFile))
        {
            Console.Error.WriteLine("Input file is required.");
            return 2;
        }

        if (!File.Exists(inputFile))
        {
            Console.Error.WriteLine($"File not found: {inputFile}");
            return 2;
        }

        if (target == Target.Parse)
        {
            return RunParse(inputFile);
        }

        if (target == Target.Run)
        {
            return RunScript(inputFile, checkMemory, Console.Error, ProgramIo.Inherited);
        }

        // Script mode: the file's :: directives say what a package's manifest would.
        var result = Script.compileFile(inputFile);

        Console.WriteLine($"Phase: {result.Phase}");
        PrintDiagnostics(result.Diagnostics, Console.Out);

        return target switch
        {
            Target.Check => RunCheck(result),
            Target.Wasm => RunWasm(result, inputFile, outputPath, release),
            _ => UnsupportedTarget(target.ToString())
        };
    }

    /// <summary>
    /// What <c>nanyx run</c> does with the script <paramref name="inputFile"/>, which exists: compiles and runs it, with
    /// its standard streams <paramref name="io"/>, and gives the exit code. The compiler's diagnostics and the run's
    /// own errors go to <paramref name="errors"/>. The Wasm tests call this to run programs in their own process.
    /// </summary>
    internal static int RunScript(string inputFile, bool checkMemory, TextWriter errors, ProgramIo io)
    {
        // Script mode: the file's :: directives say what a package's manifest would.
        var result = Script.compileFile(inputFile);

        // `run` is the user-facing command: its stdout belongs to the program, so compiler chatter is kept off it.
        PrintDiagnostics(WorthStoppingFor(result.Diagnostics), errors);
        // A debug run: type and name errors the compiler deferred do not stop it. Each is reported again if the
        // program reaches it.
        var deferred = ErrorsDeferred(result);
        if (deferred)
        {
            errors.WriteLine($"Running with {DeferredErrors(result.Diagnostics)}");
        }

        return RunProgram(result, errors, io, deferred, checkMemory);
    }

    private static int RunParse(string inputFile)
    {
        var parsed = Parser.Program.Api.parseModuleFile(Path.GetFullPath(inputFile), File.ReadAllText(inputFile));

        if (parsed.IsError)
        {
            Console.WriteLine($"Parse error: {parsed.ErrorValue}");
            return 1;
        }

        Console.WriteLine("Parsed successfully:");
        foreach (var item in parsed.ResultValue)
        {
            Console.WriteLine($"  {item}");
        }

        return 0;
    }

    /// <summary>
    /// The diagnostics `run` shows: errors and warnings. Info and hints are suggestions for when the code is being
    /// worked on, so `check` and the editor show them, but running a program does not.
    /// </summary>
    private static FSharpList<Diagnostic> WorthStoppingFor(FSharpList<Diagnostic> diagnostics) =>
        ListModule.OfSeq(diagnostics.Where(diagnostic => !diagnostic.Severity.IsInfo && !diagnostic.Severity.IsHint));

    private static void PrintDiagnostics(FSharpList<Diagnostic> diagnostics, TextWriter writer)
    {
        if (diagnostics.IsEmpty)
        {
            if (writer == Console.Out)
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
            var location = string.Empty;

            if (diagnostic.Span is not null)
            {
                var span = diagnostic.Span.Value;
                location = $"{DisplayPath(span.File)}:{span.StartLine + 1}:{span.StartColumn + 1}: ";
            }

            writer.WriteLine($"{location}{severity} {diagnostic.Code}: {diagnostic.Message}");

            foreach (var related in diagnostic.Related)
            {
                var span = related.Item1;
                writer.WriteLine($"  {DisplayPath(span.File)}:{span.StartLine + 1}:{span.StartColumn + 1}: {related.Item2}");
            }
        }
    }

    /// <summary>A diagnostic's file, relative to the current directory when it is under it.</summary>
    private static string DisplayPath(string file)
    {
        if (string.IsNullOrEmpty(file) || !Path.IsPathRooted(file))
        {
            return file;
        }

        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), file);
        return relative.StartsWith("..", StringComparison.Ordinal) ? file : relative;
    }

    private static void PrintTypes(FSharpMap<string, Ty> types)
    {
        if (types.Count == 0)
        {
            Console.WriteLine("(no inferred types)");
            return;
        }

        foreach (var entry in types.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"{entry.Key} : {entry.Value}");
        }
    }

    private static int RunCheck(CompileResult result)
    {
        if (result.Typed is null || !result.TypeChecked)
        {
            return 1;
        }

        Console.WriteLine("Inferred types:");
        PrintTypes(result.Typed.Value.Types);

        // The module type-checked, but a linter rule configured as an error fails the check all the same.
        return HasErrors(result.Diagnostics) ? 1 : 0;
    }

    private static int RunWasm(CompileResult result, string inputFile, string? outputPath, bool release)
    {
        var deferred = !release && ErrorsDeferred(result);
        if (!TryEmitWat(result, Console.Out, out var wat, CodeGen.WasmOptions.Default, deferred))
        {
            return 1;
        }

        var finalOutputPath = string.IsNullOrWhiteSpace(outputPath) ? DefaultWasmOutputPath(inputFile) : outputPath!;
        File.WriteAllText(finalOutputPath, wat);
        var withDeferred = deferred ? $" with {DeferredErrors(result.Diagnostics)}" : "";
        Console.WriteLine($"WASM (WAT) emitted{withDeferred}: {finalOutputPath}");
        return 0;
    }

    /// <summary>
    /// Runs a compiled program with its standard streams <paramref name="io"/>, reporting what stops it to
    /// <paramref name="errors"/>, and gives the exit code.
    /// </summary>
    private static int RunProgram(
        CompileResult result, TextWriter errors, ProgramIo io, bool errorsDeferred = false, bool checkMemory = false)
    {
        var options = new CodeGen.WasmOptions(checkMemory);
        if (!TryEmitWat(result, errors, out var wat, options, errorsDeferred))
        {
            return 1;
        }

        if (!checkMemory)
        {
            var outcome = WasmRunner.run(io, wat);
            return outcome.IsOk ? 0 : ReportRunError(outcome.ErrorValue, errors);
        }

        var counted = WasmRunner.runReading(
            io,
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
        CompileResult result, TextWriter errors, out string wat, CodeGen.WasmOptions options, bool errorsDeferred = false)
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
            PrintDiagnostics(ListModule.OfSeq(new[] { emitted.ErrorValue }), errors);
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

    private static int UnsupportedTarget(string target)
    {
        Console.Error.WriteLine($"Unsupported target: {target}");
        return 2;
    }
}
