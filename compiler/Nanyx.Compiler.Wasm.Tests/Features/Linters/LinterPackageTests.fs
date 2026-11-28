/// A linter package, named by a project's manifest in its `linters`, built into a lint build and run by `nanyx check` and
/// the language server (see the Linters page in Work in progress, #3172).
module TranspilerWasmTests.Features.Linters.LinterPackage

open System.IO
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

/// The test linter's manifest, with `rules` as its declared rules.
let private linterManifest (rules: string) =
    $"""(
  package = (
    name = "acme.lint"
    version = "1.0.0"
    sources = ["src/**/*.nyx"]
    linter = (root = "acme.lint", name = "acme", rules = [{rules}])
  )
  dependencies = [(package = "nanyx.analysis", version = "{Toolchain.version}")]
)
"""

let private shortNames = """(id = "shortNames", title = "Short name", severity = #warning, description = "A top-level def whose name is shorter than three letters.")"""

/// The test linter: it reports each top-level def whose name is shorter than three letters.
let private linterSource = """export module acme.lint

import (
  nanyx.analysis/analysis as (Analysis, Report)
  nanyx.analysis/syntax as (Module, TopLevelItem)
)

def check: TopLevelItem -> list(Report) = { item ->
  match item.kind
    | #def(d) ->
      if string.length(d.name) < 3 then
        [(rule = "shortNames", span = item.span, message = "'{d.name}' is short", subject = #some(d.name), related = [], fixes = [])]
      else
        []
    | _ -> []
}

export def lint: [Analysis] Module -> list(Report) = { m -> list.flatMap(m, check) }
"""

/// A project beside the test linter, in `directory`, whose manifest names it by its path, with `settings` as the entry's
/// settings, and whose one source file, `src/main.nyx`, is `source`.
let private project (directory: TempDirectory) (settings: string) (source: string) =
    directory.Write "lint/acme.lint.nyxp" (linterManifest shortNames)
    directory.Write "lint/src/acme.lint.nyx" linterSource
    directory.Write
        "app/app.nyxp"
        $"""(
  package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"])
  linters = [(package = "acme.lint", path = "../lint"{settings})]
  {consoleHostEntry}
)
"""
    directory.Write "app/src/main.nyx" source
    Path.Combine(directory.Path, "app")

let private source = """module main

def ab = 1

@allow(acme/shortNames)
def cd = 2

def long = 3

@entry
def main = { println("{ab}{cd}{long}") }
"""

[<Fact>]
let ``nanyx check reports what a linter package named by its path finds, at the severity its settings give, and @allow suppresses it`` () =
    use directory = new TempDirectory()
    let app = project directory ", settings = (rules = (shortNames = #error))" source
    let checkedOnce = nanyxIn app [ "check" ]
    Assert.Equal(1, checkedOnce.ExitCode)
    let output = normalizeOutput checkedOnce.StandardOutput
    Assert.Contains("error acme/shortNames", output)
    Assert.Contains("'ab' is short", output)
    Assert.DoesNotContain("'cd' is short", output)
    Assert.DoesNotContain("'long'", output)

[<Fact>]
let ``A linter package's rule runs at its declared severity when the settings don't give one`` () =
    use directory = new TempDirectory()
    let app = project directory "" source
    let checkedOnce = nanyxIn app [ "check" ]
    Assert.Equal(0, checkedOnce.ExitCode)
    Assert.Contains("warning acme/shortNames", normalizeOutput checkedOnce.StandardOutput)

[<Fact>]
let ``A misspelt rule in a linter package's settings is an error that names its rules`` () =
    use directory = new TempDirectory()
    let app = project directory ", settings = (rules = (shortName = #error))" source
    let checkedOnce = nanyxIn app [ "check" ]
    Assert.Equal(1, checkedOnce.ExitCode)
    Assert.Contains(
        "The rules of 'acme.lint' has no field 'shortName': its fields are 'shortNames', 'linterFailed'",
        normalizeOutput checkedOnce.StandardError
    )

[<Fact>]
let ``A package named in linters that doesn't declare itself a linter is an error at its entry`` () =
    use directory = new TempDirectory()
    let app = project directory "" source
    directory.Write
        "lint/acme.lint.nyxp"
        $"""(package = (name = "acme.lint", version = "1.0.0"), dependencies = [(package = "nanyx.analysis", version = "{Toolchain.version}")])"""
    let checkedOnce = nanyxIn app [ "check" ]
    Assert.Equal(1, checkedOnce.ExitCode)
    Assert.Contains("'acme.lint' is not a linter", normalizeOutput checkedOnce.StandardError)

