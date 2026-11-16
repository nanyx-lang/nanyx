module Nanyx.Compiler.Tests.Features.Packages.GitHubReferences

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

let private errorsOf result =
    match result with
    | Result.Error errors -> errors
    | Result.Ok _ -> failwith "Expected errors"

let private okOf result =
    match result with
    | Result.Ok value -> value
    | Result.Error errors -> failwith (String.concat "\n" (errors |> Seq.map string))

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
    Assert.Equal(Some { Repository = { Owner = "acme"; Name = "web" }; File = None; Commit = commit }, dependency.GitHub)

[<Fact>]
let ``A tag is pinned by the commit beside it`` () =
    let manifest = okOf (Manifest.check "app.nyxp" (manifestWith $"""(package = "acme.web", github = "acme/web", rev = "v1.0", commit = "{commit}")"""))
    Assert.Equal(commit, manifest.Dependencies.Head.GitHub.Value.Commit)

[<Fact>]
let ``A reference with no rev is not pinned`` () =
    let error = manifestError """(module = "retry", github = "acme/snippets/retry.nyx")"""
    Assert.Equal(DiagnosticCodes.GitHubReferenceNotPinned.Code, error.Code)
    Assert.Contains("acme/snippets/retry.nyx has no rev", error.Message)

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
    Assert.Contains("has no github", error.Message)

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
    Assert.Contains("declares the module name 'named', but a file from GitHub must declare none", error)

[<Fact>]
let ``A file from GitHub imports only the standard library`` () =
    let entry = $"""(module = "importing", github = "acme/snippets/importing.nyx", rev = "{commit}")"""
    let helpers = "app/helpers.nyx", withFinalNewline "module helpers\n\nexport def one = 1"
    use package = new TempPackage(repositories @ [ app [ entry ]; helpers; mainImporting "importing" "ignore(four)" ])
    let found = okOf (Package.ofManifestFrom (storeFor package) (package.Path "app/app.nyxp"))
    let errors = (Package.build found).Diagnostics |> List.filter Diagnostics.isError
    Assert.Contains(errors, fun error -> error.Code = DiagnosticCodes.InvalidReferencedFile.Code && error.Message.Contains "can import only the standard library, not helpers")

[<Fact>]
let ``Downloading from GitHub isn't supported yet`` () =
    use package = new TempPackage([ app [ retry; web ]; mainImporting "retry" "ignore(twice(2))" ])
    let errors = errorsOf (Package.ofManifest (package.Path "app/app.nyxp"))
    Assert.Equal(2, errors.Length)
    for error in errors do
        Assert.Contains(DiagnosticCodes.UnsupportedManifestField.Code, error)
        Assert.Contains("is on GitHub, but downloading from GitHub isn't supported yet", error)

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
    let diagnostic = (Package.compileManifest "app.nyxp" text).Diagnostics |> List.exactlyOne
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
