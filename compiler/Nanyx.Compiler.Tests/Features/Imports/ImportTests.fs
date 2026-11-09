module Nanyx.Compiler.Tests.Features.Imports

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``Import brings members into scope unqualified`` () =
    withTempDir (fun dir ->
        let modulePath = Path.Combine(dir, "mod.nyx")
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(modulePath, "module mod\nexport def foo = 1")
        File.WriteAllText(mainPath, "module main\nimport \"mod.nyx\"\ndef value = foo")
        let result = Compiler.compileFile mainPath
        Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
        let typed = result.Typed.Value
        Assert.Equal(TyPrimitive "int", typed.Types.["value"]))

[<Fact>]
let ``Import with alias is qualified`` () =
    withTempDir (fun dir ->
        let modulePath = Path.Combine(dir, "mod.nyx")
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(modulePath, "module mod\nexport def foo = 1")
        File.WriteAllText(mainPath, "module main\nimport \"mod.nyx\" as m1\ndef value = m1.foo")
        let result = Compiler.compileFile mainPath
        Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
        let typed = result.Typed.Value
        Assert.Equal(TyPrimitive "int", typed.Types.["value"]))

[<Fact>]
let ``Import with alias does not bring unqualified names`` () =
    withTempDir (fun dir ->
        let modulePath = Path.Combine(dir, "mod.nyx")
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(modulePath, "module mod\nexport def foo = 1")
        File.WriteAllText(mainPath, "module main\nimport \"mod.nyx\" as m1\ndef value = foo")
        let result = Compiler.compileFile mainPath
        Assert.False(result.Diagnostics.IsEmpty)
        let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
        Assert.Contains("Unknown identifier", message))

let private csvAndJson =
    [ "csv.nyx", "module csvParser\nexport def parse = { s -> 1 }\nexport def csvOnly = 1"
      "json.nyx", "module jsonParser\nexport def parse = { s -> 2 }" ]

[<Fact>]
let ``A name two imports export is ambiguous where it is used`` () =
    let result = compileImportingAll csvAndJson "module main\nimport \"csv.nyx\"\nimport \"json.nyx\"\ndef data = parse(\"x\")"
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Contains("'parse' is ambiguous between 'csvParser.parse' and 'jsonParser.parse'", (result.Diagnostics |> List.head).Message)

[<Fact>]
let ``A name two imports export is ambiguous as a value too`` () =
    let result = compileImportingAll csvAndJson "module main\nimport \"csv.nyx\"\nimport \"json.nyx\"\ndef f = parse"
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Contains("'parse' is ambiguous", (result.Diagnostics |> List.head).Message)

[<Fact>]
let ``A name two imports export is not an error when unused`` () =
    let result = compileImportingAll csvAndJson "module main\nimport \"csv.nyx\"\nimport \"json.nyx\"\ndef value = csvOnly"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``Aliasing one import resolves an ambiguous name`` () =
    let result = compileImportingAll csvAndJson "module main\nimport \"csv.nyx\" as csv\nimport \"json.nyx\"\ndef a = parse(\"x\")\ndef b = csv.parse(\"x\")"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``The module's own definition wins over a name two imports export`` () =
    let result = compileImportingAll csvAndJson "module main\nimport \"csv.nyx\"\nimport \"json.nyx\"\ndef parse = { s -> 3 }\ndef data = parse(\"x\")"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``A local shadows a name two imports export`` () =
    let result = compileImportingAll csvAndJson "module main\nimport \"csv.nyx\"\nimport \"json.nyx\"\ndef data = { parse -> parse }"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``Importing the same module twice is not ambiguous`` () =
    let result = compileImportingAll csvAndJson "module main\nimport \"csv.nyx\"\nimport \"./csv.nyx\"\ndef data = parse(\"x\")"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``A module imported along two paths is checked once`` () =
    let modules =
        [ "shared.nyx", "module shared\nexport def foo = 1"
          "left.nyx", "module left\nimport \"shared.nyx\"\nexport def l = foo"
          "right.nyx", "module right\nimport \"shared.nyx\"\nexport def r = foo" ]
    let result = compileImportingAll modules "module main\nimport \"left.nyx\"\nimport \"right.nyx\"\nimport \"shared.nyx\"\nimport \"./shared.nyx\" as s2\ndef a = foo\ndef b = s2.foo"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    let fooSymbols =
        typed.References
        |> Map.values
        |> Seq.distinct
        |> Seq.filter (fun id -> typed.Symbols |> Map.tryFind id |> Option.exists (fun symbol -> symbol.Name = "foo"))
        |> Seq.toList
    Assert.Equal(1, fooSymbols.Length)

