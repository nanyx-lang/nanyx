/// A linter package's declaration in its manifest, and a project's `linters` entries that name linter packages (see
/// `LinterPackages`, and the Wasm tests' `LinterPackageTests.fs` for running them).
module Nanyx.Compiler.Tests.Features.Packages.LinterManifests

open System.IO
open Xunit
open NyxCompiler

let private check (text: string) = Manifest.check "demo.nyxp" (text.ReplaceLineEndings "\n")

/// Each problem with the manifest `text`, as its code and message.
let private errorsOf (text: string) =
    match check text with
    | Result.Error errors -> errors |> List.map (fun error -> error.Code, error.Message)
    | Result.Ok _ -> failwith "Expected errors"

let private okOf (text: string) =
    match check text with
    | Result.Ok manifest -> manifest
    | Result.Error errors -> failwith (errors |> List.map Manifest.describe |> String.concat "\n")

[<Fact>]
let ``A linter package declares its root, its name and its rules`` () =
    let manifest =
        okOf
            """
(
  package = (
    name = "acme.lint"
    version = "1.0.0"
    linter = (
      root = "acme.lint"
      name = "acme"
      rules = [
        (id = "noTodo", title = "TODO comment", severity = #info, description = "A TODO comment.")
        (id = "deadCode", title = "Dead code", severity = #warning, unnecessary = true)
      ]
    )
  )
)
"""
    let declared = manifest.DeclaresLinter.Value
    Assert.Equal("acme.lint", declared.Root)
    Assert.Equal("acme", declared.Name)
    Assert.Equal<(string * Severity * bool) list>(
        [ "noTodo", Severity.Info, false; "deadCode", Severity.Warning, true ],
        declared.Rules |> List.map (fun rule -> rule.Id, rule.DefaultSeverity, rule.Unnecessary)
    )

[<Fact>]
let ``A linter's declaration with a rule missing what it needs, of the wrong kind, or repeated is an error`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.MissingManifestField.Code, "The package's linter has no name, which it needs"
          DiagnosticCodes.MissingManifestField.Code, "A rule has no title, which it needs"
          DiagnosticCodes.InvalidManifestValue.Code, "A rule's severity must be #error, #warning, #info, #hint, not #off"
          DiagnosticCodes.InvalidManifestValue.Code, "Every linter has the rule 'linterFailed', which the compiler reports when the linter fails: give the rule another id"
          DiagnosticCodes.InvalidManifestValue.Code, "The rule 'noTodo' is declared twice, but each of a linter's rules has an id of its own: remove one" ],
        errorsOf
            """
(
  package = (
    name = "acme.lint"
    version = "1.0.0"
    linter = (
      root = "acme.lint"
      rules = [
        (id = "noTodo", severity = #off)
        (id = "linterFailed", title = "Failed", severity = #warning)
        (id = "noTodo", title = "TODO comment", severity = #info)
      ]
    )
  )
)
"""
    )

[<Fact>]
let ``A linters entry that isn't a linter the compiler has built in names a linter package by its path or version`` () =
    let manifest =
        okOf
            """
(
  package = (name = "demo", version = "0.1.0")
  linters = [
    (package = "acme.lint", path = "../lint", settings = (rules = (noTodo = #error)))
    (package = "other.lint", version = "1.2")
  ]
)
"""
    Assert.Empty(manifest.Linters.Linters)
    Assert.Equal<(string * string option * string option) list>(
        [ "acme.lint", Some(Path.GetFullPath "../lint"), None; "other.lint", None, Some "1.2" ],
        manifest.LinterPackages |> List.map (fun entry -> entry.Package, entry.Path, entry.Version |> Option.map string)
    )

[<Fact>]
let ``A linter package's entry with no path or version, or both, is an error`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.MissingManifestField.Code,
          "The linter 'acme.lint' has no `path` or `version`: give the directory its package is in, `path = \"../lint\"`, or the version to download, `version = \"1.0\"`"
          DiagnosticCodes.InvalidManifestValue.Code, "The linter 'other.lint' is found by its path, so it has no version: give one or the other" ],
        errorsOf
            """
(
  package = (name = "demo", version = "0.1.0")
  linters = [(package = "acme.lint"), (package = "other.lint", path = "../other", version = "1.0.0")]
)
"""
    )

[<Fact>]
let ``The official linter is built in, so it has no path`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.InvalidManifestValue.Code, "The compiler has 'nanyx.lint' built in, so it has no `path`: name it at the compiler's version" ],
        errorsOf
            $"""
(
  package = (name = "demo", version = "0.1.0")
  linters = [(package = "nanyx.lint", version = "{Toolchain.version}", path = "../lint")]
)
"""
    )
