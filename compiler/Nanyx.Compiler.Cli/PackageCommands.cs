using Consolo;
using Microsoft.FSharp.Core;
using NyxCompiler;

/// <summary>
/// The commands that make a package and manage its dependencies: <c>nanyx migrate</c>, <c>nanyx pull</c>,
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
    /// Make a package's dependencies available to build: download into the package cache each package nanyx.lock locks that isn't there yet, check that each dependency is there, and list them. Choosing the version of a package that nanyx.lock doesn't lock isn't supported yet.
    /// </summary>
    /// <param name="path">A package's directory or .nyxp manifest; none means the package the current directory is in.</param>
    [Consolo("pull")]
    public static int Pull(params string[] path) => PullIn(Shell.Process, path);

    /// <summary>What <c>nanyx pull</c> does, run in <paramref name="shell"/>.</summary>
    internal static int PullIn(Shell shell, string[] path)
    {
        if (!TrySinglePath(shell, path, out var single))
        {
            return UsageError;
        }

        if (!TryFindManifest(shell, single, out var manifestPath, out var failed))
        {
            return failed;
        }

        var pulled = PackageDependencies.pull(shell.Packages, manifestPath);
        if (pulled.IsError)
        {
            foreach (var error in pulled.ErrorValue)
            {
                shell.Error.WriteLine($"error: {error}");
            }

            return 1;
        }

        foreach (var line in pulled.ResultValue)
        {
            shell.Out.WriteLine(line);
        }

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

        foreach (var change in result.Changes)
        {
            var bump = change.Bump.IsMajorBump ? "major" : change.Bump.IsMinorBump ? "minor" : "patch";
            shell.Out.WriteLine($"{bump}: {change.Description}");
        }

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
    /// Add a dependency to the manifest of the package the current directory is in: one found in a directory with --path, or named by its version with --version. A package the compiler ships, such as nanyx.console.core, needs neither. A package named by its version can't be downloaded yet.
    /// </summary>
    /// <param name="package">The package to depend on, as in acme.util.</param>
    /// <param name="version">The version of the package to download.</param>
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

        var installed = PackageDependencies.install(
            Toolchain.version,
            manifestPath,
            package,
            version is null ? FSharpOption<string>.None : FSharpOption<string>.Some(version),
            directory is null ? FSharpOption<string>.None : FSharpOption<string>.Some(shell.FullPath(directory)));
        if (installed.IsError)
        {
            shell.Error.WriteLine($"error: {installed.ErrorValue}");
            return 1;
        }

        File.WriteAllText(manifestPath, installed.ResultValue.Manifest);
        shell.Out.WriteLine($"Added {package} to {shell.DisplayPath(manifestPath)}");
        if (installed.ResultValue.ToDownload is { } download)
        {
            shell.Error.WriteLine($"error: {PackageDependencies.notDownloaded(download.Value.Item1, download.Value.Item2)}");
            return 1;
        }

        return 0;
    }
}
