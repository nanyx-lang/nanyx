using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Nanyx.Runtime;
using Nanyx.Runtime.Debugging;
using NyxCompiler;

/// <summary>
/// <c>nanyx run --debug</c>: the program, built with debug probes, run under a Debug Adapter Protocol server on the
/// shell's stdin and stdout (see <c>DebugServer</c>).
/// </summary>
internal static partial class CliCommands
{
    /// <summary>
    /// Runs the program <paramref name="result"/> compiled to <paramref name="wasm"/> under the debugger, with
    /// <paramref name="arguments"/>, its name first, and <paramref name="directories"/>, and gives the exit code it ended
    /// with. Its files are named as the table of places names them, from <paramref name="packageRoot"/>, a package's
    /// directory, or by their names alone for a script, and found again on disk by the files its code is in, or else in
    /// <paramref name="debugFrom"/>, the package's directory or the script's. A served program can't be debugged yet.
    /// </summary>
    private static int Debug(Shell shell, CompileResult result, byte[] wasm, string wat, FSharpList<string> arguments, FSharpList<string> directories, string? packageRoot, string debugFrom, TextWriter errors)
    {
        if (WasmServer.serves(wat))
        {
            errors.WriteLine("error: --debug runs a program that runs once, and this one is served: debugging a served program isn't supported yet.");
            return UsageError;
        }

        var root = packageRoot is null ? FSharpOption<string>.None : FSharpOption<string>.Some(packageRoot);
        // Each name the table of places gives a file of the program's code, with its full path: a name two share is left out.
        var codeFiles = result.Typed is null ? [] : PackageModulesModule.codeFiles(result.Typed.Value.Module).Where(Path.IsPathRooted).ToList();
        var fullPaths = codeFiles
            .GroupBy(file => PackageModulesModule.shownFile(root, file))
            .Where(named => named.Count() == 1)
            .ToDictionary(named => named.Key, named => Path.GetFullPath(named.Single()));
        string? FileOf(string shown)
        {
            if (fullPaths.TryGetValue(shown, out var full))
            {
                return full;
            }

            var candidate = Path.GetFullPath(Path.Combine(debugFrom, shown));
            return File.Exists(candidate) ? candidate : null;
        }

        string ShownOf(string path) => PackageModulesModule.shownFile(root, Path.GetFullPath(path));

        (string, int) Report(RunError error)
        {
            using var text = new StringWriter();
            var code = ReportRunError(error, text);
            return (text.ToString(), code);
        }

        var target = new DebugTarget(
            wasm,
            arguments,
            directories,
            FuncConvert.FromFunc<string, FSharpOption<string>>(shown => FileOf(shown) is { } file ? FSharpOption<string>.Some(file) : FSharpOption<string>.None),
            FuncConvert.FromFunc<string, string>(ShownOf),
            FuncConvert.FromFunc<RunError, Tuple<string, int>>(error =>
            {
                var (text, exit) = Report(error);
                return Tuple.Create(text, exit);
            }));
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        return DebugServer.serve(input, output, target);
    }
}
