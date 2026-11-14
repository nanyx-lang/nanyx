module Nanyx.Compiler.Tests.Features.Linters.Linters

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

let private rule id severity =
    { Id = id; Title = $"Title of {id}"; DefaultSeverity = severity; Description = "" }

/// Reports every top-level def under `everyDef`, at a warning unless configured otherwise.
let private everyDef =
    { Name = "test"
      Rules = [ rule "everyDef" Severity.Warning; rule "neverReported" Severity.Hint ]
      Lint =
        fun model ->
            model.Syntax
            |> List.choose (function
                | Kind (Def (ValueDef(_, name, _, _))) as item ->
                    Some { Rule = "everyDef"; Span = item.Span; Message = $"def {name}"; Related = []; Fixes = [] }
                | _ -> None) }

let private throwing =
    { Name = "broken"; Rules = [ rule "anything" Severity.Warning ]; Lint = fun _ -> failwith "boom" }

let private options linters severities =
    { Linters = linters; Severities = Map.ofList severities }

let private source = "def one = 1\ndef two = one + 1"

[<Fact>]
let ``The official linter is registered, and runs only when turned on`` () =
    let official = Assert.Single(Linters.all)
    Assert.Equal("default", official.Name)
    Assert.Equal<string list>(
        [ "unusedBindings"; "nonTailRecursion"; "unreachableArms"; "unusedImports"; "noTagInCommon"; "unusedPrivateDefinitions"; "unreachableCode"; "useInBraces"; "strayDocComments"; "plusBetweenContexts"; "unannotatedExports"; "unusedResults"; "shadowedPreludeNames"; "semicolons" ],
        official.Rules |> List.map (fun rule -> rule.Id))
    // Linters are opt-in: compiling without saying which to run runs none.
    Assert.Empty((Compiler.compile "def f = { x -> 1 }").Diagnostics)
    Assert.Empty((compileWithOfficial source).Diagnostics)
    let unused = Assert.Single((compileWithOfficial "def f = { x -> 1 }").Diagnostics)
    Assert.Equal("default/unusedBindings", unused.Code)

[<Fact>]
let ``A report becomes a diagnostic with the rule's code, title and default severity`` () =
    let result = Compiler.compileWith (options [ everyDef ] []) source
    Assert.True(result.Typed.IsSome)
    Assert.Equal(2, result.Diagnostics.Length)
    let first = result.Diagnostics.Head
    Assert.Equal("test/everyDef", first.Code)
    Assert.Equal("Title of everyDef", first.Title)
    Assert.Equal(Severity.Warning, first.Severity)
    Assert.Equal("def one", first.Message)
    Assert.Equal("def one = 1", textAt source first.Span.Value)
    Assert.False(Diagnostics.isError first)

[<Fact>]
let ``A rule can be given another severity, turned off, or promoted to an error`` () =
    let severityWith configured =
        (Compiler.compileWith (options [ everyDef ] [ "test/everyDef", configured ]) source).Diagnostics
        |> List.map (fun diagnostic -> diagnostic.Severity)
    Assert.Equal<Severity list>([ Severity.Info; Severity.Info ], severityWith (Some Severity.Info))
    Assert.Empty(severityWith None)
    let promoted = Compiler.compileWith (options [ everyDef ] [ "test/everyDef", Some Severity.Error ]) source
    Assert.True(promoted.Diagnostics |> List.forall Diagnostics.isError)
    // The module still type-checked, so the editor keeps its types; the errors are what fail the compile.
    Assert.True(promoted.Typed.IsSome)
    Assert.True(promoted.TypeChecked)

[<Fact>]
let ``The linters do not look at a module that does not type-check`` () =
    let result = Compiler.compileWith (options [ everyDef ] []) "def one = missing\ndef two = 2"
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal("Unknown identifier 'missing'", diagnostic.Message)

[<Fact>]
let ``Configuring another code leaves a rule at its default`` () =
    let result = Compiler.compileWith (options [ everyDef ] [ "test/neverReported", None; "other/everyDef", Some Severity.Error ]) source
    Assert.True(result.Diagnostics |> List.forall (fun diagnostic -> diagnostic.Severity = Severity.Warning))
    Assert.Equal(2, result.Diagnostics.Length)

[<Fact>]
let ``A linter that throws is reported once and does not stop the others`` () =
    let result = Compiler.compileWith (options [ throwing; everyDef ] []) source
    Assert.True(result.Typed.IsSome)
    match result.Diagnostics with
    | failure :: rest ->
        Assert.Equal("broken/linterFailed", failure.Code)
        Assert.Equal(Severity.Warning, failure.Severity)
        Assert.Contains("'broken'", failure.Message)
        Assert.Contains("boom", failure.Message)
        Assert.Equal(None, failure.Span)
        Assert.Equal<string list>([ "test/everyDef"; "test/everyDef" ], rest |> List.map (fun diagnostic -> diagnostic.Code))
    | [] -> failwith "Expected the failure"
    // A broken linter can be turned off like any rule.
    let silenced = Compiler.compileWith (options [ throwing ] [ "broken/linterFailed", None ]) source
    Assert.Empty(silenced.Diagnostics)

[<Fact>]
let ``A linter that reports a rule it does not declare fails, and its reports are left out`` () =
    let undeclared =
        { everyDef with
            Name = "sloppy"
            Lint = fun model -> everyDef.Lint model |> List.map (fun report -> { report with Rule = "madeUp" }) }
    let diagnostic = Assert.Single(Compiler.compileWith (options [ undeclared ] []) source |> fun result -> result.Diagnostics)
    Assert.Equal("sloppy/linterFailed", diagnostic.Code)
    Assert.Contains("'madeUp'", diagnostic.Message)

[<Fact>]
let ``Linters do not run on a module that does not type-check`` () =
    let result = Compiler.compileWith (options [ everyDef ] []) "def one = missing"
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.UnknownIdentifier.Code, diagnostic.Code)

[<Fact>]
let ``The model is the module as written, with what type checking found`` () =
    let mutable seen = None
    let recorder =
        { Name = "recorder"
          Rules = []
          Lint = fun model -> seen <- Some model; [] }
    let source = "@entry\ndef main = {\n  def x = 1\n  x + 1\n}"
    Assert.Empty((Compiler.compileWith (options [ recorder ] []) source).Diagnostics)
    let model = seen.Value
    // Attributes are kept: the model is the syntax before `@entry` is applied.
    Assert.True(model.Syntax |> List.exists (function Kind (Attribute("entry", None)) -> true | _ -> false))
    // The `x` in `x + 1` resolves to the local `x`, which has a type.
    let local = model.Symbols |> Map.values |> Seq.find (fun symbol -> symbol.Name = "x")
    Assert.Equal(LocalValue, local.Kind)
    Assert.Contains(local.Id, model.References |> Map.values)
    Assert.True(model.SymbolTypes.ContainsKey local.Id)
    Assert.NotEmpty(model.Types)

[<Fact>]
let ``Only the module being compiled is analyzed, not the modules it imports`` () =
    withTempDir (fun dir ->
        File.WriteAllText(Path.Combine(dir, "lib.nyx"), "module lib\nexport def helper = 1\n")
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(mainPath, "import lib\nimport nanyx/console\ndef answer = helper + 1\n")
        let result = Compiler.compileFileWith (options [ everyDef ] []) mainPath
        let diagnostic = Assert.Single(result.Diagnostics)
        Assert.Equal("def answer", diagnostic.Message)
        Assert.Equal(Path.GetFullPath mainPath, diagnostic.Span.Value.File))
