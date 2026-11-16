using Consolo;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Nanyx.Runtime;
using NyxCompiler;
using System.Reflection;
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

    /// <summary>A diagnostic's file, relative to <see cref="Directory"/> when it is under it, and after its package's name
    /// when it is in a package the compiler ships.</summary>
    public string DisplayPath(string file)
    {
        if (string.IsNullOrEmpty(file) || !Path.IsPathRooted(file))
        {
            return file;
        }

        if (ShippedPackages.tryDisplay(file) is { } shipped)
        {
            return shipped.Value;
        }

        var relative = Path.GetRelativePath(Directory, file);
        return relative.StartsWith("..", StringComparison.Ordinal) ? file : relative;
    }
}

internal static partial class CliCommands
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
    /// <param name="timings">Write how long each module took to check to stderr, as a tree of the modules each one imported. On a terminal, the tree is shown live while the compile runs.</param>
    /// <param name="host">The host to run the package's @entry under, in place of its manifest's host: a host package the compiler ships, such as nanyx.console, or the directory of one. A script names its host with a ::host directive.</param>
    [Consolo("check")]
    public static int Check(
        [Consolo("timings")] bool timings = false,
        [Consolo("host")] string? host = null,
        params string[] path)
    {
        return CheckIn(Shell.Process, path, timings, host);
    }

    /// <summary>What <c>nanyx check</c> does, run in <paramref name="shell"/>.</summary>
    internal static int CheckIn(Shell shell, string[] path, bool timings = false, string? host = null)
    {
        if (!TrySinglePath(shell, path, out var single))
        {
            return UsageError;
        }

        if (single is not null && !IsPackagePath(shell, single))
        {
            return NoHostForScript(shell, host) ?? Execute(shell, single, Target.Check, outputPath: null, timings: timings);
        }

        if (!TryFindPackage(shell, single, host, out var package, out var failed))
        {
            return failed;
        }

        var build = BuildPackage(shell, package, timings);
        PrintDiagnostics(shell, build.Diagnostics, shell.Out);
        shell.Out.WriteLine($"{Checked(package)}.");
        return HasErrors(build.Diagnostics) ? 1 : 0;
    }

    /// <summary>
    /// Print a package's manifest, for tools outside the compiler, once it is checked.
    /// </summary>
    /// <param name="json">Print it as JSON: a record is an object, a list an array, and a tag such as #error the string "#error". This is the only form so far, so it is needed.</param>
    /// <param name="path">A package's directory or .nyxp manifest; none means the package the current directory is in.</param>
    [Consolo("manifest")]
    public static int PrintManifest([Consolo("json")] bool json = false, params string[] path) =>
        ManifestIn(Shell.Process, json, path);

    /// <summary>What <c>nanyx manifest</c> does, run in <paramref name="shell"/>.</summary>
    internal static int ManifestIn(Shell shell, bool json, string[] path)
    {
        if (!TrySinglePath(shell, path, out var single))
        {
            return UsageError;
        }

        if (!json)
        {
            shell.Error.WriteLine("nanyx manifest prints the manifest as JSON, so it needs --json: nanyx manifest --json");
            return UsageError;
        }

        if (!TryFindManifest(shell, single, out var manifestPath, out var failed))
        {
            return failed;
        }

        var read = ManifestModule.read(manifestPath);
        if (read.IsError)
        {
            PrintDiagnostics(shell, read.ErrorValue, shell.Error);
            return 1;
        }

        shell.Out.WriteLine(ManifestModule.toJson(read.ResultValue.Value));
        return 0;
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

        var created = NewPackage.create(name, Toolchain.version, shell.FullPath(single ?? NewPackage.defaultDirectory(name)));
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
    /// <param name="path">A .nyx file to run as a script, or a package's directory or .nyxp manifest, followed by the program's arguments; none means the package the current directory is in. Put -- before arguments that start with -, so that they are not read as run's options.</param>
    /// <param name="checkMemory">Check the program's reference counting as it runs: stop at a block freed twice or an object used after it was freed, and fail if anything is left on the heap at the end.</param>
    /// <param name="timings">Write how long each module took to check to stderr, as a tree of the modules each one imported, and how long the program took to emit. On a terminal, the tree is shown live while the compile runs.</param>
    /// <param name="host">The host to run the package's @entry under, in place of its manifest's host: a host package the compiler ships, such as nanyx.console, or the directory of one. A script names its host with a ::host directive.</param>
    /// <param name="noInline">Compile every call as a call, without inlining: for working on the compiler, to tell whether a misbehaving program is the inliner's doing.</param>
    [Consolo("run")]
    public static int Run(
        [Consolo("check-memory")] bool checkMemory = false,
        [Consolo("timings")] bool timings = false,
        [Consolo("host")] string? host = null,
        [Consolo("no-inline")] bool noInline = false,
        params string[] path)
    {
        return RunIn(Shell.Process, checkMemory, path, timings, host, !noInline);
    }

    /// <summary>What <c>nanyx run</c> does, run in <paramref name="shell"/>.</summary>
    internal static int RunIn(Shell shell, bool checkMemory, string[] path, bool timings = false, string? host = null, bool inlineCalls = true)
    {
        // What follows the program is its own: its arguments, which it reads through `Env`.
        var single = path.FirstOrDefault();
        var programArguments = path.Skip(1).ToArray();

        if (single is not null && !IsPackagePath(shell, single))
        {
            return NoHostForScript(shell, host)
                ?? Execute(shell, single, Target.Run, outputPath: null, checkMemory: checkMemory, timings: timings, programArguments: programArguments, inlineCalls: inlineCalls);
        }

        if (!TryFindPackage(shell, single, host, out var package, out var failed))
        {
            return failed;
        }

        // As for a script, stdout belongs to the program, so the build's diagnostics go to stderr, in the run's box.
        using var box = RunBox.For(shell, $"run {package.Manifest.Name}");
        var compiling = box is null ? shell : shell with { Error = box.Lines };
        var build = BuildPackage(compiling, package, timings);
        PrintDiagnostics(compiling, WorthStoppingFor(build.Diagnostics), compiling.Error);
        var deferred = ErrorsDeferred(build);
        if (HasErrors(build.Diagnostics) && !deferred)
        {
            return 1;
        }

        var entry = PackageModule.entryResult(build);
        if (entry is null)
        {
            compiling.Error.WriteLine(package.Host is null
                ? $"error: no entry point: {package.Manifest.Name} names no host, so it is a library, which can't be run. An application names the host its @entry runs under in its manifest: host = (package = \"{Hosts.consolePackage}\", version = \"{Toolchain.version}\")."
                : $"error: no entry point: the package {package.Manifest.Name} has no @entry, so there is nothing to run.");
            return 1;
        }

        ReportDeferredRun(compiling, deferred, build.Diagnostics);
        return RunProgram(compiling, entry.Value, deferred, checkMemory, timings, [package.Manifest.Name, .. programArguments], inlineCalls);
    }

    /// <summary>
    /// Build a Nanyx package: check every source file the manifest selects, and write its @entry program as WebAssembly text.
    /// </summary>
    /// <param name="path">The package's directory or .nyxp manifest; none means the package the current directory is in.</param>
    /// <param name="outputPath">Where to write the program; the default is out/&lt;package name&gt;.wat beside the manifest.</param>
    /// <param name="release">Refuse any error. Without it, type and name errors are deferred: the program is written, and reports one if it reaches it.</param>
    /// <param name="timings">Write how long each module took to check to stderr, as a tree of the modules each one imported, and how long the program took to emit. On a terminal, the tree is shown live while the compile runs.</param>
    /// <param name="host">The host to run the package's @entry under, in place of its manifest's host: a host package the compiler ships, such as nanyx.console, or the directory of one. A script names its host with a ::host directive.</param>
    /// <param name="noInline">Compile every call as a call, without inlining: for working on the compiler, to tell whether a misbehaving program is the inliner's doing.</param>
    [Consolo("build")]
    public static int Build(
        [Consolo("out", Alias = "o")] string? outputPath = null,
        [Consolo("release")] bool release = false,
        [Consolo("timings")] bool timings = false,
        [Consolo("host")] string? host = null,
        [Consolo("no-inline")] bool noInline = false,
        params string[] path)
    {
        return BuildIn(Shell.Process, outputPath, release, path, timings, host, !noInline);
    }

    /// <summary>What <c>nanyx build</c> does, run in <paramref name="shell"/>.</summary>
    internal static int BuildIn(Shell shell, string? outputPath, bool release, string[] path, bool timings = false, string? host = null, bool inlineCalls = true)
    {
        if (!TrySinglePath(shell, path, out var single))
        {
            return UsageError;
        }

        if (!TryFindPackage(shell, single, host, out var package, out var failed))
        {
            return failed;
        }

        var build = BuildPackage(shell, package, timings);
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

        if (!TryEmitWat(shell, entry.Value, shell.Out, out var wat, new CodeGen.WasmOptions(false, inlineCalls), deferred, timings))
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

    /// <summary>
    /// The full path of the manifest <paramref name="path"/> names: a .nyxp file, or the manifest of the package a
    /// directory is in, the nearest in it or above it; none means the current directory. What is wrong is written to
    /// stderr, with the exit code it comes to in <paramref name="failed"/>.
    /// </summary>
    private static bool TryFindManifest(Shell shell, string? path, out string manifestPath, out int failed)
    {
        manifestPath = string.Empty;
        failed = 0;
        var full = path is null ? shell.Directory : shell.FullPath(path);
        if (File.Exists(full) && full.EndsWith(ManifestModule.extension, StringComparison.OrdinalIgnoreCase))
        {
            manifestPath = full;
            return true;
        }

        if (!Directory.Exists(full))
        {
            shell.Error.WriteLine($"Not a directory or a {ManifestModule.extension} manifest: {path}");
            failed = UsageError;
            return false;
        }

        var found = ManifestModule.tryFind(full);
        if (found.IsError)
        {
            shell.Error.WriteLine(found.ErrorValue);
            failed = 1;
            return false;
        }

        if (found.ResultValue is null)
        {
            shell.Error.WriteLine($"No {ManifestModule.extension} manifest in {full} or any directory above it");
            failed = 1;
            return false;
        }

        manifestPath = found.ResultValue.Value;
        return true;
    }

    /// <summary>"1 file", "3 files": how many source files the package has.</summary>
    private static string FileCount(Package package) =>
        package.Sources.Length == 1 ? "1 file" : $"{package.Sources.Length} files";

    /// <summary>
    /// Builds the package, with a stopwatch on stderr naming the file and module the build is on while stderr is a
    /// terminal, and each module's time after it with <paramref name="timings"/> (see <see cref="CompileProgress"/>).
    /// </summary>
    private static PackageBuild BuildPackage(Shell shell, Package package, bool timings)
    {
        using var progress = CompileProgress.Start(shell.Error, $"{package.Manifest.Name} ({FileCount(package)})", shell.DisplayPath, timings);
        var build = PackageModule.buildReporting(progress.Listener, package);
        progress.Finish();
        return build;
    }

    /// <summary>
    /// Compiles a script, as <see cref="Script.compileFile"/> does, with a stopwatch on the shell's stderr while that is
    /// a terminal, and each module's time after it with <paramref name="timings"/> (see <see cref="CompileProgress"/>).
    /// </summary>
    private static CompileResult CompileScript(Shell shell, string inputFile, bool timings)
    {
        using var progress = CompileProgress.Start(shell.Error, shell.DisplayPath(shell.FullPath(inputFile)), shell.DisplayPath, timings);
        var result = Script.compileFileReporting(progress.Listener, shell.FullPath(inputFile));
        progress.Finish();
        return result;
    }

    /// <summary>Whether a path names a package (its directory or manifest) rather than a file to run as a script.</summary>
    private static bool IsPackagePath(Shell shell, string path) =>
        Directory.Exists(shell.FullPath(path)) || path.EndsWith(ManifestModule.extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A usage error, reported, when <c>--host</c> is given for a script, which names its host with <c>::host</c>; null
    /// when it is not given.
    /// </summary>
    private static int? NoHostForScript(Shell shell, string? host)
    {
        if (host is null)
        {
            return null;
        }

        shell.Error.WriteLine($"--host chooses a package's host: a script names its own with a ::host directive, ::host acme.server ../server, and runs under {Hosts.consolePackage} without one.");
        return UsageError;
    }

    /// <summary>
    /// The package a path names, or the one the shell's directory is in, under the host <paramref name="host"/> names if
    /// it names one (<c>--host</c>), in place of its manifest's; otherwise reports why there is none, or why the host
    /// can't be chosen, and gives the exit code in <paramref name="failed"/>.
    /// </summary>
    private static bool TryFindPackage(Shell shell, string? path, string? host, out Package package, out int failed)
    {
        if (!TryFindPackage(shell, path, out package, out failed))
        {
            return false;
        }

        if (host is null)
        {
            return true;
        }

        var chosen = PackageModule.withHost(shell.Directory, host, package);
        if (chosen.IsError)
        {
            shell.Error.WriteLine($"--host: {chosen.ErrorValue}");
            failed = UsageError;
            return false;
        }

        package = chosen.ResultValue;
        return true;
    }

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
    /// Print the compiler's version.
    /// </summary>
    [Consolo("--version")]
    public static int PrintVersion() => VersionIn(Shell.Process);

    /// <summary>
    /// Print the release version, the &lt;Version&gt; in Directory.Build.props, such as 0.28.0 (see <c>Toolchain.version</c>).
    /// </summary>
    internal static int VersionIn(Shell shell)
    {
        shell.Out.WriteLine(Toolchain.version);
        return 0;
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
    /// <param name="stdin">Format standard input, as a file in the current directory, and write the result to standard output, for an editor to format a buffer it has not saved.</param>
    /// <param name="paths">Files or directories to format; a directory means every .nyx file under it, and none means the current directory.</param>
    [Consolo("format")]
    public static int Format(bool check = false, bool write = false, bool stdin = false, params string[] paths)
    {
        if (check && write)
        {
            Console.Error.WriteLine("Use --check or --write, not both.");
            return UsageError;
        }

        if (stdin && paths.Length > 0)
        {
            Console.Error.WriteLine("Format standard input (--stdin) or files, not both.");
            return UsageError;
        }

        var shell = Shell.Process;
        var mode = check ? Nanyx.Formatter.FormatCommand.Mode.Check : Nanyx.Formatter.FormatCommand.Mode.Write;
        FSharpList<Tuple<string, Nanyx.Formatter.FormatCommand.FileResult>> results;
        if (stdin)
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            results = ListModule.OfSeq([Nanyx.Formatter.FormatCommand.runInput(Nanyx.Formatter.Settings.cached(), mode, shell.Directory, input, output)]);
        }
        else
        {
            var found = Nanyx.Formatter.FormatCommand.findFiles(shell.Directory, ListModule.OfSeq(paths));
            if (found.IsError)
            {
                Console.Error.WriteLine(found.ErrorValue);
                return UsageError;
            }

            results = Nanyx.Formatter.FormatCommand.run(Nanyx.Formatter.Settings.cached(), mode, found.ResultValue);
        }

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

    /// <summary>
    /// Write the reference of each standard-library page on the docs site from the library's doc comments
    /// (see docs/src/content/work-in-progress/standard-library-implementation.md).
    /// </summary>
    /// <param name="check">Write nothing; list the pages that are out of date, and exit with 1 if there are any.</param>
    /// <param name="directory">The docs site's standard-library pages: docs/src/content/stdlib.</param>
    [Consolo("stdlib-docs")]
    public static int StdlibDocs(string directory, bool check = false)
    {
        var shell = Shell.Process;
        var path = Path.GetFullPath(directory, shell.Directory);
        if (!Directory.Exists(path))
        {
            Console.Error.WriteLine($"{directory} is not a directory.");
            return UsageError;
        }

        var failed = false;
        foreach (var (page, update) in StandardLibraryDocs.update(path))
        {
            switch (update)
            {
                case StandardLibraryDocs.PageUpdate.OutOfDate outOfDate when check:
                    Console.WriteLine(shell.DisplayPath(page));
                    failed = true;
                    break;
                case StandardLibraryDocs.PageUpdate.OutOfDate outOfDate:
                    File.WriteAllText(page, outOfDate.Item);
                    Console.WriteLine($"Wrote {shell.DisplayPath(page)}");
                    break;
                case StandardLibraryDocs.PageUpdate.Unwritable unwritable:
                    Console.Error.WriteLine($"{shell.DisplayPath(page)} {unwritable.Item}");
                    failed = true;
                    break;
            }
        }

        return failed ? 1 : 0;
    }

    /// <summary>
    /// Search the standard library by name, by type, or both: "trim", "bool -> string" or "trim: string -> string".
    /// Prints each match's signature, the import it needs and the contexts it still needs, best first.
    /// </summary>
    /// <param name="query">A name, a type written as a def's annotation is, or a name and a type with a colon between them.</param>
    [Consolo("search")]
    public static int Search(string query) => SearchIn(Shell.Process, query);

    internal static int SearchIn(Shell shell, string query)
    {
        var parsed = LibrarySearch.parse(query);
        if (parsed.IsError)
        {
            shell.Error.WriteLine(parsed.ErrorValue);
            return UsageError;
        }

        var matches = LibrarySearch.search(_ => "", parsed.ResultValue);
        if (matches.IsEmpty)
        {
            shell.Out.WriteLine("Nothing in the standard library matches.");
            return 1;
        }

        foreach (var found in matches)
        {
            var line = string.Join(" ", found.Signature.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
            if (found.Import is { } import)
            {
                line += $"  ({import.Value})";
            }
            if (!found.Needs.IsEmpty)
            {
                line += $"  needs {string.Join(", ", found.Needs)}";
            }
            shell.Out.WriteLine(line);
        }

        return 0;
    }

    private const string HelloWorldSource = """
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
    /// <param name="timings">Log how long each module took to check with each compile, as a tree of the modules each one imported, not only how long the compile took.</param>
    /// <param name="noFormatHints">Don't show where a document differs from what nanyx format would make of it.</param>
    [Consolo("lsp")]
    public static int Lsp(
        [Consolo("stdio")] bool stdio = false,
        [Consolo("ascii")] bool ascii = false,
        [Consolo("top-level-type-hints")] string topLevelTypeHints = "lens",
        [Consolo("no-local-type-hints")] bool noLocalTypeHints = false,
        [Consolo("no-parameter-type-hints")] bool noParameterTypeHints = false,
        [Consolo("no-pattern-variable-type-hints")] bool noPatternVariableTypeHints = false,
        [Consolo("timings")] bool timings = false,
        [Consolo("no-format-hints")] bool noFormatHints = false)
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
            PatternVariableTypeHints: !noPatternVariableTypeHints,
            Timings: timings,
            FormattingHints: !noFormatHints);
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

    private static int Execute(Shell shell, string inputFile, Target target, string? outputPath, bool release = false, bool checkMemory = false, bool timings = false, string[]? programArguments = null, bool inlineCalls = true)
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
            return RunScript(shell, inputFile, checkMemory, timings, programArguments, inlineCalls);
        }

        // Script mode: the file's :: directives say what a package's manifest would.
        var result = CompileScript(shell, inputFile, timings);

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
    /// shell's stderr. The program's command-line arguments are <paramref name="inputFile"/> followed by
    /// <paramref name="programArguments"/>. The Wasm tests call this to run programs in their own process.
    /// </summary>
    internal static int RunScript(Shell shell, string inputFile, bool checkMemory, bool timings = false, string[]? programArguments = null, bool inlineCalls = true)
    {
        // `run` is the user-facing command: its stdout belongs to the program, so compiler chatter is kept off it, and
        // on a terminal it goes in the run's box.
        using var box = RunBox.For(shell, $"run {shell.DisplayPath(shell.FullPath(inputFile))}");
        var compiling = box is null ? shell : shell with { Error = box.Lines };

        // Script mode: the file's :: directives say what a package's manifest would.
        var result = CompileScript(compiling, inputFile, timings);
        PrintDiagnostics(compiling, WorthStoppingFor(result.Diagnostics), compiling.Error);
        // A debug run: type and name errors the compiler deferred do not stop it. Each is reported again if the
        // program reaches it.
        var deferred = ErrorsDeferred(result);
        ReportDeferredRun(compiling, deferred, result.Diagnostics);
        return RunProgram(compiling, result, deferred, checkMemory, timings, [inputFile, .. programArguments ?? []], inlineCalls);
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
        // `path/to/file.nyx:3:9: error NYX0201: message`, then the line of source with the span underlined (see
        // `SourceExcerpt`). On a terminal, the severity and the underline are in its colour. In the run's box, only the
        // gutter is dimmed, so diagnostics keep their colours.
        var box = (writer as RunBox.Writer)?.Box;
        writer = box?.Diagnostics ?? writer;
        var colour = box?.InColour ?? Terminal.InColour(writer);
        var sources = new Dictionary<string, string?>();
        foreach (var diagnostic in diagnostics)
        {
            var severity = diagnostic.Severity.ToString().ToLowerInvariant();
            var escape = colour ? SeverityColour(diagnostic.Severity) : null;
            string Coloured(string text) => escape is null ? text : $"\u001b[{escape}m{text}\u001b[0m";
            var location = diagnostic.Span is null ? string.Empty : $"{FormatLocation(shell, diagnostic.Span.Value)}: ";
            writer.WriteLine($"{location}{Coloured(severity)} {diagnostic.Code}: {diagnostic.Message}");

            if (diagnostic.Span is not null)
            {
                var span = diagnostic.Span.Value;
                if (!sources.TryGetValue(span.File, out var source))
                {
                    source = SourceExcerpt.SourceOf(span.File);
                    sources[span.File] = source;
                }

                if (source is not null)
                {
                    foreach (var line in SourceExcerpt.Lines(span, source, Coloured))
                    {
                        writer.WriteLine(line);
                    }
                }
            }

            foreach (var related in diagnostic.Related)
            {
                writer.WriteLine($"  {FormatLocation(shell, related.Item1)}: {related.Item2}");
            }
        }
    }

    /// <summary>The ANSI colour a diagnostic of <paramref name="severity"/> is shown in: bold red, yellow or cyan.</summary>
    private static string SeverityColour(Severity severity) =>
        severity.IsError || severity.IsCritical ? "1;31" : severity.IsWarning ? "33" : "36";

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
    /// Runs a compiled program with the shell's standard streams and the command-line arguments
    /// <paramref name="arguments"/>, its name first, reporting what stops it to the shell's stderr, and gives the exit
    /// code.
    /// </summary>
    private static int RunProgram(Shell shell, CompileResult result, bool errorsDeferred = false, bool checkMemory = false, bool timings = false, string[]? arguments = null, bool inlineCalls = true)
    {
        var programArguments = ListModule.OfSeq(arguments ?? ["nanyx"]);
        var errors = shell.Error;
        var options = new CodeGen.WasmOptions(checkMemory, inlineCalls);
        if (!TryEmitWat(shell, result, errors, out var wat, options, errorsDeferred, timings))
        {
            return 1;
        }

        // The program's output follows the run's box, unmarked, and so does what the run reports after it.
        (errors as RunBox.Writer)?.Box.Close("output");
        if (!checkMemory)
        {
            var outcome = WasmRunner.run(shell.Io, programArguments, wat);
            return outcome.IsOk ? 0 : ReportRunError(outcome.ErrorValue, errors);
        }

        var counted = WasmRunner.runReading(
            shell.Io,
            programArguments,
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
            case var _ when error.IsOutOfMemory:
                errors.WriteLine("Runtime error: out of memory");
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
    /// traps is lowered too. With <paramref name="timings"/>, how long the lowering took goes to the shell's stderr,
    /// after the modules' times (see <see cref="CompileProgress"/>).
    /// </summary>
    private static bool TryEmitWat(
        Shell shell, CompileResult result, TextWriter errors, out string wat, CodeGen.WasmOptions options, bool errorsDeferred = false, bool timings = false)
    {
        wat = string.Empty;

        if (result.Typed is null || (HasErrors(result.Diagnostics) && !errorsDeferred))
        {
            return false;
        }

        // What the backend cannot compile is reported where it is written, like any other error.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var emitted = CodeGen.tryTranspileTypedModuleWith(options, result.Typed.Value);
        if (timings)
        {
            shell.Error.WriteLine($"Emitted the program in {stopwatch.Elapsed.TotalSeconds:0.000}s");
        }

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
