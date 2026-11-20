module Nanyx.Compiler.Tests.Features.Packages.GitHubReferences

open System
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A package or a script that depends on a file, or a whole package, in a GitHub repository, pinned to a commit (#1472).
// The repositories are directories on disk here, read by `GitHub.inDirectory`: `github/acme/web/<commit>` is the tree of
// acme/web at that commit.

let private commit = "1f8ae8b3c9d04e5f6a7b8c9d0e1f2a3b4c5d6e7f"
let private other = "0a1b2c3d4e5f60718293a4b5c6d7e8f901234567"

/// The repositories: a file of snippets, and a package, each at `commit`. Their tag `v1.0` points at `commit`.
let private repositories =
    [ $"github/acme/snippets/{commit}/retry.nyx", withFinalNewline """export def twice = { n: int -> n * 2 }"""
      $"github/acme/snippets/{commit}/named.nyx", withFinalNewline """module named

export def three = 3"""
      $"github/acme/snippets/{commit}/importing.nyx",
      withFinalNewline """import helpers

export def four = 4"""
      $"github/acme/web/{commit}/web.nyxp", withFinalNewline """(package = (name = "acme.web", version = "1.2.0"))"""
      $"github/acme/web/{commit}/http.nyx",
      withFinalNewline """export module http

export def status = 200""" ]

let private fetcherFor (package: TempPackage) =
    GitHub.inDirectory (package.Path "github") (Map.ofList [ "acme/snippets@v1.0", commit; "acme/web@v1.0", commit ])

/// A store whose GitHub is `fetcher`, with an empty cache in `package`.
let private storeWith (package: TempPackage) (fetcher: GitHubFetcher) =
    { Cache = package.Path "cache"; Index = package.Path "index"; GitHub = fetcher }

let private storeFor (package: TempPackage) = storeWith package (fetcherFor package)

/// The app's manifest, depending on `dependencies`, each an entry as the manifest writes it, run by the console host.
let private app (dependencies: string list) =
    let entries = String.concat ", " dependencies
    "app/app.nyxp",
    withFinalNewline $"""(
  package = (name = "app", version = "0.1.0")
  dependencies = [{entries}]
  {consoleHostEntry}
)"""

let private mainImporting (import: string) (body: string) =
    "app/main.nyx",
    withFinalNewline $"""module main

import {import}

@entry
def run = {{ () -> {body} }}"""

let private retry = $"""(module = "retry", github = "acme/snippets/retry.nyx", rev = "{commit}")"""
let private web = $"""(package = "acme.web", github = "acme/web", rev = "{commit}")"""

let private manifestWith (entry: string) =
    $"""(package = (name = "app", version = "0.1.0"), dependencies = [{entry}])"""

/// The one error checking the manifest whose `dependencies` hold `entry` reports.
let private manifestError (entry: string) =
    match errorsOf (Manifest.check "app.nyxp" (manifestWith entry)) with
    | [ error ] -> error
    | errors -> failwith $"Expected one error, got {errors}"

[<Fact>]
let ``A file and a package on GitHub are read with the commit they are pinned to`` () =
    let manifest = okOf (Manifest.check "app.nyxp" (manifestWith (retry + ", " + web)))
    let file = manifest.Files |> List.exactlyOne
    Assert.Equal("retry", file.Module)
    Assert.Equal($"acme/snippets/retry.nyx", string file.Reference)
    Assert.Equal(commit, file.Reference.Commit)
    let dependency = manifest.Dependencies |> List.exactlyOne
    Assert.Equal("acme.web", dependency.Name)
    Assert.Equal(OnGitHub { Repository = { Owner = "acme"; Name = "web" }; File = None; Commit = commit }, dependency.Source)

[<Fact>]
let ``A tag is pinned by the commit beside it`` () =
    let manifest = okOf (Manifest.check "app.nyxp" (manifestWith $"""(package = "acme.web", github = "acme/web", rev = "v1.0", commit = "{commit}")"""))
    Assert.Equal(Some commit, (match manifest.Dependencies.Head.Source with OnGitHub reference -> Some reference.Commit | _ -> None))

[<Fact>]
let ``A reference with no rev is not pinned`` () =
    let error = manifestError """(module = "retry", github = "acme/snippets/retry.nyx")"""
    Assert.Equal(DiagnosticCodes.GitHubReferenceNotPinned.Code, error.Code)
    Assert.Contains("'acme/snippets/retry.nyx' has no `rev`", error.Message)

[<Fact>]
let ``A tag or a branch alone is not pinned, since it can move`` () =
    let entry = """(package = "acme.web", github = "acme/web", rev = "main")"""
    let error = manifestError entry
    Assert.Equal(DiagnosticCodes.GitHubReferenceNotPinned.Code, error.Code)
    Assert.Equal("\"main\"", textAt (manifestWith entry) (spanOf error))
    Assert.Contains("not a full commit SHA, so what it names can move", error.Message)

[<Fact>]
let ``A commit must be a full SHA`` () =
    let error = manifestError """(package = "acme.web", github = "acme/web", rev = "v1.0", commit = "1f8ae8b")"""
    Assert.Equal(DiagnosticCodes.InvalidManifestValue.Code, error.Code)
    Assert.Contains("must be a full commit SHA", error.Message)

[<Fact>]
let ``A rev and a commit that are different commits are refused`` () =
    let error = manifestError $"""(package = "acme.web", github = "acme/web", rev = "{other}", commit = "{commit}")"""
    Assert.Equal(DiagnosticCodes.InvalidManifestValue.Code, error.Code)
    Assert.Contains("different commits", error.Message)

[<Fact>]
let ``A module name must be a valid one`` () =
    let error = manifestError $"""(module = "Retry-Helpers", github = "acme/snippets/retry.nyx", rev = "{commit}")"""
    Assert.Equal(DiagnosticCodes.InvalidManifestValue.Code, error.Code)
    Assert.Contains("must be a module name", error.Message)

[<Fact>]
let ``An entry gives a package or a module, not both`` () =
    let error = manifestError $"""(package = "acme.web", module = "web", github = "acme/web", rev = "{commit}")"""
    Assert.Equal(DiagnosticCodes.InvalidManifestValue.Code, error.Code)
    Assert.Contains("a package or a module, not both", error.Message)

[<Fact>]
let ``A package on GitHub is a repository, and a module a file of one`` () =
    let package = manifestError $"""(package = "acme.web", github = "acme/web/http.nyx", rev = "{commit}")"""
    Assert.Contains("a whole repository", package.Message)
    let file = manifestError $"""(module = "retry", github = "acme/snippets", rev = "{commit}")"""
    Assert.Contains("one file of a repository", file.Message)

[<Fact>]
let ``A module without github is refused`` () =
    let error = manifestError """(module = "retry", path = "../retry.nyx")"""
    Assert.Equal(DiagnosticCodes.MissingManifestField.Code, error.Code)
    Assert.Contains("has no `github`", error.Message)

[<Fact>]
let ``A package with a GitHub reference can't be packed or published`` () =
    let manifest = okOf (Manifest.check "app.nyxp" (manifestWith (retry + ", " + web)))
    let errors = Manifest.publishErrors manifest
    Assert.Equal<string list>(
        [ DiagnosticCodes.GitHubReferenceNotPublishable.Code; DiagnosticCodes.GitHubReferenceNotPublishable.Code ],
        errors |> List.map _.Code
    )
    Assert.Equal("\"acme/web\"", textAt (manifestWith (retry + ", " + web)) (spanOf errors.Head))
    Assert.Empty(Manifest.publishErrors (okOf (Manifest.check "app.nyxp" (manifestWith ""))))

[<Fact>]
let ``A file from GitHub is a module of the package`` () =
    use package = new TempPackage(repositories @ [ app [ retry ]; mainImporting "retry" "ignore(twice(2))" ])
    let found = okOf (Package.ofManifestFrom (storeFor package) (package.Path "app/app.nyxp"))
    Assert.Equal<string list>([ "retry" ], found.ReferencedFiles |> List.map fst)
    let build = Package.build found
    Assert.Empty(build.Diagnostics)
    Assert.True((Package.entryResult build).Value.Typed.IsSome)

[<Fact>]
let ``A package from GitHub is imported as a path dependency is`` () =
    use package = new TempPackage(repositories @ [ app [ web ]; mainImporting "acme.web/http" "ignore(status)" ])
    let found = okOf (Package.ofManifestFrom (storeFor package) (package.Path "app/app.nyxp"))
    Assert.Equal<string list>([ "acme.web" ], found.Dependencies |> List.map _.Manifest.Name)
    Assert.Empty((Package.build found).Diagnostics)

[<Fact>]
let ``A file from GitHub must declare no module name`` () =
    let entry = $"""(module = "named", github = "acme/snippets/named.nyx", rev = "{commit}")"""
    use package = new TempPackage(repositories @ [ app [ entry ]; mainImporting "named" "ignore(three)" ])
    let errors = errorsOf (Package.ofManifestFrom (storeFor package) (package.Path "app/app.nyxp"))
    let error = errors |> List.exactlyOne
    Assert.Contains(DiagnosticCodes.InvalidReferencedFile.Code, error)
    Assert.Contains("declares the module name 'named', but a file from GitHub declares none", error)

[<Fact>]
let ``A file from GitHub imports only the standard library`` () =
    let entry = $"""(module = "importing", github = "acme/snippets/importing.nyx", rev = "{commit}")"""
    let helpers = "app/helpers.nyx", withFinalNewline "module helpers\n\nexport def one = 1"
    use package = new TempPackage(repositories @ [ app [ entry ]; helpers; mainImporting "importing" "ignore(four)" ])
    let found = okOf (Package.ofManifestFrom (storeFor package) (package.Path "app/app.nyxp"))
    let errors = (Package.build found).Diagnostics |> List.filter Diagnostics.isError
    Assert.Contains(errors, fun error -> error.Code = DiagnosticCodes.InvalidReferencedFile.Code && error.Message.Contains "can import only the standard library, not 'helpers'")

[<Fact>]
let ``A build reads GitHub only from the cache, and says to pull what isn't there`` () =
    use package = new TempPackage([ app [ retry; web ]; mainImporting "retry" "ignore(twice(2))" ])
    let store = storeWith package (GitHub.cachedWith (fun _ _ -> failwith "a build doesn't download") (package.Path "cache"))
    let errors = errorsOf (Package.ofManifestFrom store (package.Path "app/app.nyxp"))
    Assert.Equal(2, errors.Length)
    for error in errors do
        Assert.Contains(DiagnosticCodes.PackageNotDownloaded.Code, error)
        Assert.Contains("is on GitHub, but it isn't in the package cache: run `nanyx pull` to fetch it", error)

/// GitHub's addresses as `GitHub.downloadingWith` asks for them, answered from `repositories`: a file from
/// raw.githubusercontent.com, a repository's zip from codeload.github.com, in one folder as GitHub makes it, and the
/// commit a tag points at from api.github.com. Each address asked for is added to `asked`.
let private fakeGitHub (package: TempPackage) (asked: Collections.Generic.List<string>) : GitHub.Get =
    fun _ address ->
        asked.Add address
        let raw = $"https://raw.githubusercontent.com/acme/snippets/{commit}/"
        if address.StartsWith raw then
            let file = package.Path $"github/acme/snippets/{commit}/{address.Substring raw.Length}"
            Result.Ok(if System.IO.File.Exists file then Some(System.IO.File.ReadAllBytes file) else None)
        elif address = $"https://codeload.github.com/acme/web/zip/{commit}" then
            use stream = new System.IO.MemoryStream()
            (
                use zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, true)
                for file in System.IO.Directory.GetFiles(package.Path $"github/acme/web/{commit}") do
                    use writer = new System.IO.StreamWriter(zip.CreateEntry($"web-{commit}/{System.IO.Path.GetFileName file}").Open())
                    writer.Write(System.IO.File.ReadAllText file)
            )
            Result.Ok(Some(stream.ToArray()))
        elif address = "https://api.github.com/repos/acme/web/commits/v1.0" then
            Result.Ok(Some(Text.Encoding.UTF8.GetBytes commit))
        else
            Result.Ok None