[<Fact>]
let ``An error in a module imported along two paths is reported once`` () =
    let modules =
        [ "shared.nyx", "module shared\nexport def foo = undefinedName"
          "left.nyx", "module left\nimport \"shared.nyx\"\nexport def l = foo"
          "right.nyx", "module right\nimport \"shared.nyx\"\nexport def r = foo" ]
    let result = compileImportingAll modules "module main\nimport \"left.nyx\"\nimport \"right.nyx\"\ndef a = l"
    let unknown = result.Diagnostics |> List.filter (fun diag -> diag.Message.Contains "undefinedName")
    Assert.Equal(1, unknown.Length)

let private valueDefinitionNames (result: CompileResult) =
    result.Typed.Value.Module
    |> List.choose (function
        | Kind (Def (ValueDef(_, name, _, _))) -> Some name
        | _ -> None)
    |> Set.ofList

[<Fact>]
let ``Only the imported definitions a module reaches are inlined`` () =
    let modules =
        [ "mod.nyx",
          "module mod\n" +
          "def c = { 1 }\n" +
          "def b = { c() + 1 }\n" +
          "export def a = { b() + 1 }\n" +
          "export def unused = { 5 }" ]
    let result = compileImportingAll modules "module main\nimport \"mod.nyx\"\ndef x = a()"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let names = valueDefinitionNames result
    Assert.Contains("a", names)
    Assert.Contains("mod.b", names)
    Assert.Contains("mod.c", names)
    Assert.DoesNotContain("unused", names)

[<Fact>]
let ``Only the standard-library definitions a program reaches are inlined`` () =
    let result = Compiler.compile "import nanyx/text\ndef x = String.contains(\"abc\", \"b\")"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let names = valueDefinitionNames result
    Assert.Contains("String.contains", names)
    Assert.DoesNotContain("String.padStart", names)
    Assert.DoesNotContain("String.words", names)

[<Fact>]
let ``Definitions reached through an aliased import are inlined`` () =
    let modules = [ "mod.nyx", "module mod\nexport def a = { 1 }\nexport def unused = { 5 }" ]
    let result = compileImportingAll modules "module main\nimport \"mod.nyx\" as m\ndef x = m.a()"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let names = valueDefinitionNames result
    Assert.Contains("m.a", names)
    Assert.DoesNotContain("m.unused", names)

[<Fact>]
let ``An attached name two imports define is ambiguous where it is used`` () =
    let modules =
        [ "one.nyx", "module one\nexport def String.shout: string -> string = { s -> s }"
          "two.nyx", "module two\nexport def String.shout: string -> string = { s -> s ++ \"!\" }" ]
    let result = compileImportingAll modules "module main\nimport \"one.nyx\"\nimport \"two.nyx\"\ndef a = \"x\" \\ shout"
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Contains("'String.shout' is ambiguous between 'one.String.shout' and 'two.String.shout'", (result.Diagnostics |> List.head).Message)

[<Fact>]
let ``Import does not bring private definitions into scope`` () =
    let result = compileImporting "module mod\nexport def foo = 1\ndef bar = 2" "module main\nimport \"mod.nyx\"\ndef value = bar"
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Contains("Unknown identifier", (result.Diagnostics |> List.head).Message)

[<Fact>]
let ``Import with alias does not expose private definitions`` () =
    let result = compileImporting "module mod\nexport def foo = 1\ndef bar = 2" "module main\nimport \"mod.nyx\" as m1\ndef value = m1.bar"
    Assert.False(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Private definitions of an import cannot be reached by the module's name`` () =
    let result = compileImporting "module mod\nexport def foo = 1\ndef bar = 2" "module main\nimport \"mod.nyx\"\ndef value = mod.bar"
    Assert.False(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Exported definitions of an import can use its private ones`` () =
    let result = compileImporting "module mod\nexport def foo = { bar + 1 }\ndef bar = 2" "module main\nimport \"mod.nyx\"\ndef value = foo()"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["value"])

[<Fact>]
let ``Import does not bring private types into scope`` () =
    let result = compileImporting "module mod\ntype Secret = int\nexport def foo = 1" "module main\nimport \"mod.nyx\"\ndef value: Secret = 1"
    Assert.False(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Private definitions of a nested module are not visible outside it`` () =
    let result = Compiler.compile "module inner =\n  def secret = 1\n  export def open = secret + 1\ndef value = inner.secret"
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Contains("'inner.secret' is not exported from module 'inner'", (result.Diagnostics |> List.head).Message)

[<Fact>]
let ``Private functions of a nested module cannot be called outside it`` () =
    let result = Compiler.compile "module inner =\n  def secret = { x -> x + 1 }\n  export def open = { x -> secret(x) }\ndef value = inner.secret(1)"
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Contains("is not exported from module 'inner'", (result.Diagnostics |> List.head).Message)

[<Fact>]
let ``Exported definitions of a nested module are visible outside it`` () =
    let result = Compiler.compile "module inner =\n  def secret = 1\n  export def open = secret + 1\ndef value = inner.open"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["value"])
