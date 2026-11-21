using Consolo;
using Microsoft.FSharp.Core;
using NyxCompiler;

/// <summary>
/// The commands that publish a package: <c>nanyx publish</c>, which releases a version on the package's own GitHub
/// repository and registers a new package with the index, and <c>nanyx yank</c>. What they change on GitHub is
/// <see cref="GitHubApi"/>'s, and the files of the index's repository they write are <see cref="Publishing"/>'s.
/// These run the steps in order, say what each is doing, and stop at the first that fails.
/// </summary>
internal static partial class CliCommands
{
    /// <summary>
    /// Publish the version a package's manifest gives: check that it builds and is formatted, check that its version is a big enough bump from the newest published below it, pack it, create the release v&lt;version&gt; with the .nyxpkg on the GitHub repository its manifest's repository names, and, for its first version, open the pull request that registers it with the package index. Run it again to finish a publish that stopped after creating the release. Signs in to GitHub with GITHUB_TOKEN, or else the GitHub CLI's login (gh auth login).
    /// </summary>
    /// <param name="path">A package's directory or .nyxp manifest; none means the package the current directory is in.</param>
    [Consolo("publish")]
    public static int Publish(params string[] path) => PublishIn(Shell.Process, path);

    /// <summary>What <c>nanyx publish</c> does, run in <paramref name="shell"/>.</summary>
    internal static int PublishIn(Shell shell, string[] path)
    {
        if (!TrySinglePath(shell, path, out var single))
        {
            return UsageError;
        }

        if (!TryUpdateLockfile(shell, single, frozen: false, out var failed) || !TryFindPackage(shell, single, out var package, out failed))
        {
            return failed;
        }

        var manifest = package.Manifest;
        if (manifest.Repository?.Value is not { } repository)
        {
            shell.Error.WriteLine($"error: {manifest.Name} has no `repository`, the GitHub repository whose releases are its versions: add `repository = \"owner/name\"` to the `package` in {shell.DisplayPath(manifest.Path)}");
            return 1;
        }

        shell.Out.WriteLine($"Publishing {manifest.Name} {manifest.Version} to {repository}");
        shell.Out.WriteLine("Checking that it builds");
        var build = BuildPackage(shell, package, timings: false);
        if (HasErrors(build.Diagnostics))
        {
            PrintDiagnostics(shell, build.Diagnostics, shell.Error);
            shell.Error.WriteLine("error: It doesn't build, and a version can't change once it is published: fix the errors above");
            return 1;
        }

        shell.Out.WriteLine("Checking that it is formatted");
        var unformatted = package.Sources.Where(file => !IsFormatted(file)).Select(shell.DisplayPath).ToList();
        if (unformatted.Count > 0)
        {
            shell.Error.WriteLine($"error: {string.Join(", ", unformatted)} {(unformatted.Count == 1 ? "isn't" : "aren't")} formatted: run `nanyx format --write` on the package");
            return 1;
        }

        shell.Out.WriteLine("Comparing its API with the newest version published");
        var compared = PackageApiModule.publishCheck(shell.Packages, package);
        if (compared.IsError)
        {
            foreach (var error in compared.ErrorValue)
            {
                shell.Error.WriteLine($"error: {error}");
            }

            return 1;
        }

        if (compared.ResultValue?.Value is { } baseline)
        {
            PrintChanges(shell, baseline.Item2);
            shell.Out.WriteLine($"{manifest.Version} is a big enough bump from {baseline.Item1}");
        }
        else
        {
            shell.Out.WriteLine("It is the first version, so there is nothing to compare");
        }

        shell.Out.WriteLine("Packing it");
        var packed = PackageApiModule.pack(package);
        if (packed.IsError)
        {
            PrintDiagnostics(shell, packed.ErrorValue, shell.Error);
            return 1;
        }

        if (!TryGitHub(shell, out var github))
        {
            return 1;
        }

        // Everything that can stop the publish part way is checked before the release is made, so that a failure leaves
        // nothing behind; a publish that stops after it all the same can be run again to finish (see createRelease).
        shell.Out.WriteLine($"Checking that you can release on {repository}");
        var canRelease = GitHubApi.canPush(github, repository);
        if (canRelease.IsError || !canRelease.ResultValue)
        {
            shell.Error.WriteLine(canRelease.IsError
                ? $"error: {canRelease.ErrorValue}"
                : $"error: The account you are signed in to GitHub as can't push to {repository}, so it can't create the release: sign in as one that can, or use a token with write access to it");
            return 1;
        }

        var registrationPath = Publishing.registrationPath(manifest.Name);
        var registered = GitHubApi.indexFile(github, registrationPath);
        if (registered.IsError)
        {
            shell.Error.WriteLine($"error: {registered.ErrorValue}");
            return 1;
        }

        var needsRegistration = registered.ResultValue is null;
        GitHubApi.IndexRoute? route = null;
        if (needsRegistration)
        {
            shell.Out.WriteLine($"Checking that you can open the pull request that registers it on {GitHubApi.indexRepository}");
            if (!TryIndexRoute(shell, github, out route))
            {
                return 1;
            }
        }

        var tag = $"v{manifest.Version}";
        shell.Out.WriteLine($"Creating the release {tag} of {repository}");
        var release = GitHubApi.createRelease(github, repository, tag, $"{manifest.Name} {manifest.Version}", PackageArchive.fileName(manifest), packed.ResultValue);
        if (release.IsError)
        {
            shell.Error.WriteLine($"error: {release.ErrorValue}");
            return 1;
        }

        shell.Out.WriteLine(release.ResultValue switch
        {
            GitHubApi.Release.Released released => $"Released: {released.Item}",
            GitHubApi.Release.ReleasedAlready earlier => $"Released already, with this {PackageArchive.extension}, by a publish that didn't finish: {earlier.Item}",
            _ => throw new InvalidOperationException("A release is made or found"),
        });

        if (needsRegistration)
        {
            shell.Out.WriteLine($"Opening the pull request that registers {manifest.Name} with the package index");
            var pull = GitHubApi.openIndexPullRequest(
                github,
                route!,
                registrationPath,
                Publishing.registration(manifest.Name, repository),
                FSharpOption<string>.None,
                $"register-{manifest.Name}",
                $"Register {manifest.Name}",
                $"Registers {manifest.Name}, whose versions are the releases of https://github.com/{repository}. Opened by `nanyx publish`.");
            if (pull.IsError)
            {
                shell.Error.WriteLine($"error: {pull.ErrorValue}");
                shell.Error.WriteLine($"error: The release is made: run `nanyx publish` again to open the pull request");
                return 1;
            }

            shell.Out.WriteLine($"Opened {pull.ResultValue}: once a maintainer merges it, the index records {manifest.Name} within the hour");
            return 0;
        }

        shell.Out.WriteLine($"Published {manifest.Name} {manifest.Version}: the index records it within the hour");
        return 0;
    }