[<Fact>]
let ``Pull fetches a file and a package from GitHub into the cache, and the build uses them`` () =
    use package = new TempPackage(repositories @ [ app [ retry; web ]; mainImporting "acme.web/http" "ignore(status)" ])
    let asked = Collections.Generic.List<string>()
    let cache = package.Path "cache"
    let pulled = okOf (pullFrom (storeWith package (GitHub.downloadingWith (fakeGitHub package asked) cache)) (package.Path "app/app.nyxp"))
    Assert.Contains("acme.web 1.2.0 (", pulled |> String.concat "\n")
    Assert.True(System.IO.File.Exists(GitHub.fileIn cache { Owner = "acme"; Name = "snippets" } commit "retry.nyx"))
    Assert.True(System.IO.File.Exists(System.IO.Path.Combine(GitHub.treeIn cache { Owner = "acme"; Name = "web" } commit, "web.nyxp")))
    let fetched = asked.Count
    let store = storeWith package (GitHub.cachedWith (fakeGitHub package asked) cache)
    Assert.Empty((Package.build (okOf (Package.ofManifestFrom store (package.Path "app/app.nyxp")))).Diagnostics)
    // Pulling again finds both in the cache.
    okOf (pullFrom (storeWith package (GitHub.downloadingWith (fakeGitHub package asked) cache)) (package.Path "app/app.nyxp")) |> ignore
    Assert.Equal(fetched, asked.Count)

