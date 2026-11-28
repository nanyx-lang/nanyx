module Nanyx.Compiler.Tests.Features.Packages.Manifests

open Xunit
open NyxCompiler

/// The manifest `text`, read.
let private check (text: string) = Manifest.check "demo.nyxp" (text.ReplaceLineEndings "\n")

let private okOf (text: string) =
    match check text with
    | Result.Ok manifest -> manifest
    | Result.Error errors -> failwith (errors |> List.map Manifest.describe |> String.concat "\n")

/// Each problem with the manifest `text`, as its code and message.
let private errorsOf (text: string) =
    match check text with
    | Result.Error errors -> errors |> List.map (fun error -> error.Code, error.Message)
    | Result.Ok _ -> failwith "Expected errors"

/// The one problem with the manifest `text`.
let private errorOf (text: string) =
    match check text with
    | Result.Error [ error ] -> error
    | Result.Error errors -> failwith (errors |> List.map Manifest.describe |> String.concat "\n")
    | Result.Ok _ -> failwith "Expected an error"

[<Fact>]
let ``A manifest's package is read, with the default sources, no linters and no host: a library`` () =
    let manifest = okOf """(package = (name = "demo", version = "0.1.0"))"""
    Assert.Equal("demo", manifest.Name)
    Assert.Equal("0.1.0", manifest.Version)
    Assert.Equal<string list>([ "**/*.nyx" ], manifest.Sources)
    Assert.Empty(manifest.Linters.Linters)
    Assert.Equal(None, manifest.Host)

[<Fact>]
let ``A manifest is read with comments, its sources, public modules, formatter, empty dependencies and host`` () =
    let manifest =
        okOf
            $"""
-- The demo package.
(
  package = (
    name = "acme.demo"
    version = "1.2.0-beta.1"
    sources = ["src/**/*.nyx"]
  )
  dependencies = []
  formatter = (lineWidth = 100)
  host = (package = "nanyx.cli.host", version = "{Toolchain.version}")
)
"""
    Assert.Equal("acme.demo", manifest.Name)
    Assert.Equal<string list>([ "src/**/*.nyx" ], manifest.Sources)
    Assert.Equal(Some Hosts.consolePackage, manifest.Host |> Option.map _.Package)

[<Fact>]
let ``Linters turn on the linters they name, with the severities their settings set`` () =
    let manifest =
        okOf
            $"""
(
  package = (name = "demo", version = "0.1.0")
  linters = [
    (package = "nanyx.lint", version = "{Toolchain.version}", settings = (rules = (unusedBindings = #error, linterFailed = #off)))
  ]
)
"""
    Assert.Equal<string list>([ "default" ], manifest.Linters.Linters |> List.map _.Name)
    Assert.Equal(Some(Some Severity.Error), manifest.Linters.Severities |> Map.tryFind "default/unusedBindings")
    Assert.Equal(Some None, manifest.Linters.Severities |> Map.tryFind "default/linterFailed")

[<Fact>]
let ``A package's repository is the GitHub repository its versions are released on`` () =
    let manifest = okOf """(package = (name = "acme.web", version = "1.0.0", repository = "acme/web"))"""
    Assert.Equal(Some { Owner = "acme"; Name = "web" }, manifest.Repository)
    Assert.Equal(None, (okOf """(package = (name = "demo", version = "0.1.0"))""").Repository)
    let error = errorOf """(package = (name = "acme.web", version = "1.0.0", repository = "https://github.com/acme/web"))"""
    Assert.Equal(DiagnosticCodes.InvalidManifestValue.Code, error.Code)
    Assert.Contains("must be a GitHub repository, `owner/name`, as `acme/web`", error.Message)

[<Fact>]
let ``A manifest that is not Nanyx is a syntax error`` () =
    let error = errorOf "package: demo"
    Assert.Equal(DiagnosticCodes.SyntaxError.Code, error.Code)

[<Theory>]
[<InlineData("[package]\nname = \"demo\"\nversion = \"0.1.0\"\n", "[package]")>]
[<InlineData("# The demo package.\n\nname = \"demo\"\n", "name = \"demo\"")>]
let ``A manifest in the old TOML form is reported as TOML at its first line`` (text: string, first: string) =
    let error = errorOf text
    Assert.Equal(DiagnosticCodes.TomlManifest.Code, error.Code)
    Assert.Equal("The manifest is a Nanyx record, `( package = (...) ... )`, not TOML", error.Message)
    Assert.Equal(first, text.Substring(error.Span.Value.Start, error.Span.Value.End - error.Span.Value.Start))