    /// <summary>
    /// Yank a published version of a package, so that no new resolution chooses it; a lockfile that locks it still builds. Opens the pull request on the package index that marks it yanked. Signs in to GitHub as nanyx publish does.
    /// </summary>
    /// <param name="package">The package, as in acme.util.</param>
    /// <param name="version">The version to yank, as in 1.2.0.</param>
    [Consolo("yank")]
    public static int Yank(string package, string version) => YankIn(Shell.Process, package, version);

    /// <summary>What <c>nanyx yank</c> does, run in <paramref name="shell"/>.</summary>
    internal static int YankIn(Shell shell, string package, string version)
    {
        if (SemanticVersionModule.tryParse(version)?.Value is not { } parsed)
        {
            shell.Error.WriteLine($"error: '{version}' is not a version, which must be a semantic version, as `1.2.0`");
            return UsageError;
        }

        if (!TryGitHub(shell, out var github))
        {
            return 1;
        }

        var indexPath = Publishing.indexPath(package);
        var index = GitHubApi.indexFile(github, indexPath);
        if (index.IsError || index.ResultValue is null)
        {
            shell.Error.WriteLine(index.IsError ? $"error: {index.ErrorValue}" : $"error: The package index has no package '{package}'");
            return 1;
        }

        var yanked = Publishing.yank(package, parsed, index.ResultValue.Value.Item1);
        if (yanked.IsError)
        {
            shell.Error.WriteLine($"error: {yanked.ErrorValue}");
            return 1;
        }

        if (!TryIndexRoute(shell, github, out var route))
        {
            return 1;
        }

        shell.Out.WriteLine($"Opening the pull request that yanks {package} {parsed}");
        var pull = GitHubApi.openIndexPullRequest(
            github,
            route,
            indexPath,
            yanked.ResultValue,
            FSharpOption<string>.Some(index.ResultValue.Value.Item2),
            $"yank-{package}-{parsed}",
            $"Yank {package} {parsed}",
            $"Yanks {package} {parsed}, so that no new resolution chooses it. Opened by `nanyx yank`.");
        if (pull.IsError)
        {
            shell.Error.WriteLine($"error: {pull.ErrorValue}");
            return 1;
        }

        shell.Out.WriteLine($"Opened {pull.ResultValue}: once it is merged, new resolutions pass over {package} {parsed}");
        return 0;
    }

    /// <summary>The shell's GitHub, or GitHub itself signed in with the user's token; otherwise reports that there is no
    /// token to sign in with.</summary>
    private static bool TryGitHub(Shell shell, out FSharpFunc<GitHubRequest, FSharpResult<GitHubResponse, string>> github)
    {
        github = shell.GitHub!;
        if (shell.GitHub is not null)
        {
            return true;
        }

        if (GitHubApi.token()?.Value is not { } token)
        {
            shell.Error.WriteLine("error: You aren't signed in to GitHub: run `gh auth login`, or set GITHUB_TOKEN to a token that can create releases on the package's repository and fork or push to nanyx-lang/packages");
            return false;
        }

        github = FuncConvert.FromFunc<GitHubRequest, FSharpResult<GitHubResponse, string>>(request => GitHubApi.client(token, request));
        return true;
    }

    /// <summary>Where the account <paramref name="github"/> is signed in as opens its pull requests to the package index
    /// from: a branch of the index, if it can push to it, or else its fork of it, which this makes if need be; otherwise
    /// reports why it can't open them.</summary>
    private static bool TryIndexRoute(
        Shell shell,
        FSharpFunc<GitHubRequest, FSharpResult<GitHubResponse, string>> github,
        out GitHubApi.IndexRoute route)
    {
        var sleep = FuncConvert.FromAction<int>(seconds => shell.Stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds)));
        var found = GitHubApi.indexRoute(github, sleep);
        if (found.IsError)
        {
            shell.Error.WriteLine($"error: {found.ErrorValue}");
            route = null!;
            return false;
        }

        route = found.ResultValue;
        return true;
    }

    /// <summary>Whether the file at <paramref name="file"/> is laid out as the formatter lays it out, under its package's
    /// settings.</summary>
    private static bool IsFormatted(string file)
    {
        var text = File.ReadAllText(file);
        return Formatted(file, text) == text;
    }
}