[<Fact>]
let ``A tag is resolved through GitHub's API, once`` () =
    use package = new TempPackage([])
    let asked = Collections.Generic.List<string>()
    let fetcher = GitHub.cachedWith (fakeGitHub package asked) (package.Path "cache")
    let web = { Owner = "acme"; Name = "web" }
    Assert.Equal<Result<string, FetchFailure>>(Result.Ok commit, fetcher.Resolve web "v1.0")
    Assert.Equal<Result<string, FetchFailure>>(Result.Ok commit, fetcher.Resolve web "v1.0")
    Assert.Equal<string list>([ "https://api.github.com/repos/acme/web/commits/v1.0" ], List.ofSeq asked)
    Assert.Equal<Result<string, FetchFailure>>(Result.Error(FetchFailed "'acme/web' has no tag or branch 'v9'"), fetcher.Resolve web "v9")

[<Fact>]
let ``The editor offers to pin a tag to the commit it points at`` () =
    let text = manifestWith """(package = "acme.web", github = "acme/web", rev = "v1.0")"""
    use package = new TempPackage(repositories @ [ "app/app.nyxp", text; mainImporting "acme.web/http" "ignore(status)" ])
    let diagnostic =
        (Package.compileManifestFrom (storeFor package) (package.Path "app/app.nyxp") text).Diagnostics
        |> List.find (fun diagnostic -> diagnostic.Code = DiagnosticCodes.GitHubReferenceNotPinned.Code)
    let fix = diagnostic.Fixes |> List.exactlyOne
    Assert.Equal($"Pin to {commit}", fix.Title)
    let pinned = applyFix text fix
    Assert.Contains($"""rev = "v1.0", commit = "{commit}")""", pinned)
    Assert.True((Manifest.check "app.nyxp" pinned).IsOk)