[<Fact>]
let ``A TOML manifest with Windows line endings is reported at its first line's own place`` () =
    match Manifest.check "demo.nyxp" "# demo\r\n[package]\r\nname = \"demo\"\r\n" with
    | Result.Error [ error ] ->
        Assert.Equal(DiagnosticCodes.TomlManifest.Code, error.Code)
        Assert.Equal((8, 17, 1), (error.Span.Value.Start, error.Span.Value.End, error.Span.Value.StartLine))
    | other -> failwith $"Expected one error, got {other}"

[<Fact>]
let ``Code in a manifest is an error at each place it is written`` () =
    let errors =
        errorsOf
            """
(
  package = (name = "demo", version = "0.1.0")
  linters = [lint]
  formatter = (lineWidth = 50 + 50, indentSize = width())
  host = { 1 }
)
"""
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.CodeInManifest.Code, "The name 'lint' is code, but this file holds only data"
          DiagnosticCodes.CodeInManifest.Code, "The operator `+` is code, but this file holds only data"
          DiagnosticCodes.CodeInManifest.Code, "A call of 'width' is code, but this file holds only data"
          DiagnosticCodes.CodeInManifest.Code, "A lambda is code, but this file holds only data" ],
        errors
    )

[<Fact>]
let ``An interpolated string in a manifest is code`` () =
    let error = errorOf "(package = (name = \"demo\", version = \"{major}.0.0\"))"
    Assert.Equal(DiagnosticCodes.CodeInManifest.Code, error.Code)
    Assert.Equal("An interpolated string is code, but this file holds only data", error.Message)

[<Fact>]
let ``A definition, an import or a directive in a manifest is an error`` () =
    let errors = errorsOf "::linter nanyx.lint\nimport util\n\ndef x = 1\n"
    Assert.Equal(3, errors.Length)
    Assert.All(errors, fun (code, _) -> Assert.Equal(DiagnosticCodes.CodeInManifest.Code, code))

[<Fact>]
let ``An empty manifest is an error`` () =
    let error = errorOf "-- nothing yet\n"
    Assert.Equal(DiagnosticCodes.CodeInManifest.Code, error.Code)
    Assert.Equal("this file is empty, but it should hold one value written as data", error.Message)

[<Fact>]
let ``An unknown field is an error at its name`` () =
    let text = "(\n  package = (name = \"demo\", version = \"0.1.0\", colour = \"red\")\n  extras = []\n)"
    let errors =
        match check text with
        | Result.Error errors -> errors
        | Result.Ok _ -> failwith "Expected errors"
    Assert.Equal<string list>([ DiagnosticCodes.UnknownManifestField.Code; DiagnosticCodes.UnknownManifestField.Code ], errors |> List.map _.Code)
    Assert.Equal<string list>(
        [ "The manifest has no field 'extras': its fields are 'package', 'dependencies', 'linters', 'host', 'formatter'"
          "Package has no field 'colour': its fields are 'name', 'version', 'sources', 'tests', 'repository', 'nanyx', 'host', 'hostLibrary', 'linter'" ],
        errors |> List.map _.Message
    )
    let extras = errors.[0].Span.Value
    Assert.Equal((2, 2), (extras.StartLine, extras.StartColumn))
    Assert.Equal("extras", text.Substring(extras.Start, extras.End - extras.Start))
    Assert.EndsWith("demo.nyxp:3:3: error NYX0021: The manifest has no field 'extras': its fields are 'package', 'dependencies', 'linters', 'host', 'formatter'", Manifest.describe errors.[0])

[<Fact>]
let ``A missing package, name or version is an error`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.MissingManifestField.Code, "The manifest has no `package`, which needs at least a name and a version" ],
        errorsOf "(linters = [])"
    )
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.MissingManifestField.Code, "Package has no name, which it needs"
          DiagnosticCodes.MissingManifestField.Code, "Package has no version, which it needs" ],
        errorsOf "(package = (sources = []))"
    )

[<Fact>]
let ``A linter or host without a package or version is an error`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.MissingManifestField.Code, "A linter has no version, which it needs"
          DiagnosticCodes.MissingManifestField.Code, "The host has no package, which it needs" ],
        errorsOf
            $"""(package = (name = "demo", version = "0.1.0"), linters = [(package = "nanyx.lint")], host = (version = "{Toolchain.version}"))"""
    )

