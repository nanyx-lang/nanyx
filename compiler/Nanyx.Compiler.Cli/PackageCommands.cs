using Consolo;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using NyxCompiler;

/// <summary>
/// The commands that make a package and manage its dependencies: <c>nanyx migrate</c>, <c>nanyx pull</c>, <c>nanyx api-check</c>,
/// <c>nanyx install</c>, <c>nanyx pack</c> and <c>nanyx bump</c>. What they do is in the compiler (<see cref="MigrationModule"/>,
/// <see cref="PackageDependencies"/>, <see cref="PackageArchive"/>, <see cref="PackageApiModule"/>); these parse their arguments, write what they make
/// and say what happened.
/// </summary>
internal static partial class CliCommands
{
    /// <summary>
    /// Turn a script, or a directory of scripts, into a package: write a .nyxp manifest from what their :: directives say, and make each file a module. A script with top-level statements becomes the package's @entry.
    /// </summary>
    /// <param name="name">The package's name: lower case, with words separated by dots, as in acme.web. The default is the directory's name.</param>
    /// <param name="path">A .nyx script, or a directory whose .nyx files are migrated together; none means the current directory.</param>
    [Consolo("migrate")]
    public static int Migrate([Consolo("name")] string? name = null, params string[] path) => MigrateIn(Shell.Process, name, path);

    /// <summary>What <c>nanyx migrate</c> does, run in <paramref name="shell"/>.</summary>
    internal static int MigrateIn(Shell shell, string? name, string[] path)
    {
        if (!TrySinglePath(shell, path, out var single))
        {
            return UsageError;
        }

        var migrated = MigrationModule.migrate(Toolchain.version, shell.FullPath(single ?? "."), name is null ? FSharpOption<string>.None : FSharpOption<string>.Some(name));
        if (migrated.IsError)
        {
            foreach (var error in migrated.ErrorValue)
            {
                shell.Error.WriteLine($"error: {error}");
            }

            return 1;
        }

        var migration = migrated.ResultValue;
        File.WriteAllText(migration.ManifestPath, migration.Manifest);
        foreach (var file in migration.Files)
        {
            File.WriteAllText(file.Item1, Formatted(file.Item1, file.Item2));
        }

        var count = migration.Files.Length == 1 ? "1 file" : $"{migration.Files.Length} files";
        shell.Out.WriteLine($"Migrated {count} to a package: {shell.DisplayPath(migration.ManifestPath)}");
        foreach (var note in migration.Notes)
        {
            shell.Out.WriteLine($"note: {note}");
        }

        return 0;
    }

    /// <summary><paramref name="text"/>, the new text of the file at <paramref name="file"/>, as the formatter lays it out under
    /// its package's settings; as it is if it can't be formatted.</summary>
    private static string Formatted(string file, string text)
    {
        var options = Nanyx.Formatter.Settings.forFile(file);
        return options.IsOk && Nanyx.Formatter.FormatCommand.formatText(options.ResultValue, file, text) is Nanyx.Formatter.FormatCommand.Formatting.Formatted formatted
            ? formatted.Item
            : text;
    }

    /// <summary>
    /// Make a package's dependencies available to build: when the manifest asks for something nanyx.lock doesn't lock, choose the newest versions that satisfy every dependency, keeping those it locks while they still do, and write nanyx.lock; then download into the package cache each package it locks that isn't there yet, check that each dependency is there, and list them. For a script, download the exact versions its ::dependency lines pin, and the packages they depend on.
    /// </summary>
    /// <param name="path">A package's directory or .nyxp manifest, or a script; none means the package the current directory is in.</param>
    [Consolo("pull")]
    public static int Pull(params string[] path) => PullIn(Shell.Process, path);

    /// <summary>What <c>nanyx pull</c> does, run in <paramref name="shell"/>.</summary>
    internal static int PullIn(Shell shell, string[] path)
    {
        if (!TrySinglePath(shell, path, out var single))
        {
            return UsageError;
        }

        if (single is not null && single.EndsWith(".nyx", StringComparison.OrdinalIgnoreCase) && File.Exists(shell.FullPath(single)))
        {
            return TryPullScript(shell, single, listPulled: true) ? 0 : 1;
        }

        if (!TryFindManifest(shell, single, out var manifestPath, out var failed))
        {
            return failed;
        }

        var pulled = PackageDependencies.pull(PackageStoreModule.downloading(shell.Packages), manifestPath);
        if (pulled.IsError)
        {
            PrintLockFailure(shell, pulled.ErrorValue);
            return 1;
        }

        foreach (var line in pulled.ResultValue.Lines)
        {
            shell.Out.WriteLine(line);
        }

        PrintWarnings(shell, pulled.ResultValue.Warnings);
        return 0;
    }

