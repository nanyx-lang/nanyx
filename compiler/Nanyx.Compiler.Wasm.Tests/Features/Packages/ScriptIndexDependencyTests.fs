/// A script's dependencies on packages from the index: `::dependency acme.core 1.0.0` pins the exact version the script
/// uses, since a script has no lockfile. `nanyx run` and `nanyx pull` download it, and the packages it depends on, into
/// the cache, checked against the index's SHA-256, and a build reads them from the cache. A package from the index named
/// with no version is an error, with a fix that writes the newest.
module TranspilerWasmTests.Features.Packages.ScriptIndexDependency

open System.IO
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

/// A store whose cache and index are directories in `directory`.
let private storeIn (directory: TempDirectory) =
    { Cache = Path.Combine(directory.Path, "cache"); Index = Path.Combine(directory.Path, "index"); GitHub = GitHub.unsupported }

/// The library acme.core at `version`, whose `answer` is `answer`, in a directory of its own, published to the index of
/// `store`, with `fields` in its index entry.
let private publishCore (directory: TempDirectory) (store: PackageStore) (version: string) (answer: int) (fields: string) =
    directory.Write $"core-{version}/core.nyxp" $"(package = (name = \"acme.core\", version = \"{version}\"))\n"
    directory.Write $"core-{version}/core.nyx" $"export module core\n\nexport def answer = {answer}\n"
    publishToIndex store (Path.Combine(directory.Path, $"core-{version}")) [] fields |> ignore

/// The script `script.nyx` in its own directory under `directory`: `directives`, then a line that prints the answer of
/// acme.core. Gives its path.
let private scriptWith (directory: TempDirectory) (directives: string) =
    directory.Write "scripts/script.nyx" $"{directives}\n\nimport acme.core/core as core\n\nprintln(core.answer)\n"
    Path.Combine(directory.Path, "scripts", "script.nyx")

let private normalised (text: string) = text.ReplaceLineEndings "\n"

[<Fact>]
let ``Run - a script runs with the exact version of a package from the index its directive pins, though a newer one is there`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    publishCore directory store "1.0.0" 1 ""
    publishCore directory store "1.1.0" 2 ""
    let script = scriptWith directory "::dependency acme.core 1.0.0"
    let outcome = nanyxInStore store (Path.GetDirectoryName script) [ "run"; "script.nyx" ]
    Assert.Equal("1\n", normalised outcome.StandardOutput)
    Assert.Equal(0, outcome.ExitCode)
    Assert.True(Directory.Exists(PackageStore.directoryOf store "acme.core" (SemanticVersion.tryParse "1.0.0").Value))
    Assert.False(Directory.Exists(PackageStore.directoryOf store "acme.core" (SemanticVersion.tryParse "1.1.0").Value))

[<Fact>]
let ``Run - the packages a pinned package depends on are chosen as a package's are, the newest each accepts`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    publishCore directory store "1.0.0" 1 ""
    publishCore directory store "1.2.0" 3 ""
    publishCore directory store "2.0.0" 4 ""
    directory.Write "web/web.nyxp" "(package = (name = \"acme.web\", version = \"0.1.0\"), dependencies = [(package = \"acme.core\", version = \"1.0\")])\n"
    directory.Write "web/web.nyx" "export module web\n\nimport acme.core/core as core\n\nexport def page = core.answer * 10\n"
    publishToIndex store (Path.Combine(directory.Path, "web")) [ "acme.core", "1.0" ] "" |> ignore
    directory.Write "scripts/script.nyx" "::dependency acme.web 0.1.0\n\nimport acme.web/web as web\n\nprintln(web.page)\n"
    let outcome = nanyxInStore store (Path.Combine(directory.Path, "scripts")) [ "pull"; "script.nyx" ]
    Assert.Equal("acme.core 1.2.0\nacme.web 0.1.0\n", normalised outcome.StandardOutput)
    Assert.Equal(0, outcome.ExitCode)
    let ran = nanyxInStore store (Path.Combine(directory.Path, "scripts")) [ "run"; "script.nyx" ]
    Assert.Equal("30\n", normalised ran.StandardOutput)