[<Fact>]
let ``A value of the wrong kind is an error saying what it must be`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.InvalidManifestValue.Code, "Package must be a record, `(name = value, ...)`, not a list" ],
        errorsOf "(package = [])"
    )
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.InvalidManifestValue.Code, """Package's name "Demo" must be lower case, with words separated by dots, as `acme.web`"""
          DiagnosticCodes.InvalidManifestValue.Code, "Package's version must be a string, not 1"
          DiagnosticCodes.InvalidManifestValue.Code, "Package's sources must be a list of strings, not the string \"src\"" ],
        errorsOf """(package = (name = "Demo", version = 1, sources = "src"))"""
    )
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.InvalidManifestValue.Code, "The manifest must be a record, `(name = value, ...)`, not a list" ],
        errorsOf "[]"
    )

[<Fact>]
let ``An unknown rule or severity of the official linter is an error`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.UnknownManifestField.Code,
          "The rules of 'nanyx.lint' has no field 'noSuchRule': its fields are 'unusedBindings', 'nonTailRecursion', 'unreachableArms', 'unusedImports', 'noTagInCommon', 'unusedPrivateDefinitions', 'unreachableCode', 'useInBraces', 'strayDocComments', 'unannotatedExports', 'resultOnlyTypeVariable', 'unusedResults', 'uselessExpressions', 'unawaitedTask', 'shadowedPreludeNames', 'semicolons', 'unconstructibleType', 'unusedContexts', 'repeatedPulls', 'redundantTypeName', 'undefinedPayloadArm', 'unusedAllows', 'linterFailed'"
          DiagnosticCodes.InvalidManifestValue.Code, "The rule unusedBindings must be #error, #warning, #info, #hint, #off, not the string \"error\"" ],
        errorsOf
            $"""
(
  package = (name = "demo", version = "0.1.0")
  linters = [
    (package = "nanyx.lint", version = "{Toolchain.version}", settings = (rules = (noSuchRule = #error, unusedBindings = "error")))
  ]
)
"""
    )

// Which settings a host takes is checked once the host is read (see `HostSettings`, and the HTTP host's tests).
[<Fact>]
let ``A host's settings must be a record`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.InvalidManifestValue.Code, "The host's `settings` must be a record, `(port = 3000)`, not 3000" ],
        errorsOf
            $"""
(
  package = (name = "demo", version = "0.1.0")
  dependencies = [(package = "acme.util", path = "../util")]
  host = (package = "nanyx.http.host", version = "{Toolchain.version}", settings = 3000)
)
"""
    )

