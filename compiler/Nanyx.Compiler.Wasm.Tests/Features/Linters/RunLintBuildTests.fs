/// A linter written in Nanyx, under the linter host (`nanyx.analysis.host`), built into a lint build and run over a
/// module by the compiler's linter host, as a linter package will be (see the Linters page in Work in progress, #3171).
module TranspilerWasmTests.Features.Linters.RunLintBuild

open System.IO
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

let private rule id =
    { Id = id; Title = $"Title of {id}"; DefaultSeverity = Severity.Warning; Unnecessary = false; Description = "" }

/// Reports a top-level def whose name is longer than five letters, from the tree alone, with a fix; and every top-level
/// def's type, which it asks `Analysis` for and has it describe.
let private probe = """::host nanyx.analysis.host
::install nanyx.analysis

import (
  nanyx.analysis/analysis as (Report)
  nanyx.analysis/syntax as (Module, TopLevelItem)
)

def check: [Analysis] TopLevelItem -> list(Report) = { item ->
  match item.kind
    | #def(d) ->
      def long: list(Report) =
        if string.length(d.name) > 5 then
          [
            (
              rule = "longName"
              span = item.span
              message = "'{d.name}' is long"
              subject = #some(d.name)
              related = []
              fixes = [(title = "Call it x", edits = [(span = item.span, newText = "def x = 1")])]
            )
          ]
        else
          []
      def typed: list(Report) =
        match typeOf(d.value.id)
          | #some(ty) ->
            [(rule = "typed", span = item.span, message = "'{d.name}' is {describe(ty)}", subject = #none, related = [], fixes = [])]
          | #unknown -> []
      list.flatMap([long, typed], { reports -> reports })
    | _ -> []
}

@entry
def lint: [Analysis] Module -> list(Report) = { m -> list.flatMap(m, check) }
"""

/// The probe linter's lint build, built once for the tests here.
let private probeBuild =
    lazy
        (use directory = new TempDirectory()
         let path = Path.Combine(directory.Path, "probe.nyx")
         File.WriteAllText(path, probe.ReplaceLineEndings "\n")
         lintBuildOf path)

let private linting (linter: Linter) (source: string) =
    Compiler.compileWith { Linters = [ linter ]; Severities = Map.empty } (source.ReplaceLineEndings "\n")

[<Fact>]
let ``A linter written in Nanyx reports from the tree and from what it asks, with fixes`` () =
    let linter = lintBuildLinter "probe" [ rule "longName"; rule "typed" ] probeBuild.Value
    let result = linting linter "def answer = 42\n\ndef x = \"hi\"\n"
    let shown = result.Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, diagnostic.Message) |> List.sort
    Assert.Equal<(string * string) list>(
        [ "probe/longName", "'answer' is long"; "probe/typed", "'answer' is int"; "probe/typed", "'x' is string" ],
        shown
    )
    let long = result.Diagnostics |> List.find (fun diagnostic -> diagnostic.Code = "probe/longName")
    Assert.Equal(Some "answer", long.Subject)
    Assert.Equal(Some 0, long.Span |> Option.map _.Start)
    Assert.Equal("def x = 1", (Assert.Single((Assert.Single(long.Fixes)).Edits)).NewText)

[<Fact>]
let ``A linter that reports a rule it doesn't declare fails, and the module still compiles`` () =
    let linter = lintBuildLinter "probe" [ rule "typed" ] probeBuild.Value
    let result = linting linter "def answer = 42\n"
    Assert.True(result.Typed.IsSome)
    let failed = Assert.Single(result.Diagnostics)
    Assert.Equal("probe/linterFailed", failed.Code)
    Assert.Contains("longName", failed.Message)

[<Fact>]
let ``A linter that crashes fails with its message, and the module still compiles`` () =
    use directory = new TempDirectory()
    let path = Path.Combine(directory.Path, "crashing.nyx")
    File.WriteAllText(
        path,
        """::host nanyx.analysis.host
::install nanyx.analysis

import (
  nanyx.analysis/analysis as (Report)
  nanyx.analysis/syntax as (Module)
)

@entry
def lint: [Analysis] Module -> list(Report) = { m -> crash("no rules today") }
""".ReplaceLineEndings "\n"
    )
    let linter = lintBuildLinter "crashing" [ rule "anything" ] (lintBuildOf path)
    let result = linting linter "def answer = 42\n"
    Assert.True(result.Typed.IsSome)
    let failed = Assert.Single(result.Diagnostics)
    Assert.Equal("crashing/linterFailed", failed.Code)
    Assert.Contains("no rules today", failed.Message)