[<Fact>]
let ``Run - a yanked version a script names still resolves`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    publishCore directory store "1.0.0" 7 """, "yanked": true"""
    let script = scriptWith directory "::dependency acme.core 1.0.0"
    let outcome = nanyxInStore store (Path.GetDirectoryName script) [ "run"; "script.nyx" ]
    Assert.Equal("7\n", normalised outcome.StandardOutput)
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``Run - a version the index doesn't have, or that can't be downloaded, is an error`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    publishCore directory store "1.0.0" 1 ""
    publishCore directory store "1.1.0" 2 """, "available": false"""
    for version in [ "2.0.0"; "1.1.0" ] do
        let script = scriptWith directory $"::dependency acme.core {version}"
        let outcome = nanyxInStore store (Path.GetDirectoryName script) [ "run"; "script.nyx" ]
        Assert.Contains($"script.nyx depends on acme.core {version}", outcome.StandardError)
        Assert.Equal("", outcome.StandardOutput)
        Assert.Equal(1, outcome.ExitCode)

[<Fact>]
let ``Run - a download that isn't what the index recorded isn't used`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    publishCore directory store "1.0.0" 1 ""
    let artifact = Path.Combine(store.Index, "artifacts", "acme.core-1.0.0.nyxpkg")
    File.AppendAllText(artifact, "changed")
    let script = scriptWith directory "::dependency acme.core 1.0.0"
    let outcome = nanyxInStore store (Path.GetDirectoryName script) [ "run"; "script.nyx" ]
    Assert.Contains("The download of 'acme.core' 1.0.0 has the SHA-256", outcome.StandardError)
    Assert.Equal(1, outcome.ExitCode)

[<Fact>]
let ``A build reads a pinned package from the cache alone, and says how to download one that isn't there`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    publishCore directory store "1.0.0" 1 ""
    let script = scriptWith directory "::dependency acme.core 1.0.0"
    let directives = Script.readFrom store script (File.ReadAllText script)
    let diagnostic = Assert.Single directives.Diagnostics
    Assert.Equal(DiagnosticCodes.PackageNotDownloaded.Code, diagnostic.Code)
    Assert.Contains("'acme.core' 1.0.0 isn't in the package cache", diagnostic.Message)
    Assert.Contains("`nanyx pull script.nyx`", diagnostic.Message)
    Assert.Equal(0, (nanyxInStore store (Path.GetDirectoryName script) [ "pull"; "script.nyx" ]).ExitCode)
    Assert.Empty((Script.readFrom store script (File.ReadAllText script)).Diagnostics)

[<Fact>]
let ``A script names the exact version it uses, not the versions it accepts`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    let script = scriptWith directory "::dependency acme.core 1.0"
    let diagnostic = Assert.Single (Script.readFrom store script (File.ReadAllText script)).Diagnostics
    Assert.Equal(DiagnosticCodes.InvalidDirective.Code, diagnostic.Code)
    Assert.Contains("A script names the exact version of 'acme.core' it uses, as in `1.2.0`, not the versions it accepts, '1.0'", diagnostic.Message)

[<Fact>]
let ``A package from the index named with no version is an error, whose fix writes the newest the index has`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    publishCore directory store "1.0.0" 1 ""
    publishCore directory store "1.2.0" 2 ""
    publishCore directory store "1.3.0" 3 """, "yanked": true"""
    let script = scriptWith directory "::dependency acme.core"
    let source = File.ReadAllText script
    let diagnostic = Assert.Single (Script.readFrom store script source).Diagnostics
    Assert.Equal(DiagnosticCodes.InvalidDirective.Code, diagnostic.Code)
    Assert.Contains("`::dependency acme.core` needs the version of 'acme.core' to use, as in `::dependency acme.core 1.2.0`", diagnostic.Message)
    Assert.Contains("the newest in the index is 1.2.0", diagnostic.Message)
    let fix = Assert.Single diagnostic.Fixes
    Assert.Equal("Use acme.core 1.2.0", fix.Title)
    let edit = Assert.Single fix.Edits
    let atEnd = source.IndexOf "acme.core" + "acme.core".Length
    Assert.Equal((atEnd, atEnd, " 1.2.0"), (edit.Span.Start, edit.Span.End, edit.NewText))
