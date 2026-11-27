/// A script's dependencies on packages from the index: `::install acme.core 1.0.0` pins the exact version the script
/// uses, since a script has no lockfile. `nanyx run` and `nanyx pull` download it, and the packages it depends on, into
/// the cache, checked against the index's SHA-256, and a build reads them from the cache. A package from the index named
/// with no version is an error, with a fix that writes the newest.
module TranspilerWasmTests.Features.Packages.ScriptIndexDependency

open System.IO
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

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
    let script = scriptWith directory "::install acme.core 1.0.0"
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
    directory.Write "scripts/script.nyx" "::install acme.web 0.1.0\n\nimport acme.web/web as web\n\nprintln(web.page)\n"
    let outcome = nanyxInStore store (Path.Combine(directory.Path, "scripts")) [ "pull"; "script.nyx" ]
    Assert.Equal("acme.core 1.2.0\nacme.web 0.1.0\n", normalised outcome.StandardOutput)
    Assert.Equal(0, outcome.ExitCode)
    let ran = nanyxInStore store (Path.Combine(directory.Path, "scripts")) [ "run"; "script.nyx" ]
    Assert.Equal("30\n", normalised ran.StandardOutput)

[<Fact>]
let ``Run - an unlisted version a script names still resolves`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    publishCore directory store "1.0.0" 7 """, "unlisted": true"""
    let script = scriptWith directory "::install acme.core 1.0.0"
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
        let script = scriptWith directory $"::install acme.core {version}"
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
    let script = scriptWith directory "::install acme.core 1.0.0"
    let outcome = nanyxInStore store (Path.GetDirectoryName script) [ "run"; "script.nyx" ]
    Assert.Contains("The download of 'acme.core' 1.0.0 has the SHA-256", outcome.StandardError)
    Assert.Equal(1, outcome.ExitCode)

[<Fact>]
let ``A build reads a pinned package from the cache alone, and says how to download one that isn't there`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    publishCore directory store "1.0.0" 1 ""
    let script = scriptWith directory "::install acme.core 1.0.0"
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
    let script = scriptWith directory "::install acme.core 1.0"
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
    let script = scriptWith directory "::install acme.core"
    let source = File.ReadAllText script
    let diagnostic = Assert.Single (Script.readFrom store script source).Diagnostics
    Assert.Equal(DiagnosticCodes.InvalidDirective.Code, diagnostic.Code)
    Assert.Contains("`::install acme.core` needs the version of 'acme.core' to use, as in `::install acme.core 1.2.0`", diagnostic.Message)
    Assert.Contains("the newest in the index is 1.2.0", diagnostic.Message)
    let fix = Assert.Single diagnostic.Fixes
    Assert.Equal("Use acme.core 1.2.0", fix.Title)
    let edit = Assert.Single fix.Edits
    let atEnd = source.IndexOf "acme.core" + "acme.core".Length
    Assert.Equal((atEnd, atEnd, " 1.2.0"), (edit.Span.Start, edit.Span.End, edit.NewText))

[<Fact>]
let ``Read as the editor reads it, a package named with no version asks the index nothing`` () =
    // The editor compiles a script as it is typed, and each new spelling of a package's name waited for two requests to
    // the index (#2223).
    let mutable requests = 0
    use index = new TestServer(fun _ _ _ _ -> (System.Threading.Interlocked.Increment(&requests) |> ignore; 500, [], ""))
    use directory = new TempDirectory()
    let store = { storeIn directory with Index = index.Url }
    let script = scriptWith directory "::install acme.coreless"
    let diagnostic = Assert.Single (Script.readFromWithoutIndex store script (File.ReadAllText script)).Diagnostics
    Assert.Equal(DiagnosticCodes.InvalidDirective.Code, diagnostic.Code)
    Assert.Contains("`::install acme.coreless` needs the version of 'acme.coreless' to use", diagnostic.Message)
    Assert.DoesNotContain("newest", diagnostic.Message)
    Assert.Empty diagnostic.Fixes
    Assert.Equal(0, requests)