[<Fact>]
let ``A linter written against another nanyx.analysis than the compiler's is an error at its entry`` () =
    use directory = new TempDirectory()
    let app = project directory "" source
    directory.Write
        "lint/acme.lint.nyxp"
        ((linterManifest shortNames).Replace($"""(package = "nanyx.analysis", version = "{Toolchain.version}")""", """(package = "nanyx.analysis", version = "0.1.0")"""))
    let checkedOnce = nanyxIn app [ "check" ]
    Assert.Equal(1, checkedOnce.ExitCode)
    Assert.Contains("is written against nanyx.analysis 0.1.0", normalizeOutput checkedOnce.StandardError)

[<Fact>]
let ``A linter package whose lint build doesn't compile fails each module, and the build goes on`` () =
    use directory = new TempDirectory()
    let app = project directory "" source
    directory.Write "lint/src/acme.lint.nyx" (linterSource.Replace("list.flatMap(m, check)", "list.flatMap(m, nope)"))
    let checkedOnce = nanyxIn app [ "check" ]
    Assert.Equal(0, checkedOnce.ExitCode)
    let output = normalizeOutput checkedOnce.StandardOutput
    Assert.Contains("warning acme/linterFailed", output)
    Assert.Contains("its lint build has errors", output)

/// The language server's compile of the project's `src/main.nyx`, with the CLI's way of running linter packages, as
/// `nanyx lsp` starts it.
let private serverCompile (app: string) (text: string) =
    let settings =
        Nanyx.LanguageServer.ServerSettings(TypeNotation.Unicode, Packages = (fun () -> testStore ()), LintRunner = LintRunners.Wasm)
    let documents = Nanyx.LanguageServer.DocumentStore(Microsoft.Extensions.Logging.Abstractions.NullLogger<Nanyx.LanguageServer.DocumentStore>.Instance, settings)
    let path = Path.Combine(app, "src", "main.nyx")
    documents.Compile(OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri.FromFileSystemPath path, text.ReplaceLineEndings "\n")

[<Fact>]
let ``The language server reports what a linter package finds, and a keystroke doesn't build its lint build again`` () =
    use directory = new TempDirectory()
    let app = project directory ", settings = (rules = (shortNames = #info))" source
    let codes (document: Nanyx.LanguageServer.OpenDocument) =
        document.Compiled.Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, diagnostic.Severity, diagnostic.Message)
    let first = serverCompile app source
    Assert.Equal<(string * Severity * string) list>([ "acme/shortNames", Severity.Info, "'ab' is short" ], codes first)
    let built = LintBuilds.count ()
    let typed = serverCompile app (source.Replace("def long = 3", "def long = 3\n\ndef xy = 4"))
    Assert.Equal(built, LintBuilds.count ())
    Assert.Equal<(string * Severity * string) list>(
        [ "acme/shortNames", Severity.Info, "'ab' is short"; "acme/shortNames", Severity.Info, "'xy' is short" ],
        codes typed |> List.sort
    )

[<Fact>]
let ``The language server reports a misspelt rule in a linter package's settings in the manifest`` () =
    use directory = new TempDirectory()
    let app = project directory ", settings = (rules = (shortName = #error))" source
    let settings = Nanyx.LanguageServer.ServerSettings(TypeNotation.Unicode, Packages = (fun () -> testStore ()))
    let documents = Nanyx.LanguageServer.DocumentStore(Microsoft.Extensions.Logging.Abstractions.NullLogger<Nanyx.LanguageServer.DocumentStore>.Instance, settings)
    let manifest = Path.Combine(app, "app.nyxp")
    let compiled = documents.Compile(OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri.FromFileSystemPath manifest, File.ReadAllText manifest)
    let error = Assert.Single(compiled.Compiled.Diagnostics)
    Assert.Equal(DiagnosticCodes.UnknownManifestField.Code, error.Code)
    Assert.Equal("The rules of 'acme.lint' has no field 'shortName': its fields are 'shortNames', 'linterFailed'", error.Message)