    /// <summary>
    /// Pack a package as a .nyxpkg, the file a release of it carries: a zip of its manifest, its source files and its api.json, the types of what its public modules export. A package with a dependency found by its path, or on GitHub, can't be packed.
    /// </summary>
    /// <param name="output">The directory to write it to; the package's own by default. Its name is &lt;name&gt;-&lt;version&gt;.nyxpkg.</param>
    /// <param name="path">A package's directory or .nyxp manifest; none means the package the current directory is in.</param>
    [Consolo("pack")]
    public static int Pack([Consolo("out", Alias = "o")] string? output = null, params string[] path) => PackIn(Shell.Process, output, path);

    /// <summary>What <c>nanyx pack</c> does, run in <paramref name="shell"/>.</summary>
    internal static int PackIn(Shell shell, string? output, string[] path)
    {
        if (!TrySinglePath(shell, path, out var single))
        {
            return UsageError;
        }

        if (!TryFindPackage(shell, single, out var package, out var failed))
        {
            return failed;
        }

        var packed = PackageApiModule.pack(package);
        if (packed.IsError)
        {
            PrintDiagnostics(shell, packed.ErrorValue, shell.Error);
            return 1;
        }

        var directory = output is null ? package.Manifest.Root : shell.FullPath(output);
        System.IO.Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, PackageArchive.fileName(package.Manifest));
        File.WriteAllBytes(file, packed.ResultValue);
        shell.Out.WriteLine($"Packed {package.Manifest.Name} {package.Manifest.Version}: {shell.DisplayPath(file)}");
        shell.Out.WriteLine($"sha256 {PackageArchive.sha256(packed.ResultValue)}");
        return 0;
    }

    /// <summary>
    /// Set a package's version to the one the changes to its public API need, as semantic versioning says: compared with the newest version of it published, a removed or changed export needs a major bump, an added one a minor bump, and no change a patch. Below 1.0.0, a minor bump for a breaking change and a patch for any other.
    /// </summary>
    /// <param name="path">A package's directory or .nyxp manifest; none means the package the current directory is in.</param>
    [Consolo("bump")]
    public static int Bump(params string[] path) => BumpIn(Shell.Process, path);

    /// <summary>What <c>nanyx bump</c> does, run in <paramref name="shell"/>.</summary>
    internal static int BumpIn(Shell shell, string[] path)
    {
        if (!TrySinglePath(shell, path, out var single))
        {
            return UsageError;
        }

        if (!TryFindPackage(shell, single, out var package, out var failed))
        {
            return failed;
        }

        var bumped = PackageApiModule.bump(shell.Packages, package);
        if (bumped.IsError)
        {
            foreach (var error in bumped.ErrorValue)
            {
                shell.Error.WriteLine($"error: {error}");
            }

            return 1;
        }

        var result = bumped.ResultValue;
        var name = package.Manifest.Name;
        if (result.Baseline?.Value is not { } baseline)
        {
            shell.Out.WriteLine($"{name} has no version published before {package.Manifest.Version}, so there is nothing to compare: its version stays {result.Version}");
            return 0;
        }

        PrintChanges(shell, result.Changes);

        if (result.Changes.IsEmpty)
        {
            shell.Out.WriteLine($"Nothing another package sees has changed since {baseline}");
        }

        if (result.Version == package.Manifest.Version)
        {
            shell.Out.WriteLine($"{name} {result.Version} is already the version these changes need, after {baseline}");
            return 0;
        }

        File.WriteAllText(package.Manifest.Path, result.Manifest);
        shell.Out.WriteLine($"{name} {package.Manifest.Version} -> {result.Version}, after {baseline}: written to {shell.DisplayPath(package.Manifest.Path)}");
        return 0;
    }

    /// <summary>
    /// Check that a new version of a package is a big enough bump from an older one, as the package index checks a release before it records it: compare the API each .nyxpkg's api.json records, list the changes, and fail when the newer version's number is too small for them.
    /// </summary>
    /// <param name="older">The .nyxpkg of the older version: the newest published below the newer one.</param>
    /// <param name="newer">The .nyxpkg of the newer version.</param>
    [Consolo("api-check")]
    public static int ApiCheck(string older, string newer) => ApiCheckIn(Shell.Process, older, newer);

    /// <summary>What <c>nanyx api-check</c> does, run in <paramref name="shell"/>: the changes on stdout, as
    /// <c>nanyx bump</c> lists them, then a line that says the bump is big enough; or what is wrong on stderr, exiting
    /// with 1.</summary>
    internal static int ApiCheckIn(Shell shell, string older, string newer)
    {
        var apis = new List<PackageApi>();
        foreach (var path in new[] { older, newer })
        {
            var full = shell.FullPath(path);
            if (!File.Exists(full))
            {
                shell.Error.WriteLine($"error: There is no file '{path}'");
                return UsageError;
            }

            var api = PackageApiModule.ofArchive(Path.GetFileName(full), File.ReadAllBytes(full));
            if (api.IsError)
            {
                shell.Error.WriteLine($"error: {api.ErrorValue}");
                return 1;
            }

            apis.Add(api.ResultValue);
        }

        var checkedVersions = PackageApiModule.checkVersions(apis[0], apis[1]);
        if (checkedVersions.IsError)
        {
            foreach (var error in checkedVersions.ErrorValue)
            {
                shell.Error.WriteLine($"error: {error}");
            }

            return 1;
        }

        PrintChanges(shell, checkedVersions.ResultValue);
        shell.Out.WriteLine($"{apis[1].Package} {apis[1].Version} is a big enough bump from {apis[0].Version}");
        return 0;
    }

    /// <summary>Writes each of <paramref name="changes"/> on a line of its own, after the bump it needs.</summary>
    private static void PrintChanges(Shell shell, IEnumerable<ApiChange> changes)
    {
        foreach (var change in changes)
        {
            var bump = change.Bump.IsMajorBump ? "major" : change.Bump.IsMinorBump ? "minor" : "patch";
            shell.Out.WriteLine($"{bump}: {change.Description}");
        }
    }

    /// <summary>
    /// Add a dependency to the manifest of the package the current directory is in: one found in a directory with --path, or one from the package index, at the versions --version accepts, or else at its newest. A package the compiler ships, such as nanyx.cli, is named at the compiler's version. A package from the index is locked in nanyx.lock and downloaded, as nanyx pull does; if no versions satisfy every dependency with it, the manifest is left as it was.
    /// </summary>
    /// <param name="package">The package to depend on, as in acme.util.</param>
    /// <param name="version">The versions of the package to accept: 1.2 or 1.2.0 accepts that version and those compatible with it. The default is the newest version in the index.</param>
    /// <param name="directory">The directory the package is in, which holds its manifest.</param>
    [Consolo("install")]
    public static int Install(string package, [Consolo("version")] string? version = null, [Consolo("path")] string? directory = null) =>
        InstallIn(Shell.Process, package, version, directory);

    /// <summary>What <c>nanyx install</c> does, run in <paramref name="shell"/>.</summary>
    internal static int InstallIn(Shell shell, string package, string? version, string? directory)
    {
        if (!TryFindManifest(shell, null, out var manifestPath, out var failed))
        {
            return failed;
        }

        var store = PackageStoreModule.downloading(shell.Packages);
        var installed = PackageDependencies.install(
            Toolchain.version,
            store,
            manifestPath,
            package,
            version is null ? FSharpOption<string>.None : FSharpOption<string>.Some(version),
            directory is null ? FSharpOption<string>.None : FSharpOption<string>.Some(shell.FullPath(directory)));
        if (installed.IsError)
        {
            PrintLockFailure(shell, installed.ErrorValue);
            return 1;
        }

        File.WriteAllText(manifestPath, installed.ResultValue.Manifest);
        shell.Out.WriteLine($"Added {package} to {shell.DisplayPath(manifestPath)}");
        if (installed.ResultValue.Lockfile?.Value is not { } locked)
        {
            return 0;
        }

        File.WriteAllText(LockfileModule.pathIn(Path.GetDirectoryName(manifestPath)!), LockfileModule.write(locked.Lockfile));
        foreach (var line in locked.Lines)
        {
            shell.Out.WriteLine(line);
        }

        var pulled = PackageDependencies.pull(store, manifestPath);
        if (pulled.IsError)
        {
            PrintWarnings(shell, locked.Warnings);
            PrintLockFailure(shell, pulled.ErrorValue);
            return 1;
        }

        PrintWarnings(shell, ListModule.Append(locked.Warnings, pulled.ResultValue.Warnings));
        return 0;
    }
}