[<Fact>]
let ``The editor's fix for a package named with no version writes the newest the index has, when it is asked for`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    publishCore directory store "1.0.0" 1 ""
    publishCore directory store "1.2.0" 2 ""
    let script = scriptWith directory "::install acme.core"
    let source = File.ReadAllText script
    let diagnostic = Assert.Single (Script.readFromWithoutIndex store script source).Diagnostics
    let span, fix = Assert.Single(Script.newestVersionFixes store script source)
    Assert.Equal(diagnostic.Span, Some span)
    Assert.Equal("Use acme.core 1.2.0", fix.Title)
    let edit = Assert.Single fix.Edits
    let atEnd = source.IndexOf "acme.core" + "acme.core".Length
    Assert.Equal((atEnd, atEnd, " 1.2.0"), (edit.Span.Start, edit.Span.End, edit.NewText))

[<Fact>]
let ``Run - a script whose pinned packages are all in the cache runs without the index`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    publishCore directory store "1.0.0" 5 ""
    let script = scriptWith directory "::install acme.core 1.0.0"
    let first = nanyxInStore store (Path.GetDirectoryName script) [ "run"; "script.nyx" ]
    Assert.Equal("5\n", normalised first.StandardOutput)
    // An index that can't be read, as one with no network can't (#2221).
    let offline = { store with Index = Path.Combine(directory.Path, "unreachable") }
    let again = nanyxInStore offline (Path.GetDirectoryName script) [ "run"; "script.nyx" ]
    Assert.Equal("", again.StandardError)
    Assert.Equal("5\n", normalised again.StandardOutput)
    Assert.Equal(0, again.ExitCode)
    // A pin the cache doesn't hold still goes to the index, and says it can't be read.
    let other = scriptWith directory "::install acme.core 1.1.0"
    let missing = nanyxInStore offline (Path.GetDirectoryName other) [ "run"; "script.nyx" ]
    Assert.Contains("unreachable", missing.StandardError)
    Assert.Equal(1, missing.ExitCode)

/// acme.web 0.1.0, which depends on acme.core 1.0, published to the index of `store` beside acme.core 1.0.0.
let private publishWeb (directory: TempDirectory) (store: PackageStore) =
    publishCore directory store "1.0.0" 1 ""
    directory.Write "web/web.nyxp" "(package = (name = \"acme.web\", version = \"0.1.0\"), dependencies = [(package = \"acme.core\", version = \"1.0\")])\n"
    directory.Write "web/web.nyx" "export module web\n\nimport acme.core/core as core\n\nexport def page = core.answer * 10\n"
    publishToIndex store (Path.Combine(directory.Path, "web")) [ "acme.core", "1.0" ] "" |> ignore

/// How many times each file of the index of `store` was read, of `reads` (see `PackageIndex.startCounting`).
let private readsOfIndex (store: PackageStore) (reads: string seq) =
    reads
    |> Seq.filter (fun location -> location.StartsWith store.Index)
    |> Seq.countBy (fun location -> Path.GetRelativePath(store.Index, location).Replace('\\', '/'))
    |> Seq.sort
    |> List.ofSeq

[<Fact>]
let ``Pull - a first pull of a script reads the index's config.json once and each listing once`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    publishWeb directory store
    directory.Write "scripts/script.nyx" "::install acme.web 0.1.0\n\nimport acme.web/web as web\n\nprintln(web.page)\n"
    let reads = PackageIndex.startCounting ()
    let pulled = nanyxInStore store (Path.Combine(directory.Path, "scripts")) [ "pull"; "script.nyx" ]
    Assert.Equal(0, pulled.ExitCode)
    Assert.Equal<(string * int) list>(
        [ "config.json", 1; "index/acme/acme.core.json", 1; "index/acme/acme.web.json", 1 ],
        readsOfIndex store reads
    )

[<Fact>]
let ``Pull - a first pull of a package reads the index's config.json once and each listing once`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    publishWeb directory store
    directory.Write "app/app.nyxp" "(package = (name = \"acme.app\", version = \"0.1.0\"), dependencies = [(package = \"acme.web\", version = \"0.1\")])\n"
    directory.Write "app/app.nyx" "export module app\n\nimport acme.web/web as web\n\nexport def page = web.page\n"
    let reads = PackageIndex.startCounting ()
    let pulled = nanyxInStore store (Path.Combine(directory.Path, "app")) [ "pull" ]
    Assert.True((pulled.StandardError = ""), pulled.StandardError)
    Assert.Equal(0, pulled.ExitCode)
    Assert.Equal<(string * int) list>(
        [ "config.json", 1; "index/acme/acme.core.json", 1; "index/acme/acme.web.json", 1 ],
        readsOfIndex store reads
    )
