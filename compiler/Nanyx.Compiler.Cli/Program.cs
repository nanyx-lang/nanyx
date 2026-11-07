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
    /// Compile a Nanyx source file.
    /// </summary>
    /// <param name="inputFile">The path to the .nyx file to compile.</param>
    /// <param name="outputPath">Optional output path for generated artifacts.</param>
    [Consolo("check")]
    public static int Check(
        string inputFile,
        [Consolo("out", Alias = "o")] string? outputPath = null)
    {
        return Execute(inputFile, Target.Check, outputPath);
    }

    /// <summary>
    /// Compile a Nanyx source file.
    /// </summary>
    /// <param name="inputFile">The path to the .nyx file to compile.</param>
    /// <param name="outputPath">Optional output path for generated artifacts.</param>
    [Consolo("wasm")]
    public static int Wasm(
        string inputFile,
        [Consolo("out", Alias = "o")] string? outputPath = null)
    {
        return Execute(inputFile, Target.Wasm, outputPath);
    }

    /// <summary>
    /// Compile a Nanyx source file.
    /// </summary>
    /// <param name="inputFile">The path to the .nyx file to compile.</param>
    /// <param name="outputPath">Optional output path for generated artifacts.</param>
    [Consolo("run")]
    public static int Run(
        string inputFile,
        [Consolo("out", Alias = "o")] string? outputPath = null)
    {
        return Execute(inputFile, Target.Run, outputPath);
    }

    /// <summary>
    /// Compile and run a built-in "hello world" program, to check that the compiler works.
    /// </summary>
    [Consolo("hello")]
    public static int Hello()
    {
        var result = Compiler.compile(HelloWorldSource);
        PrintDiagnostics(result.Diagnostics, Console.Error);
        return RunProgram(result);
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
        var results = Nanyx.Formatter.FormatCommand.run(Nanyx.Formatter.Formatter.OptionsModule.defaults, mode, found.ResultValue);

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
                        PrintDiagnostics(ListModule.OfSeq([SyntaxError(notParsed.Item)]), Console.Error);
                        break;
                    case Nanyx.Formatter.FormatCommand.Formatting.NotSafe notSafe:
                        Console.Error.WriteLine($"{DisplayPath(file)}: internal error: {notSafe.Item}; the file was left as it is.");
                        break;
                }
            }
        }

        return Nanyx.Formatter.FormatCommand.exitCode(mode, results);
    }

    /// <summary>The diagnostic `nanyx check` gives for a failed parse. FParsec's report starts by giving the file and
    /// position ("Error in path: Ln: 1 Col: 5", wrapped when the path is long), which the diagnostic's span already
    /// gives.</summary>
    private static Diagnostic SyntaxError(Parser.Program.ParseError error)
    {
        var message = error.Message;
        var column = message.IndexOf(" Col: ", StringComparison.Ordinal);
        var lineEnd = column < 0 ? -1 : message.IndexOf('\n', column);
        if (message.StartsWith("Error in ") && lineEnd >= 0)
        {
            message = message[(lineEnd + 1)..];
        }

        return Diagnostics.at(DiagnosticCodes.SyntaxError, error.Span, message);
    }

    private const string HelloWorldSource = """
        import nanyx/console

        println("Hello, World!")
        """;

    /// <summary>
    /// Run the Nanyx language server over standard input and output (for editors; see compiler/README.md).
    /// </summary>
    /// <param name="stdio">Accepted and ignored: stdio is the only transport, but LSP clients conventionally pass it.</param>
    /// <param name="latinTypeVars">Name inferred type variables a, b, ... in hovers, rather than α, β, ...</param>
    /// <param name="topLevelTypeHints">Where to show top-level defs' inferred types: lens (above them, the default), inlay (after their names) or off.</param>
    [Consolo("lsp")]
    public static int Lsp(
        [Consolo("stdio")] bool stdio = false,
        [Consolo("latin-type-vars")] bool latinTypeVars = false,
        [Consolo("top-level-type-hints")] string topLevelTypeHints = "lens")
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
            latinTypeVars ? TypeVarNames.Latin : TypeVarNames.Greek,
            topLevel.Value);
        Nanyx.LanguageServer.NanyxLanguageServer.RunStdioAsync(settings).GetAwaiter().GetResult();
        return 0;
    }

    private static int Execute(string inputFile, Target target, string? outputPath)
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

        var result = Compiler.compileFile(inputFile);

        // `run` is the user-facing command: its stdout belongs to the program, so compiler chatter is kept off it.
        if (target == Target.Run)
        {
            PrintDiagnostics(result.Diagnostics, Console.Error);
            return RunProgram(result);
        }

        Console.WriteLine($"Phase: {result.Phase}");
        PrintDiagnostics(result.Diagnostics, Console.Out);

        return target switch
        {
            Target.Check => RunCheck(result),
            Target.Wasm => RunWasm(result, inputFile, outputPath),
            _ => UnsupportedTarget(target.ToString())
        };
    }

    private static int RunParse(string inputFile)
    {
        var parsed = Parser.Program.parseModuleFile(Path.GetFullPath(inputFile), File.ReadAllText(inputFile));

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
        if (result.Typed is null)
        {
            return 1;
        }

        Console.WriteLine("Inferred types:");
        PrintTypes(result.Typed.Value.Types);

        // The module type-checked, but an analyzer rule configured as an error fails the check all the same.
        return HasErrors(result.Diagnostics) ? 1 : 0;
    }

    private static int RunWasm(CompileResult result, string inputFile, string? outputPath)
    {
        if (!TryEmitWat(result, Console.Out, out var wat))
        {
            return 1;
        }

        var finalOutputPath = string.IsNullOrWhiteSpace(outputPath) ? DefaultWasmOutputPath(inputFile) : outputPath!;
        File.WriteAllText(finalOutputPath, wat);
        Console.WriteLine($"WASM (WAT) emitted: {finalOutputPath}");
        return 0;
    }

    private static int RunProgram(CompileResult result)
    {
        if (!TryEmitWat(result, Console.Error, out var wat))
        {
            return 1;
        }

        var outcome = WasmRunner.run(wat);
        if (outcome.IsOk)
        {
            return 0;
        }

        switch (outcome.ErrorValue)
        {
            case RunError.InvalidModule invalid:
                Console.Error.WriteLine($"Internal compiler error: the generated WebAssembly is invalid: {invalid.Item}");
                return 1;
            case RunError.Trapped trapped:
                Console.Error.WriteLine($"Runtime error: {trapped.Item}");
                return 1;
            default:
                Console.Error.WriteLine($"Run failed: {outcome.ErrorValue}");
                return 1;
        }
    }

    /// <summary>Lowers a successfully type-checked program to WebAssembly text, reporting failures to <paramref name="errors"/>.</summary>
    private static bool TryEmitWat(CompileResult result, TextWriter errors, out string wat)
    {
        wat = string.Empty;

        if (result.Typed is null || HasErrors(result.Diagnostics))
        {
            return false;
        }

        try
        {
            wat = CodeGen.transpileTypedModuleToWat(result.Typed.Value);
            return true;
        }
        catch (Exception ex)
        {
            errors.WriteLine($"WASM emission failed: {ex.Message}");
            return false;
        }
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
