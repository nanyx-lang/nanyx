using Consolo;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Nanyx.Runtime;
using NyxCompiler;
using System.Reflection;
using Transpiler.Wasm;

/// <summary>The form <c>nanyx build</c> and <c>nanyx wasm</c> write a program in.</summary>
enum ProgramFormat
{
    /// <summary>The binary format, <c>.wasm</c>, which browsers and other embedders load.</summary>
    Binary,

    /// <summary>WebAssembly text, <c>.wat</c>, for reading.</summary>
    Text
}

enum Target
{
    Parse,
    Check,
    Wasm,
    Run
}

/// <summary>
/// Where a command runs: the directory it reads relative paths from and shows paths relative to, the writers it reports
/// to, the standard streams of a program it runs, and the package cache and index it finds packages in. The CLI's is the
/// process's own (see <see cref="Process"/>); the Wasm tests give their own, to run commands in their process.
/// </summary>
internal sealed record Shell(string Directory, TextWriter Out, TextWriter Error, ProgramIo Io)
{
    /// <summary>The package cache, the index and GitHub, as a build reads them, with the packages the compiler ships
    /// seeded: the user's (see <c>SeededPackages.user</c>).</summary>
    public PackageStore Packages { get; init; } = SeededPackages.user();

    /// <summary>What <c>nanyx publish</c> and <c>nanyx unlist</c> send requests to GitHub with: none for GitHub itself,
    /// signed in with <c>GITHUB_TOKEN</c> or the GitHub CLI's login (see <c>GitHubApi.token</c>); the Wasm tests give a
    /// fake one.</summary>
    public FSharpFunc<GitHubRequest, FSharpResult<GitHubResponse, string>>? GitHub { get; init; }

    /// <summary>What stops a program the command serves, as `nanyx run` serves one whose host answers HTTP requests: none
    /// for the process's own, which Ctrl+C stops.</summary>
    public CancellationToken Stop { get; init; } = CancellationToken.None;

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
    public static int Parse(string inputFile) => ParseIn(Shell.Process, inputFile);

    internal static int ParseIn(Shell shell, string inputFile) => Execute(shell, inputFile, Target.Parse, outputPath: null);

    /// <summary>
    /// Check a Nanyx source file, or every source file of a package, and write nothing.
    /// </summary>
    /// <param name="path">A .nyx file, or a package's directory or .nyxp manifest; none means the package the current directory is in.</param>
    /// <param name="timings">Write how long each module took to check to stderr, as a tree of the modules each one imported. On a terminal, the tree is shown live while the compile runs.</param>
    /// <param name="host">The host to run the package's @entry under, in place of its manifest's host: a host package the compiler ships, such as nanyx.cli.host, or the directory of one. A script names its host with a ::host directive.</param>
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

