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
    publicModules = ["demo"]
  )
  dependencies = []
  formatter = (lineWidth = 100)
  host = (package = "nanyx.console", version = "{Toolchain.version}")
)
"""
    Assert.Equal("acme.demo", manifest.Name)
    Assert.Equal<string list>([ "src/**/*.nyx" ], manifest.Sources)
    Assert.Equal(Some Hosts.console, manifest.Host)

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
let ``A manifest that is not Nanyx is a syntax error`` () =
    let error = errorOf "[package]\nname = \"demo\""
    Assert.Equal(DiagnosticCodes.SyntaxError.Code, error.Code)

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
        [ DiagnosticCodes.CodeInManifest.Code, "the name 'lint' is code, but this file holds only data"
          DiagnosticCodes.CodeInManifest.Code, "the operator '+' is code, but this file holds only data"
          DiagnosticCodes.CodeInManifest.Code, "a call of 'width' is code, but this file holds only data"
          DiagnosticCodes.CodeInManifest.Code, "a lambda is code, but this file holds only data" ],
        errors
    )

[<Fact>]
let ``An interpolated string in a manifest is code`` () =
    let error = errorOf "(package = (name = \"demo\", version = \"{major}.0.0\"))"
    Assert.Equal(DiagnosticCodes.CodeInManifest.Code, error.Code)
    Assert.Equal("an interpolated string is code, but this file holds only data", error.Message)

[<Fact>]
let ``A definition, an import or a directive in a manifest is an error`` () =
    let errors = errorsOf "::linters.default\nimport util\n\ndef x = 1\n"
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
        [ "the manifest has no field 'extras'; its fields are 'package', 'dependencies', 'linters', 'host', 'formatter'"
          "package has no field 'colour'; its fields are 'name', 'version', 'sources', 'publicModules'" ],
        errors |> List.map _.Message
    )
    let extras = errors.[0].Span.Value
    Assert.Equal((2, 2), (extras.StartLine, extras.StartColumn))
    Assert.Equal("extras", text.Substring(extras.Start, extras.End - extras.Start))
    Assert.EndsWith("demo.nyxp:3:3: error NYX0021: the manifest has no field 'extras'; its fields are 'package', 'dependencies', 'linters', 'host', 'formatter'", Manifest.describe errors.[0])

[<Fact>]
let ``A missing package, name or version is an error`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.MissingManifestField.Code, "the manifest has no package, which needs at least a name and a version" ],
        errorsOf "(linters = [])"
    )
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.MissingManifestField.Code, "package has no name, which it needs"
          DiagnosticCodes.MissingManifestField.Code, "package has no version, which it needs" ],
        errorsOf "(package = (sources = []))"
    )

[<Fact>]
let ``A linter or host without a package or version is an error`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.MissingManifestField.Code, "a linter has no version, which it needs"
          DiagnosticCodes.MissingManifestField.Code, "the host has no package, which it needs" ],
        errorsOf
            $"""(package = (name = "demo", version = "0.1.0"), linters = [(package = "nanyx.lint")], host = (version = "{Toolchain.version}"))"""
    )

[<Fact>]
let ``A value of the wrong kind is an error saying what it must be`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.InvalidManifestValue.Code, "package must be a record, (name = value, ...), not a list" ],
        errorsOf "(package = [])"
    )
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.InvalidManifestValue.Code, """package's name "Demo" must be lower case, with words separated by dots: acme.web"""
          DiagnosticCodes.InvalidManifestValue.Code, "package's version must be a string, not 1"
          DiagnosticCodes.InvalidManifestValue.Code, "package's sources must be a list of strings, not the string \"src\"" ],
        errorsOf """(package = (name = "Demo", version = 1, sources = "src"))"""
    )
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.InvalidManifestValue.Code, "the manifest must be a record, (name = value, ...), not a list" ],
        errorsOf "[]"
    )

[<Fact>]
let ``An unknown linter, rule, severity or host is an error`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.InvalidManifestValue.Code, "there is no linter 'acme.lint'; the linters are 'nanyx.lint'"
          DiagnosticCodes.UnknownManifestField.Code,
          "the rules of nanyx.lint has no field 'noSuchRule'; its fields are 'unusedBindings', 'nonTailRecursion', 'unreachableArms', 'unusedImports', 'noTagInCommon', 'unusedPrivateDefinitions', 'unreachableCode', 'useInBraces', 'strayDocComments', 'plusBetweenContexts', 'unannotatedExports', 'unusedResults', 'shadowedPreludeNames', 'semicolons', 'linterFailed'"
          DiagnosticCodes.InvalidManifestValue.Code, "the rule unusedBindings must be #error, #warning, #info, #hint, #off, not the string \"error\""
          DiagnosticCodes.InvalidManifestValue.Code, "there is no host 'acme.host'; the hosts are 'nanyx.console'" ],
        errorsOf
            $"""
(
  package = (name = "demo", version = "0.1.0")
  linters = [
    (package = "acme.lint", version = "1.0.0")
    (package = "nanyx.lint", version = "{Toolchain.version}", settings = (rules = (noSuchRule = #error, unusedBindings = "error")))
  ]
  host = (package = "acme.host", version = "1.0.0")
)
"""
    )

[<Fact>]
let ``A host's settings are not supported yet`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.UnsupportedManifestField.Code, "nanyx.console takes no settings" ],
        errorsOf
            $"""
(
  package = (name = "demo", version = "0.1.0")
  dependencies = [(package = "acme.util", path = "../util")]
  host = (package = "nanyx.console", version = "{Toolchain.version}", settings = (colour = true))
)
"""
    )

[<Fact>]
let ``A linter or host must be named at the version the compiler ships, exactly`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.VersionNotShipped.Code,
          $"the compiler ships nanyx.lint {Toolchain.version}, not 0.1.0: a package the compiler ships is named at its version exactly"
          DiagnosticCodes.VersionNotShipped.Code,
          $"the compiler ships nanyx.console {Toolchain.version}, not 99.0.0: a package the compiler ships is named at its version exactly" ],
        errorsOf
            """
(
  package = (name = "demo", version = "0.1.0")
  linters = [(package = "nanyx.lint", version = "0.1.0")]
  host = (package = "nanyx.console", version = "99.0.0")
)
"""
    )

[<Fact>]
let ``A public module or a dependency listed twice is an error at the repeat`` () =
    let text =
        """
(
  package = (name = "demo", version = "0.1.0", publicModules = ["strings", "lists", "strings"])
  dependencies = [(package = "acme.web", path = "../web"), (package = "acme.web", path = "../web")]
)
"""
    let errors =
        match check text with
        | Result.Error errors -> errors
        | Result.Ok _ -> failwith "Expected errors"
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.InvalidManifestValue.Code, "the dependency acme.web is listed twice: a package lists each package it depends on once"
          DiagnosticCodes.InvalidManifestValue.Code, "the module 'strings' is listed twice in publicModules" ],
        errors |> List.map (fun error -> error.Code, error.Message)
    )
    Assert.Equal<(int * int) list>([ 3, 70; 2, 84 ], errors |> List.map (fun error -> error.Span.Value.StartLine, error.Span.Value.StartColumn))

[<Fact>]
let ``The compiler's version is its release, with no commit`` () =
    Assert.Matches(@"^\d+\.\d+\.\d+$", Toolchain.version)
