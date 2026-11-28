using System.Net;
using System.Net.Sockets;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Nanyx.Runtime;
using Nanyx.Runtime.Debugging;
using NyxCompiler;

/// <summary>
/// <c>nanyx run --debug</c>: the program, built with debug probes, run under a Debug Adapter Protocol server on the
/// shell's stdin and stdout (see <c>DebugServer</c>).
/// </summary>
/// <summary>
/// How <c>nanyx run --debug</c> is reached: on stdin and stdout, with no <paramref name="Port"/>, or on that port of
/// 127.0.0.1, 0 for one the system chooses, written to <paramref name="PortFile"/> if it is given, the program then having
/// this process's standard streams.
/// </summary>
internal sealed record DebugMode(int? Port, string? PortFile);

internal static partial class CliCommands
{
    /// <summary>
    /// The debug mode <c>--debug</c>, <c>--dap-port</c> and <c>--dap-port-file</c> ask for, none without <c>--debug</c>,
    /// or false, having said why, when they don't go together.
    /// </summary>
    private static bool TryDebugMode(Shell shell, bool debug, string? port, string? portFile, out DebugMode? mode)
    {
        mode = null;
        if (!debug && (port is not null || portFile is not null))
        {
            shell.Error.WriteLine("error: --dap-port and --dap-port-file say where the debugger listens: give --debug too.");
            return false;
        }

        if (portFile is not null && port is null)
        {
            shell.Error.WriteLine("error: --dap-port-file is where the debugger writes the port it listens on: give --dap-port too.");
            return false;
        }

        int? parsed = null;
        if (port is not null)
        {
            if (!int.TryParse(port, out var number) || number < 0 || number > 65535)
            {
                shell.Error.WriteLine($"error: --dap-port is a port, from 0 to 65535, 0 for one the system chooses, not '{port}'");
                return false;
            }

            parsed = number;
        }

        mode = debug ? new DebugMode(parsed, portFile) : null;
        return true;
    }

    /// <summary>
    /// Runs the program <paramref name="result"/> compiled to <paramref name="wasm"/> under the debugger, with
    /// <paramref name="arguments"/>, its name first, and <paramref name="directories"/>, and gives the exit code it ended
    /// with. Its files are named as the table of places names them, from <paramref name="packageRoot"/>, a package's
    /// directory, or by their names alone for a script, and found again on disk by the files its code is in, or else in
    /// <paramref name="debugFrom"/>, the package's directory or the script's. A served program can't be debugged yet.
    /// </summary>
    private static int Debug(Shell shell, CompileResult result, byte[] wasm, string wat, FSharpList<string> arguments, FSharpList<string> directories, string? packageRoot, string debugFrom, DebugMode? mode, TextWriter errors)
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
            }),
            mode?.Port is not null);
        if (mode?.Port is not { } port)
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            return DebugServer.serve(input, output, target);
        }

        // On 127.0.0.1 alone, so that nothing off the machine can reach it, for the one editor that connects.
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var chosen = ((IPEndPoint)listener.LocalEndpoint).Port;
        if (mode.PortFile is not null)
        {
            // Whole or not at all, for an editor that reads it as soon as it is there.
            var portFile = shell.FullPath(mode.PortFile);
            File.WriteAllText(portFile + ".part", chosen.ToString());
            File.Move(portFile + ".part", portFile, true);
        }

        errors.WriteLine($"The debugger is listening on 127.0.0.1:{chosen}.");
        using var client = listener.AcceptTcpClient();
        listener.Stop();
        using var stream = client.GetStream();
        return DebugServer.serve(stream, stream, target);
    }
}