        // A test package has only test files, so it is checked as its test build compiles them.
        if (ManifestModule.isTestPackage(package.Manifest))
        {
            var withTests = PackageModule.withTests(shell.Packages, package);
            if (withTests.IsError)
            {
                shell.Error.WriteLine($"error: the test host can't be read: {withTests.ErrorValue}");
                return 1;
            }

            package = withTests.ResultValue;
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
            shell.Error.WriteLine("`nanyx manifest` prints the manifest as JSON, so it needs `--json`: run `nanyx manifest --json`");
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
    /// Compile a Nanyx source file to WebAssembly: a .wasm module, or WebAssembly text with --wat.
    /// </summary>
    /// <param name="inputFile">The .nyx file to compile.</param>
    /// <param name="outputPath">Where to write the program; the default is the input's name with .wasm (or .wat), beside it.</param>
    /// <param name="release">Refuse any error. Without it, type and name errors are deferred: the program is written, and reports one if it reaches it.</param>
    /// <param name="wat">Write WebAssembly text (.wat) rather than the binary format (.wasm).</param>
    [Consolo("wasm")]
    public static int Wasm(
        string inputFile,
        [Consolo("out", Alias = "o")] string? outputPath = null,
        [Consolo("release")] bool release = false,
        [Consolo("wat")] bool wat = false)
    {
        return WasmIn(Shell.Process, inputFile, outputPath, release, wat ? ProgramFormat.Text : ProgramFormat.Binary);
    }

    /// <summary>What <c>nanyx wasm</c> does, run in <paramref name="shell"/>.</summary>
    internal static int WasmIn(Shell shell, string inputFile, string? outputPath, bool release, ProgramFormat format = ProgramFormat.Binary) =>
        Execute(shell, inputFile, Target.Wasm, outputPath, release, format: format);

    /// <summary>
    /// Run a Nanyx program: a .nyx file, or the @entry of a package.
    /// </summary>
    /// <param name="path">A .nyx file to run as a script, or a package's directory or .nyxp manifest, followed by the program's arguments; none means the package the current directory is in. Put -- before arguments that start with -, so that they are not read as run's options.</param>
    /// <param name="checkMemory">Check the program's reference counting as it runs: stop at a block freed twice or an object used after it was freed, and fail if anything is left on the heap at the end.</param>
    /// <param name="memory">Check the program's memory as --check-memory does, and once it stops, write to stderr how many blocks it allocated and freed, how many objects it built in the block of one it gave up, how many bytes it allocated, the most it held at once, and how far the heap grew.</param>
    /// <param name="timings">Write how long each module took to check to stderr, as a tree of the modules each one imported, and how long the program took to emit. On a terminal, the tree is shown live while the compile runs.</param>
    /// <param name="host">The host to run the package's @entry under, in place of its manifest's host: a host package the compiler ships, such as nanyx.cli.host, or the directory of one. A script names its host with a ::host directive.</param>
    /// <param name="noInline">Compile every call as a call, without inlining: for working on the compiler, to tell whether a misbehaving program is the inliner's doing.</param>
    /// <param name="yieldAtEveryWait">Make every call that can wait stop once and be resumed: for working on the compiler, to run each way a function that waits has of stopping and carrying on.</param>
    /// <param name="allowDir">A directory the program may read and write besides the current directory, which it always may. A path outside them is #permissionDenied.</param>
    /// <param name="profile">Sample the run with Wasmtime's guest profiler and write the profile to this file, in the Firefox profiler's format, to open at profiler.firefox.com.</param>
    /// <param name="dbgTo">Write each dbg the program reaches to this file, made anew, as a line of JSON (its file, line, column, value and sequence number), and none to stderr: for an editor, to show the values where they are written.</param>
    /// <param name="showValues">With --dbg-to, write to its file each value the program's own code binds or gives as well, as a line whose kind is value: what each def that isn't a function binds, and what each statement gives that isn't (). A dbg's line has the kind dbg.</param>
    /// <param name="filesInMemory">Run the program with its files in memory: it reads the real files until it writes, appends to or deletes them, and nothing it does reaches the disk. The program is built from its host's root for this, which the console host has.</param>
    [Consolo("run")]
    public static int Run(
        [Consolo("check-memory")] bool checkMemory = false,
        [Consolo("memory")] bool memory = false,
        [Consolo("timings")] bool timings = false,
        [Consolo("host")] string? host = null,
        [Consolo("no-inline")] bool noInline = false,
        [Consolo("yield-at-every-wait")] bool yieldAtEveryWait = false,
        [Consolo("allow-dir")] string? allowDir = null,
        [Consolo("profile")] string? profile = null,
        [Consolo("dbg-to")] string? dbgTo = null,
        [Consolo("files-in-memory")] bool filesInMemory = false,
        [Consolo("show-values")] bool showValues = false,
        params string[] path)
    {
        var memoryCheck = memory ? MemoryCheck.Report : checkMemory ? MemoryCheck.Check : MemoryCheck.Off;
        return RunIn(Shell.Process, memoryCheck, path, timings, host, !noInline, allowDir is null ? [] : [allowDir], profile, yieldAtEveryWait, dbgTo, filesInMemory, showValues);
    }

    /// <summary>
    /// What <c>nanyx run</c> does, run in <paramref name="shell"/>. The program may read and write the shell's directory
    /// and each of <paramref name="allowedDirectories"/>. With <paramref name="profile"/>, the run is profiled into that
    /// file (see <see cref="RunProgram"/>).
    /// </summary>
    internal static int RunIn(Shell shell, MemoryCheck memoryCheck, string[] path, bool timings = false, string? host = null, bool inlineCalls = true, string[]? allowedDirectories = null, string? profile = null, bool yieldAtEveryWait = false, string? dbgTo = null, bool filesInMemory = false, bool showValues = false)
    {
        if (profile is not null && memoryCheck != MemoryCheck.Off)
        {
            shell.Error.WriteLine("error: --profile can't be given with --check-memory or --memory, whose checks would be in the profile: give one or the other.");
            return UsageError;
        }

        if (showValues && dbgTo is null)
        {
            shell.Error.WriteLine("error: --show-values writes the values to the file --dbg-to names: give it --dbg-to too.");
            return UsageError;
        }

        // What follows the program is its own: its arguments, which it reads through `Env`.
        var single = path.FirstOrDefault();
        var programArguments = path.Skip(1).ToArray();
        var directories = allowedDirectories?.Select(shell.FullPath).ToArray() ?? [];
        if (directories.FirstOrDefault(directory => !Directory.Exists(directory)) is { } missing)
        {
            shell.Error.WriteLine($"error: --allow-dir names {shell.DisplayPath(missing)}, which is not a directory.");
            return UsageError;
        }

        if (single is not null && !IsPackagePath(shell, single))
        {
            return NoProgramInTestFile(shell, single)
                ?? NoHostForScript(shell, host)
                ?? Execute(shell, single, Target.Run, outputPath: null, memoryCheck: memoryCheck, timings: timings, programArguments: programArguments, inlineCalls: inlineCalls, allowedDirectories: directories, profile: profile, yieldAtEveryWait: yieldAtEveryWait, dbgTo: dbgTo, filesInMemory: filesInMemory, showValues: showValues);
        }

        if (!TryUpdateLockfile(shell, single, frozen: false, out var failed) || !TryFindPackage(shell, single, host, out var package, out failed))
        {
            return failed;
        }

        if (NoProgramInTestPackage(shell, package) is { } testPackage)
        {
            return testPackage;
        }

        if (filesInMemory)
        {
            if (PackageModule.inMemory(package) is not { } inMemory)
            {
                return NoFilesInMemory(shell, package.Host);
            }

            package = inMemory.Value;
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
            var nothing = package.Host is null
                ? $"It names no host, so it is a library, which can't be run: name the host its `@entry` runs under in its manifest, as `host = (package = \"{Hosts.consolePackage}\", version = \"{Toolchain.version}\")`"
                : "It has no `@entry`, so there is nothing to run";
            PrintDiagnostics(compiling, General(DiagnosticCodes.NothingToRun, package.Manifest.Name, nothing), compiling.Error);
            return 1;
        }

        ReportDeferredRun(compiling, deferred, build.Diagnostics);
        return RunProgram(compiling, entry.Value, deferred, memoryCheck, timings, [package.Manifest.Name, .. programArguments], inlineCalls, directories, HostSettings.port(package.Host, package.Manifest.Host), package.Manifest.Root, profile, yieldAtEveryWait, dbgTo, showValues ? package.Manifest.Root : null);
    }

    /// <summary>
    /// Build a Nanyx package: check every source file the manifest selects, and write its @entry program as a WebAssembly module (.wasm), or as WebAssembly text with --wat.
    /// </summary>
    /// <param name="path">The package's directory or .nyxp manifest; none means the package the current directory is in.</param>
    /// <param name="outputPath">Where to write the program; the default is out/&lt;package name&gt;.wasm (or .wat) beside the manifest.</param>
    /// <param name="wat">Write WebAssembly text (.wat) rather than the binary format (.wasm): the core module, which a component wraps.</param>
    /// <param name="core">Write a core module, as nanyx run runs, rather than the WASI 0.3 component that a host with a component root otherwise has written.</param>
    /// <param name="release">Refuse any error. Without it, type and name errors are deferred: the program is written, and reports one if it reaches it.</param>
    /// <param name="timings">Write how long each module took to check to stderr, as a tree of the modules each one imported, and how long the program took to emit. On a terminal, the tree is shown live while the compile runs.</param>
    /// <param name="host">The host to run the package's @entry under, in place of its manifest's host: a host package the compiler ships, such as nanyx.cli.host, or the directory of one. A script names its host with a ::host directive.</param>
    /// <param name="noInline">Compile every call as a call, without inlining: for working on the compiler, to tell whether a misbehaving program is the inliner's doing.</param>
    /// <param name="locked">Refuse to change nanyx.lock: a manifest that asks for something it doesn't lock is an error, as CI wants. Without it, the build chooses versions again and updates nanyx.lock, as nanyx pull does.</param>
    [Consolo("build")]
    public static int Build(
        [Consolo("out", Alias = "o")] string? outputPath = null,
        [Consolo("release")] bool release = false,
        [Consolo("locked")] bool locked = false,
        [Consolo("timings")] bool timings = false,
        [Consolo("host")] string? host = null,
        [Consolo("no-inline")] bool noInline = false,
        [Consolo("wat")] bool wat = false,
        [Consolo("core")] bool core = false,
        params string[] path)
    {
        return BuildIn(Shell.Process, outputPath, release, path, timings, host, !noInline, wat ? ProgramFormat.Text : ProgramFormat.Binary, locked, core);
    }

    /// <summary>What <c>nanyx build</c> does, run in <paramref name="shell"/>.</summary>
    internal static int BuildIn(Shell shell, string? outputPath, bool release, string[] path, bool timings = false, string? host = null, bool inlineCalls = true, ProgramFormat format = ProgramFormat.Binary, bool locked = false, bool core = false)
    {
        if (!TrySinglePath(shell, path, out var single))
        {
            return UsageError;
        }

        if (!TryUpdateLockfile(shell, single, locked, out var failed) || !TryFindPackage(shell, single, host, out var package, out failed))
        {
            return failed;
        }

        if (NoProgramInTestPackage(shell, package) is { } testPackage)
        {
            return testPackage;
        }

        // A host with a component root builds a WASI 0.3 component from it, unless a core module is asked for.
        var asComponent = core ? null : PackageModule.asComponent(package);
        var component = asComponent?.Value.Host.Value.Component.Value;
        if (asComponent is not null)
        {
            package = asComponent.Value;
        }

        var build = BuildPackage(shell, package, timings);
        PrintDiagnostics(shell, build.Diagnostics, shell.Out);
        var deferred = !release && ErrorsDeferred(build);
        if (HasErrors(build.Diagnostics) && !deferred)
        {
            return 1;
        }

        var entry = PackageModule.entryResult(build);
        // A package with no @entry whose modules mark defs @component is a library component, built from a root that
        // imports those modules, in the host's world for libraries.
        var library = entry is null && component is not null ? PackageModule.libraryResult(build) : null;
        if (library is not null)
        {
            if (component!.Library is null)
            {
                shell.Error.WriteLine($"{package.Manifest.Name} has @component defs and no @entry, so it is a library component, but its host {package.Host.Value.Package} has no world for libraries: name one in its manifest, as `component = (..., library = \"nanyx:console/library\")`, or build it with --core.");
                return 1;
            }

            // The root is made up, so an error in it is the compiler's; the package's own were printed above.
            if (!library.Value.Diagnostics.IsEmpty)
            {
                PrintDiagnostics(shell, library.Value.Diagnostics, shell.Out);
                if (HasErrors(library.Value.Diagnostics))
                {
                    return 1;
                }
            }

            entry = library;
        }

        if (entry is null)
        {
            // With no program to write, the build is only a check, and errors fail it.
            shell.Out.WriteLine($"{Checked(package)}; it has no @entry, so no program was written.");
            return deferred ? 1 : 0;
        }

        // The functions the component exports, from the program's definitions marked @component, in the WIT package
        // the manifest's name and version give.
        var witPackage = ComponentInterface.packageName(package.Manifest.Name, package.Manifest.Version);
        var functions = component is null || entry.Value.Typed is null
            ? FSharpList<ComponentFunction>.Empty
            : ComponentInterface.functions(entry.Value.Typed.Value);
        // And the functions it imports, from its definitions marked @import that it calls.
        var imports = component is null || entry.Value.Typed is null
            ? FSharpList<ComponentFunction>.Empty
            : ComponentInterface.imports(entry.Value.Typed.Value);
        var exports = ListModule.OfSeq(functions.Select(function => Tuple.Create(ComponentInterface.exportName(witPackage, function.Export), function)));
        if (!TryEmitWat(shell, entry.Value, shell.Out, out var wat, new CodeGen.WasmOptions(false, inlineCalls, release, SourceRoot(package.Manifest.Root), true, false, crashToRuntime: false, dbgToEmbedder: false, exportStart: false, component: component is null ? null : FSharpOption<ComponentEntry>.Some(library is null ? ComponentEntryModule.ofWorld(component.World) : ComponentEntry.Library), componentExports: exports, yieldAtEveryWait: false, frameDrops: false, showValuesIn: Microsoft.FSharp.Collections.FSharpList<string>.Empty), deferred, timings))
        {
            return 1;
        }

        var outputFile = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(package.Manifest.Root, "out", package.Manifest.Name + Extension(format))
            : outputPath!;
        var written = component is null || format == ProgramFormat.Text
            ? WriteProgram(shell, wat, format, outputFile)
            : WriteComponent(shell, wat, package.Host.Value, library is null ? component.World : component.Library!.Value, witPackage, functions, imports, outputFile);
        if (written is null)
        {
            return 1;
        }

        var world = component is null ? null : functions.IsEmpty && imports.IsEmpty && library is null ? component.World : ComponentInterface.qualified(witPackage, ComponentInterface.worldName(functions));
        var kind = world is null ? "" : $", a component of {world}";
        shell.Out.WriteLine($"Built {package.Manifest.Name} ({FileCount(package)}){WithDeferred(deferred, build.Diagnostics)}{kind}: {written}");
        return 0;
    }

    /// <summary>
    /// Writes the program <paramref name="wat"/>, the core module of a component, to <paramref name="outputPath"/> as a
    /// component of <paramref name="hostWorld"/>, a world of <paramref name="host"/>'s component root, and gives the path
    /// as the shell shows it; <c>null</c>, said on the shell's stderr, when it can't be built, with wit-component's
    /// reason. A program that exports <paramref name="functions"/> of its own, or imports <paramref name="imports"/>, is a
    /// component of a world of <paramref name="witPackage"/>'s that includes the host's, whose WIT is written to a wit
    /// directory beside the component.
    /// </summary>
    private static string? WriteComponent(Shell shell, string wat, Host host, string hostWorld, string witPackage, FSharpList<ComponentFunction> functions, FSharpList<ComponentFunction> imports, string outputPath)
    {
        var fullPath = shell.FullPath(outputPath);
        var witDirectory = host.WitDirectory;
        var world = hostWorld;
        if (!functions.IsEmpty || !imports.IsEmpty)
        {
            witDirectory = Path.Combine(Path.GetDirectoryName(fullPath)!, "wit");
            world = ComponentInterface.write(host.WitDirectory, hostWorld, witPackage, functions, imports, witDirectory);
        }

        if (!TryAssemble(shell.Error, wat, out var wasm))
        {
            return null;
        }

        var built = Componentizer.componentize(wasm, witDirectory, world);
        if (built.IsError)
        {
            shell.Error.WriteLine($"The program can't be built as a component of {hostWorld}: {built.ErrorValue}");
            shell.Error.WriteLine("Build it with --core for a core module, as nanyx run runs.");
            return null;
        }

        return WriteOutput(shell, fullPath, path => File.WriteAllBytes(path, built.ResultValue));
    }

    /// <summary>
    /// Assembles the program <paramref name="wat"/> into <paramref name="wasm"/>; false, said on <paramref name="errors"/>,
    /// when the text can't be assembled, which is a bug in the compiler.
    /// </summary>
    internal static bool TryAssemble(TextWriter errors, string wat, out byte[] wasm)
    {
        try
        {
            wasm = WasmBinary.assemble(wat);
            return true;
        }
        catch (Exception ex) when (ex is WasmBinary.WasmAssemblyError or WatSyntax.WatSyntaxError)
        {
            errors.WriteLine($"Internal error in the compiler, whose WebAssembly can't be assembled ({ex.Message}): report it at https://github.com/nanyx-lang/nanyx/issues");
            wasm = [];
            return false;
        }
    }

    /// <summary>
    /// Writes a build's output to <paramref name="fullPath"/> with <paramref name="write"/>, making its directory if need
    /// be, and gives the path as the shell shows it.
    /// </summary>
    private static string WriteOutput(Shell shell, string fullPath, Action<string> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        write(fullPath);
        return shell.DisplayPath(fullPath);
    }

    /// <summary>
    /// The exit code of a command used wrongly: arguments missing or too many, an option's value not one it takes, or a
    /// path that does not exist. A command exits with 1 when the code it was given has errors or its program fails.
    /// </summary>
    private const int UsageError = 2;

    /// <summary>
    /// Writes the program <paramref name="wat"/> to <paramref name="outputPath"/> in <paramref name="format"/>, making its
    /// directory if need be, and gives the path as the shell shows it; <c>null</c>, said on the shell's stderr, when the
    /// text can't be assembled, which is a bug in the compiler.
    /// </summary>
    private static string? WriteProgram(Shell shell, string wat, ProgramFormat format, string outputPath)
    {
        var fullPath = shell.FullPath(outputPath);
        if (format != ProgramFormat.Binary)
        {
            return WriteOutput(shell, fullPath, path => File.WriteAllText(path, wat));
        }

        return TryAssemble(shell.Error, wat, out var wasm) ? WriteOutput(shell, fullPath, path => File.WriteAllBytes(path, wasm)) : null;
    }

    /// <summary>The extension of a program written in <paramref name="format"/>: <c>.wasm</c> or <c>.wat</c>.</summary>
    private static string Extension(ProgramFormat format) => format == ProgramFormat.Binary ? ".wasm" : ".wat";

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
            shell.Error.WriteLine($"{paths.Length} paths were given: give one file, directory or manifest");
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
            shell.Error.WriteLine($"'{path}' is neither a directory nor a {ManifestModule.extension} manifest");
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
            shell.Error.WriteLine($"There is no {ManifestModule.extension} manifest in '{full}' or any directory above it");
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
    internal static CompileResult CompileScript(Shell shell, string inputFile, bool timings, bool filesInMemory = false)
    {
        using var progress = CompileProgress.Start(shell.Error, shell.DisplayPath(shell.FullPath(inputFile)), shell.DisplayPath, timings);
        // With its files in memory, the script is built from its host's root for that (see `Host.InMemory`).
        var result = filesInMemory
            ? Script.compileFileRehostedMakingIn(FuncConvert.FromFunc<Host, Host>(host => host.InMemory?.Value ?? host), LintRunners.MakerFor(shell.Packages), shell.Packages, progress.Listener, shell.FullPath(inputFile))
            : Script.compileFileMakingIn(LintRunners.MakerFor(shell.Packages), shell.Packages, progress.Listener, shell.FullPath(inputFile));
        progress.Finish();
        return result;
    }

    /// <summary>
    /// Downloads into the package cache the packages from the index that the script <paramref name="inputFile"/> pins
    /// with its <c>::install</c> lines, and the packages they depend on, unless the cache holds them all already (see
    /// <c>Script.pullForRun</c>); false, with each error reported, if they can't be. With <paramref name="listPulled"/>,
    /// as for <c>nanyx pull</c>, it reads the index whatever the cache holds (see <c>Script.pull</c>), and lists each
    /// package on stdout. A file that isn't there is left for its compile to report.
    /// </summary>
    internal static bool TryPullScript(Shell shell, string inputFile, bool listPulled = false)
    {
        var path = shell.FullPath(inputFile);
        if (!File.Exists(path))
        {
            return true;
        }

        var store = PackageStoreModule.downloading(shell.Packages);
        var source = File.ReadAllText(path);
        var pulled = listPulled ? Script.pull(store, path, source) : Script.pullForRun(store, path, source);
        if (pulled.IsError)
        {
            foreach (var error in pulled.ErrorValue)
            {
                shell.Error.WriteLine($"error: {error}");
            }

            return false;
        }

        if (listPulled)
        {
            foreach (var line in pulled.ResultValue.Lines)
            {
                shell.Out.WriteLine(line);
            }
        }

        PrintWarnings(shell, pulled.ResultValue.Warnings);
        return true;
    }

    /// <summary>Whether a path names a package (its directory or manifest) rather than a file to run as a script.</summary>
    private static bool IsPackagePath(Shell shell, string path) =>
        Directory.Exists(shell.FullPath(path)) || path.EndsWith(ManifestModule.extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An error, reported, when <paramref name="package"/> is a test package, which has no program to run or build: only
    /// <c>nanyx test</c> builds one. Null for any other package.
    /// </summary>
    private static int? NoProgramInTestPackage(Shell shell, Package package)
    {
        if (!ManifestModule.isTestPackage(package.Manifest))
        {
            return null;
        }

        var why = "It is a test package, which holds the tests of the packages it depends on and no program: run them with `nanyx test`";
        PrintDiagnostics(shell, General(DiagnosticCodes.NothingToRun, package.Manifest.Name, why), shell.Error);
        return 1;
    }

    /// <summary>
    /// An error, reported, when the file <paramref name="path"/> is a test file of a package, which only its test build
    /// has: it is no script, and has no program to run. Null for any other file.
    /// </summary>
    private static int? NoProgramInTestFile(Shell shell, string path)
    {
        if (PackageModule.testFileOf(shell.FullPath(path)) is not { } tested)
        {
            return null;
        }

        var why = $"It is a test file of {tested.Value.Name}, which its manifest's `tests` selects, and has no program: run its tests with `nanyx test {path}`";
        PrintDiagnostics(shell, General(DiagnosticCodes.NothingToRun, shell.DisplayPath(shell.FullPath(path)), why), shell.Error);
        return 1;
    }

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

        shell.Error.WriteLine($"`--host` chooses a package's host, and a script names its own with a `::host` directive, as `::host acme.server ../server`, or runs under '{Hosts.consolePackage}' without one: leave out `--host`");
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

        var chosen = PackageModule.withHost(shell.Packages, shell.Directory, host, package);
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
    /// Brings the lockfile of the package a path names, or of the one the shell's directory is in, up to date before it
    /// is built, as <see cref="PackageDependencies.update"/> does: when the manifest asks for something it doesn't lock,
    /// chooses versions again, writes it and downloads what it locks, saying so on stderr. With <paramref name="frozen"/>
    /// (<c>--locked</c>), that is an error. A path that names no package is left for the build to report.
    /// </summary>
    private static bool TryUpdateLockfile(Shell shell, string? path, bool frozen, out int failed)
    {
        failed = 0;
        var full = path is null ? shell.Directory : shell.FullPath(path);
        string? manifestPath = null;
        if (File.Exists(full) && full.EndsWith(ManifestModule.extension, StringComparison.OrdinalIgnoreCase))
        {
            manifestPath = full;
        }
        else if (Directory.Exists(full) && ManifestModule.tryFind(full) is { IsOk: true } found && found.ResultValue is { } inside)
        {
            manifestPath = inside.Value;
        }

        if (manifestPath is null)
        {
            return true;
        }

        var updated = PackageDependencies.update(PackageStoreModule.downloading(shell.Packages), manifestPath, frozen);
        if (updated.IsError)
        {
            PrintLockFailure(shell, updated.ErrorValue);
            failed = 1;
            return false;
        }

        foreach (var line in updated.ResultValue.Lines)
        {
            shell.Error.WriteLine(line);
        }

        PrintWarnings(shell, updated.ResultValue.Warnings);
        return true;
    }

    /// <summary>
    /// Reports on stderr each of <paramref name="warnings"/>, once, such as a version a build uses that the index has
    /// unlisted (<c>NYX0051</c>); the command goes on.
    /// </summary>
    private static void PrintWarnings(Shell shell, FSharpList<Diagnostic> warnings)
    {
        if (!warnings.IsEmpty)
        {
            PrintDiagnostics(shell, ListModule.Distinct(warnings), shell.Error);
        }
    }

    /// <summary>Reports why a lockfile can't be brought up to date: a version conflict as a diagnostic, and anything
    /// else as errors.</summary>
    private static void PrintLockFailure(Shell shell, LockFailure failure)
    {
        if (failure is LockFailure.NoVersions conflict)
        {
            PrintDiagnostics(shell, ListModule.OfSeq([conflict.Item]), shell.Error);
            return;
        }

        foreach (var error in ((LockFailure.LockErrors)failure).Item)
        {
            shell.Error.WriteLine($"error: {error}");
        }
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
            shell.Error.WriteLine($"There is no file or directory '{path}'");
            return false;
        }

        var found = PackageModule.findIn(shell.Packages, path is null ? shell.Directory : shell.FullPath(path));
        if (found.IsError)
        {
            foreach (var error in found.ErrorValue)
            {
                shell.Error.WriteLine(error);
            }

            failed = 1;
            return false;
        }

        // Its linter packages run as lint builds, which the CLI can build and run and the compiler's core can't.
        package = PackageModule.withLinters(LintRunners.MakerFor(shell.Packages), found.ResultValue);
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
            Console.Error.WriteLine("`--check` and `--write` were both given: use one of them");
            return UsageError;
        }

        if (stdin && paths.Length > 0)
        {
            Console.Error.WriteLine("`--stdin` and files were both given: format one or the other");
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
                        Console.Error.WriteLine($"{shell.DisplayPath(file)}: Internal error in the formatter ({notSafe.Item}), so the file was left as it is: report it at https://github.com/nanyx-lang/nanyx/issues");
                        break;
                    // A directive's error is the file's own, and starts with where in it the directive is.
                    case Nanyx.Formatter.FormatCommand.Formatting.BadSettings badSettings when badSettings.Item.StartsWith(file + ":", StringComparison.Ordinal):
                        Console.Error.WriteLine($"{shell.DisplayPath(file)}{badSettings.Item[file.Length..]}, so the file was left as it is");
                        break;
                    case Nanyx.Formatter.FormatCommand.Formatting.BadSettings badSettings:
                        if (settingsErrors.Add(badSettings.Item))
                        {
                            Console.Error.WriteLine($"{badSettings.Item}, so no file under it was formatted");
                        }
                        break;
                }
            }
        }

        return Nanyx.Formatter.FormatCommand.exitCode(mode, results);
    }

    /// <summary>
    /// Write the reference of each standard-library page on the docs site from the library's doc comments, and the
    /// list of the nominal types it exports beside them, nominal-types.json
    /// (see docs/src/content/deep-dives/standard-library-implementation.md).
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
            Console.Error.WriteLine($"'{directory}' is not a directory");
            return UsageError;
        }

        var failed = false;
        foreach (var (page, update) in StandardLibraryDocs.update(shell.Packages, path))
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
    /// Run the Nanyx language server over standard input and output (for editors; see docs/src/content/standard-tools/vs-code-extension.md).
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
            Console.Error.WriteLine($"`--top-level-type-hints` must be 'lens', 'inlay' or 'off', not '{topLevelTypeHints}'");
            return UsageError;
        }

        var settings = new Nanyx.LanguageServer.ServerSettings(
            ascii ? TypeNotation.Ascii : TypeNotation.Unicode,
            topLevel.Value,
            LocalTypeHints: !noLocalTypeHints,
            ParameterTypeHints: !noParameterTypeHints,
            PatternVariableTypeHints: !noPatternVariableTypeHints,
            Timings: timings,
            FormattingHints: !noFormatHints,
            LintRunner: LintRunners.Wasm);
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

    /// <summary>
    /// The error for <c>run --files-in-memory</c> with a program whose host, <paramref name="host"/>, has no root that runs
    /// it with its files in memory (see <c>Host.InMemory</c>).
    /// </summary>
    private static int NoFilesInMemory(Shell shell, FSharpOption<Host>? host)
    {
        var named = host is null ? "Its host" : $"The host '{host.Value.Package}'";
        shell.Error.WriteLine($"error: --files-in-memory needs a host that can run a program with its files in memory, and {named.ToLowerInvariant()[0]}{named[1..]} can't: a host declares the root that does it with `filesInMemory` in its manifest.");
        return UsageError;
    }

    /// <summary>"2 deferred errors": the errors among <paramref name="diagnostics"/>, every one of them deferred.</summary>
    private static string DeferredErrors(FSharpList<Diagnostic> diagnostics)
    {
        var count = diagnostics.Count(diagnostic => diagnostic.Severity.IsError);
        return $"{count} deferred error{(count == 1 ? "" : "s")}";
    }

    private static int Execute(Shell shell, string inputFile, Target target, string? outputPath, bool release = false, MemoryCheck memoryCheck = MemoryCheck.Off, bool timings = false, string[]? programArguments = null, bool inlineCalls = true, ProgramFormat format = ProgramFormat.Binary, string[]? allowedDirectories = null, string? profile = null, bool yieldAtEveryWait = false, string? dbgTo = null, bool filesInMemory = false, bool showValues = false)
    {
        if (string.IsNullOrWhiteSpace(inputFile))
        {
            shell.Error.WriteLine("No file was given: give a .nyx file");
            return UsageError;
        }

        if (!File.Exists(shell.FullPath(inputFile)))
        {
            PrintDiagnostics(shell, General(DiagnosticCodes.FileNotFound, inputFile, "There is no file at this path"), shell.Error);
            return UsageError;
        }

        if (target == Target.Parse)
        {
            return RunParse(shell, inputFile);
        }

        if (target == Target.Run)
        {
            return RunScript(shell, inputFile, memoryCheck, timings, programArguments, inlineCalls, allowedDirectories, profile, yieldAtEveryWait, dbgTo, filesInMemory, showValues);
        }

        // Script mode: the file's :: directives say what a package's manifest would.
        var result = CompileScript(shell, inputFile, timings);

        shell.Out.WriteLine($"Phase: {result.Phase}");
        PrintDiagnostics(shell, result.Diagnostics, shell.Out);

        return target switch
        {
            Target.Check => RunCheck(shell, result, inputFile),
            Target.Wasm => RunWasm(shell, result, inputFile, outputPath, release, format),
            _ => UnsupportedTarget(shell, target.ToString())
        };
    }

    /// <summary>
    /// What <c>nanyx run</c> does with the script <paramref name="inputFile"/>, which exists: compiles and runs it in
    /// <paramref name="shell"/>, and gives the exit code. The compiler's diagnostics and the run's own errors go to the
    /// shell's stderr. The program's command-line arguments are <paramref name="inputFile"/> followed by
    /// <paramref name="programArguments"/>, and it may read and write the shell's directory and each of
    /// <paramref name="allowedDirectories"/>, full paths. The Wasm tests call this to run programs in their own process.
    /// </summary>
    internal static int RunScript(Shell shell, string inputFile, MemoryCheck memoryCheck, bool timings = false, string[]? programArguments = null, bool inlineCalls = true, string[]? allowedDirectories = null, string? profile = null, bool yieldAtEveryWait = false, string? dbgTo = null, bool filesInMemory = false, bool showValues = false)
    {
        // `run` is the user-facing command: its stdout belongs to the program, so compiler chatter is kept off it, and
        // on a terminal it goes in the run's box.
        using var box = RunBox.For(shell, $"run {shell.DisplayPath(shell.FullPath(inputFile))}");
        var compiling = box is null ? shell : shell with { Error = box.Lines };

        // Script mode: the file's :: directives say what a package's manifest would. The packages from the index they
        // pin are downloaded first, as `nanyx pull` downloads them.
        if (!TryPullScript(compiling, inputFile))
        {
            return 1;
        }

        if (filesInMemory && Script.hostOf(compiling.Packages, compiling.FullPath(inputFile)) is { } host && host.Value.InMemory is null)
        {
            return NoFilesInMemory(compiling, host);
        }

        var result = CompileScript(compiling, inputFile, timings, filesInMemory);
        return RunCompiledScript(compiling, inputFile, result, memoryCheck, timings, programArguments, inlineCalls, allowedDirectories, profile, yieldAtEveryWait, dbgTo, showValues);
    }

    /// <summary>Runs the script <paramref name="inputFile"/>, compiled to <paramref name="result"/>, as <c>run</c> does
    /// once it has compiled it: so a caller can compile a script once and run it more than one way, as the tests of the
    /// docs' examples run each with and without inlining.</summary>
    internal static int RunCompiledScript(Shell shell, string inputFile, CompileResult result, MemoryCheck memoryCheck, bool timings = false, string[]? programArguments = null, bool inlineCalls = true, string[]? allowedDirectories = null, string? profile = null, bool yieldAtEveryWait = false, string? dbgTo = null, bool showValues = false)
    {
        PrintDiagnostics(shell, WorthStoppingFor(result.Diagnostics), shell.Error);
        // A debug run: type and name errors the compiler deferred do not stop it. Each is reported again if the
        // program reaches it.
        var deferred = ErrorsDeferred(result);
        ReportDeferredRun(shell, deferred, result.Diagnostics);
        return RunProgram(shell, result, deferred, memoryCheck, timings, [inputFile, .. programArguments ?? []], inlineCalls, allowedDirectories, HostSettings.port(Script.hostOf(shell.Packages, shell.FullPath(inputFile)), FSharpOption<HostEntry>.None), null, profile, yieldAtEveryWait, dbgTo, showValues ? shell.FullPath(inputFile) : null);
    }

    /// <summary>
    /// What <c>nanyx parse</c> does: parses <paramref name="inputFile"/> and prints its syntax tree. Parsing and printing
    /// the tree recurse once per level it nests, so both run on a large stack, as checking does.
    /// </summary>
    private static int RunParse(Shell shell, string inputFile) =>
        Nanyx.LargeStack.run(FuncConvert.FromFunc(() => ParseAndPrint(shell, inputFile)));

    private static int ParseAndPrint(Shell shell, string inputFile)
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

        // As Rust lays out its errors: the severity, code and headline, then where it is, as editors and terminals
        // link to a place, then the line of source with the span underlined (see `SourceExcerpt`), then the body, the
        // related locations and the suggestions:
        //
        //     error NYX0205: Missing context 'Console'
        //       --> wrapped.nyx:4:3
        //        |
        //      4 |   println("hi")
        //        |   ^^^^^^^
        //        = 'println' requires the context 'Console', which 'wrapped' does not declare: ...
        //
        // A related location, a place that explains the error, is shown the same way under a note with its label:
        //
        //        = note: 'x' is first defined here
        //       --> main.nyx:1:5
        //        |
        //      1 | def x = 1
        //        |     ^
        //
        // On a terminal, the severity, headline and underline are in its colour. In the run's box, only the gutter is
        // dimmed, so diagnostics keep their colours.
        var box = (writer as RunBox.Writer)?.Box;
        writer = box?.Diagnostics ?? writer;
        var colour = box?.InColour ?? Terminal.InColour(writer);
        var sources = new Dictionary<string, string?>();
        var first = true;
        foreach (var diagnostic in diagnostics)
        {
            // A blank line between diagnostics, so each is seen as one.
            if (!first)
            {
                writer.WriteLine();
            }

            first = false;
            // A critical diagnostic, such as a missing file, is an error that stops everything: it is shown as one.
            var severity = diagnostic.Severity.IsCritical ? "error" : diagnostic.Severity.ToString().ToLowerInvariant();
            var escape = colour ? SeverityColour(diagnostic.Severity) : null;
            string Coloured(string text) => escape is null ? text : $"\u001b[{escape}m{text}\u001b[0m";
            writer.WriteLine($"{Coloured($"{severity} {diagnostic.Code}: {Diagnostics.headline(diagnostic)}")}");

            // The gutter is as wide as the widest line number shown, so that every excerpt's bars line up.
            var gutter = new string(' ', new[] { diagnostic.Span?.Value }
                .Concat(diagnostic.Related.Select(related => (Parser.Program.SourceSpan?)related.Item1))
                .Where(span => span is not null)
                .Select(span => (span!.StartLine + 1).ToString().Length)
                .DefaultIfEmpty(0)
                .Max());
            if (diagnostic.Span is not null)
            {
                WritePlace(diagnostic.Span.Value);
            }

            writer.WriteLine($" {gutter} = {diagnostic.Message}");
            foreach (var related in diagnostic.Related)
            {
                writer.WriteLine($" {gutter} = note: {related.Item2}");
                WritePlace(related.Item1);
            }

            foreach (var suggestion in diagnostic.Suggestions)
            {
                writer.WriteLine($" {gutter} = help: {suggestion}");
            }

            // Where `span` is, then its line with the span underlined, if its file can be read.
            void WritePlace(Parser.Program.SourceSpan span)
            {
                if (!sources.TryGetValue(span.File, out var source))
                {
                    source = SourceExcerpt.SourceOf(span.File);
                    sources[span.File] = source;
                }

                writer.WriteLine($" {gutter}--> {FormatLocation(shell, span)}");
                foreach (var line in source is null ? [] : SourceExcerpt.Lines(span, source, Coloured, gutter.Length))
                {
                    writer.WriteLine($" {line}");
                }
            }
        }
    }

    /// <summary>
    /// The diagnostic <paramref name="code"/> about the whole command, not a place in a file, its headline naming
    /// <paramref name="subject"/>, as the one diagnostic to print.
    /// </summary>
    private static FSharpList<Diagnostic> General(DiagnosticCode code, string subject, string message) =>
        ListModule.Singleton(Diagnostics.about(subject, Diagnostics.general(code, message)));

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

    private static int RunWasm(Shell shell, CompileResult result, string inputFile, string? outputPath, bool release, ProgramFormat format)
    {
        var deferred = !release && ErrorsDeferred(result);
        if (!TryEmitWat(shell, result, shell.Out, out var wat, new CodeGen.WasmOptions(false, true, release, SourceRoot(null), true, false, crashToRuntime: false, dbgToEmbedder: false, exportStart: false, component: null, componentExports: FSharpList<Tuple<string, ComponentFunction>>.Empty, yieldAtEveryWait: false, frameDrops: false, showValuesIn: Microsoft.FSharp.Collections.FSharpList<string>.Empty), deferred))
        {
            return 1;
        }

        var written = WriteProgram(shell, wat, format, string.IsNullOrWhiteSpace(outputPath) ? DefaultWasmOutputPath(inputFile, format) : outputPath!);
        if (written is null)
        {
            return 1;
        }

        var what = format == ProgramFormat.Binary ? "WebAssembly" : "WebAssembly text";
        shell.Out.WriteLine($"{what} written{WithDeferred(deferred, result.Diagnostics)}: {written}");
        return 0;
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

    private static string DefaultWasmOutputPath(string inputFile, ProgramFormat format)
    {
        var directory = Path.GetDirectoryName(inputFile) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(inputFile);
        return Path.Combine(directory, stem + Extension(format));
    }

    private static int UnsupportedTarget(Shell shell, string target)
    {
        shell.Error.WriteLine($"Unknown target '{target}'");
        return UsageError;
    }
}