[<Fact>]
let ``A linter must be named at the version the compiler ships, exactly, since the compiler runs it`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.VersionNotShipped.Code,
          $"The compiler ships 'nanyx.lint' {Toolchain.version}, not 0.1.0: name a package the compiler ships at its version exactly" ],
        errorsOf
            """
(
  package = (name = "demo", version = "0.1.0")
  linters = [(package = "nanyx.lint", version = "0.1.0")]
)
"""
    )

[<Fact>]
let ``A host or a dependency the compiler ships is named at a version, as any package from the index is`` () =
    let manifest =
        okOf
            """
(
  package = (name = "demo", version = "0.1.0")
  dependencies = [(package = "nanyx.cli", version = "0.42.0")]
  host = (package = "nanyx.cli.host", version = "99.0.0")
)
"""
    Assert.Equal<(string * string) list>(
        [ "nanyx.cli", "0.42.0" ],
        manifest.Dependencies |> List.map (fun dependency -> dependency.Name, match dependency.Source with FromIndex requirement -> string requirement | other -> string other))
    Assert.Equal(Some("nanyx.cli.host", Some "99.0.0"), manifest.Host |> Option.map (fun host -> host.Package, host.Version |> Option.map string))
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.InvalidManifestValue.Code, "The dependency 'acme.util' is found by its path, so it has no version: give one or the other" ],
        errorsOf
            """
(
  package = (name = "demo", version = "0.1.0")
  dependencies = [(package = "acme.util", path = "../util", version = "1.0.0")]
)
"""
    )

[<Fact>]
let ``A dependency listed twice is an error at the repeat`` () =
    let text =
        """
(
  package = (name = "demo", version = "0.1.0")
  dependencies = [(package = "acme.web", path = "../web"), (package = "acme.web", path = "../web")]
)
"""
    let errors =
        match check text with
        | Result.Error errors -> errors
        | Result.Ok _ -> failwith "Expected errors"
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.InvalidManifestValue.Code, "The dependency 'acme.web' is listed twice, but a package lists each package it depends on once: remove one" ],
        errors |> List.map (fun error -> error.Code, error.Message)
    )
    Assert.Equal<(int * int) list>([ 3, 70 ], errors |> List.map (fun error -> error.Span.Value.StartLine, error.Span.Value.StartColumn))

[<Fact>]
let ``The compiler's version is its release, with no commit`` () =
    Assert.Matches(@"^\d+\.\d+\.\d+$", Toolchain.version)

[<Fact>]
let ``An entry is written with its strings escaped`` () =
    let reference = { Repository = { Owner = "acme"; Name = "web" }; File = None; Commit = "v\"1\\2" }
    Assert.Equal("""(package = "acme.web", github = "acme/web", rev = "v\"1\\2")""", Manifest.entryText "" "acme.web" (OnGitHub reference))
[<Fact>]
let ``A package's nanyx is the oldest compiler it builds with, which the JSON of the manifest gives`` () =
    let manifest = okOf """(package = (name = "acme.demo", version = "0.1.0", sources = ["src/**/*.nyx"], nanyx = "0.32"))"""
    Assert.Equal(Some "0.32", manifest.Nanyx |> Option.map string)
    Assert.Equal(Some "0.32.0", (okOf """(package = (name = "demo", version = "0.1.0", nanyx = "0.32.0"))""").Nanyx |> Option.map string)
    Assert.Equal(None, (okOf """(package = (name = "demo", version = "0.1.0"))""").Nanyx)
    Assert.Contains("\"nanyx\": \"0.32\"", Manifest.toJson manifest.Value)

[<Fact>]
let ``A package whose nanyx is newer than the compiler is one error at the field, naming both versions`` () =
    let source = """(package = (name = "demo", version = "0.1.0", nanyx = "99.0"))"""
    let error = errorOf source
    Assert.Equal(DiagnosticCodes.NeedsOtherCompiler.Code, error.Code)
    Assert.Equal($"The package builds with nanyx 99.0 or later, and this is nanyx {Toolchain.version}: install a newer compiler to build it", error.Message)
    Assert.Equal("\"99.0\"", source.Substring(error.Span.Value.Start, error.Span.Value.End - error.Span.Value.Start))

[<Fact>]
let ``A package's nanyx can name one compiler only, which another compiler refuses at the field`` () =
    let manifest = okOf $"""(package = (name = "acme.engine", version = "0.1.0", nanyx = "={Toolchain.version}"))"""
    Assert.Equal(Some(true, SemanticVersion.tryParse Toolchain.version), manifest.Nanyx |> Option.map (fun nanyx -> nanyx.Exact, Some nanyx.Oldest))
    Assert.Contains($"\"nanyx\": \"={Toolchain.version}\"", Manifest.toJson manifest.Value)
    let source = """(package = (name = "demo", version = "0.1.0", nanyx = "=0.1.0"))"""
    let error = errorOf source
    Assert.Equal(DiagnosticCodes.NeedsOtherCompiler.Code, error.Code)
    Assert.Equal(
        $"The package builds with nanyx 0.1.0 only, and this is nanyx {Toolchain.version}: build it with that compiler, or use a version of the package made for this one",
        error.Message
    )
    Assert.Equal("\"=0.1.0\"", source.Substring(error.Span.Value.Start, error.Span.Value.End - error.Span.Value.Start))

[<Fact>]
let ``A package's nanyx must be a version, or its first numbers, or one compiler's version`` () =
    let error = errorOf """(package = (name = "demo", version = "0.1.0", nanyx = "latest"))"""
    Assert.Equal(DiagnosticCodes.InvalidManifestValue.Code, error.Code)
    Assert.Equal(
        "The package's `nanyx` \"latest\" must be a version, `0.43.0`, or its first numbers, `0.43`, for that compiler or a later one; or `=` and a whole version, `=0.43.0`, for that compiler only",
        error.Message
    )
    // One compiler is named by its whole version.
    Assert.Equal(DiagnosticCodes.InvalidManifestValue.Code, (errorOf """(package = (name = "demo", version = "0.1.0", nanyx = "=0.43"))""").Code)
    Assert.Equal(DiagnosticCodes.InvalidManifestValue.Code, (errorOf """(package = (name = "demo", version = "0.1.0", nanyx = 32))""").Code)