[<Fact>]
let ``The editor offers to pin a reference with no rev to its default branch's commit`` () =
    let text = manifestWith """(package = "acme.web", github = "acme/web")"""
    use package = new TempPackage([])
    let store = storeWith package (GitHub.inDirectory "unused" (Map.ofList [ "acme/web@HEAD", commit ]))
    let diagnostic = (Package.compileManifestFrom store (package.Path "app.nyxp") text).Diagnostics |> List.exactlyOne
    Assert.Contains($"""github = "acme/web", rev = "{commit}")""", applyFix text diagnostic.Fixes.Head)

[<Fact>]
let ``No pin is offered when the commit can't be found`` () =
    let text = manifestWith """(package = "acme.web", github = "acme/web", rev = "v1.0")"""
    use package = new TempPackage([])
    let store = storeWith package GitHub.unsupported
    let diagnostic = (Package.compileManifestFrom store (package.Path "app.nyxp") text).Diagnostics |> List.exactlyOne
    Assert.Equal(DiagnosticCodes.GitHubReferenceNotPinned.Code, diagnostic.Code)
    Assert.Empty(diagnostic.Fixes)

[<Fact>]
let ``A script's dependency directive names a file on GitHub`` () =
    let source = withFinalNewline $"""::dependency retry github:acme/snippets/retry.nyx@{commit}

import retry

println(twice(2))"""
    use package = new TempPackage(repositories @ [ "script.nyx", source ])
    let directives = Script.readFrom (storeFor package) (package.Path "script.nyx") source
    Assert.Empty(directives.Diagnostics)
    let result =
        Script.compileSourceReading CompileListener.none System.IO.File.ReadAllText directives Linters.none (package.Path "script.nyx") source
    Assert.Empty(result.Diagnostics)

[<Fact>]
let ``A script's dependency directive names a package on GitHub`` () =
    let source = withFinalNewline $"""::dependency acme.web github:acme/web@{commit}

import acme.web/http

println(status)"""
    use package = new TempPackage(repositories @ [ "script.nyx", source ])
    let directives = Script.readFrom (storeFor package) (package.Path "script.nyx") source
    Assert.Empty(directives.Diagnostics)

[<Fact>]
let ``A script's GitHub reference must be pinned to a commit`` () =
    let source = withFinalNewline """::dependency retry github:acme/snippets/retry.nyx@v1.0

println(twice(2))"""
    let directives = Script.read "script.nyx" source
    let error = directives.Diagnostics |> List.exactlyOne
    Assert.Equal(DiagnosticCodes.GitHubReferenceNotPinned.Code, error.Code)
