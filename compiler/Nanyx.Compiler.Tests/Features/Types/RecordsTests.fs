module Nanyx.Compiler.Tests.Features.Types.Records

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``Typecheck supports record constructor syntax`` () =
    let source =
        normalized """type Person = (firstName: string surname: string)
def p = Person(firstName = "Joe", surname = "Bloggs")"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    match typed.Types.["p"] with
    | TyAlias("Person", [], TyRecord fields) ->
        Assert.Equal(TyPrimitive "string", fields.["firstName"])
        Assert.Equal(TyPrimitive "string", fields.["surname"])
    | other -> Assert.True(false, $"Unexpected constructor type: %A{other}")

[<Fact>]
let ``A lone record type argument in the application's own brackets is the same type`` () =
    let source =
        normalized """def count: list(name: string) -> int = { xs -> list.length(xs) }
def bracketed: list((name: string)) -> int = count
def n: int = count([(name = "Ada")])
"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal(TyFunc(TyApply("list", [ TyRecord(Map.ofList [ "name", TyPrimitive "string" ]) ]), TyPrimitive "int", []), result.Typed.Value.Types.["count"])

[<Fact>]
let ``Typecheck supports mixed positional and named records`` () =
    let source = "def value = (1, 2, z = 3)"
    let result = Compiler.compile source
    let typed = result.Typed.Value
    match typed.Types.["value"] with
    | TyRecord fields when fields.ContainsKey("0") && fields.ContainsKey("1") && fields.ContainsKey("z") ->
        Assert.Equal(TyPrimitive "int", fields.["0"])
        Assert.Equal(TyPrimitive "int", fields.["1"])
        Assert.Equal(TyPrimitive "int", fields.["z"])
    | other -> Assert.True(false, $"Unexpected mixed record type: %A{other}")

[<Fact>]
let ``Typecheck allows tuple args variable for function call`` () =
    let source =
        normalized """def f = { x, y -> "" }
def args = 4, 5
def result = f(args)"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "string", typed.Types.["result"])

[<Fact>]
let ``Typecheck reports mismatch for missing tuple args`` () =
    let source =
        normalized """def f = { x, y -> "" }
def result = f(4)"""
    let result = Compiler.compile source
    assertReports DiagnosticCodes.TypeMismatch result
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Type mismatch", message)
    Assert.Contains("(", message)
    Assert.Contains("int vs (", message)

// A record type is closed: it has exactly the fields it names (#470).
[<Fact>]
let ``A closed record parameter rejects a record with a field more`` () =
    let source =
        normalized """def f: (x: int, y: int) -> int = { x, y -> x + y }
def data = (x = 1, y = 2, z = 3)
def result = f(data)"""
    let diagnostic = Assert.Single (Compiler.compile source).Diagnostics
    Assert.StartsWith("Record field mismatch: {x, y, z} vs {x, y}: only the first has z.", diagnostic.Message)

[<Fact>]
let ``A closed record parameter rejects a record with a field fewer`` () =
    let source =
        normalized """def f: (x: int, y: int) -> int = { x, y -> x + y }
def result = f((x = 1))"""
    let diagnostic = Assert.Single (Compiler.compile source).Diagnostics
    Assert.StartsWith("Record field mismatch: {x} vs {x, y}: only the second has y.", diagnostic.Message)

[<Fact>]
let ``A closed record annotation rejects a record with a field more`` () =
    let diagnostic = Assert.Single (Compiler.compile "type Point = (x: int, y: int)\ndef p: Point = (x = 1, y = 2, z = 3)").Diagnostics
    Assert.StartsWith("Record field mismatch: {x, y, z} vs {x, y}: only the first has z.", diagnostic.Message)

[<Fact>]
let ``A function that only reads some fields takes a record with more`` () =
    let source =
        normalized """def area = { r -> r.width * r.height }
def result = area((width = 2, height = 3, colour = "red"))"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["result"])

[<Fact>]
let ``Typecheck gives a record update the record's type and checks the updated fields`` () =
    let ok = Compiler.compile "def alice = (name = \"Alice\", age = 30)\ndef older = alice with age = 31\ndef n: int = older.age"
    Assert.True(ok.Diagnostics.IsEmpty, sprintf "%A" ok.Diagnostics)
    let wrongType = Compiler.compile "def alice = (name = \"Alice\", age = 30)\ndef older = alice with age = \"old\""
    Assert.False(wrongType.Diagnostics.IsEmpty, "updating age with a string should not type-check")
    let unknownField = Compiler.compile "def alice = (name = \"Alice\", age = 30)\ndef older = alice with height = 1"
    Assert.False(unknownField.Diagnostics.IsEmpty, "updating a field the record lacks should not type-check")

[<Fact>]
let ``Typecheck allows member access on the result of a call`` () =
    // #60: the call's result used to be an unsolved variable when the member access looked for fields.
    let declared =
        Compiler.compile (
            withFinalNewline """type Point = (x: int, y: int)
def shift: Point -> Point = { q -> Point(x = q.x + 1, y = q.y + 1) }
def a = shift(Point(x = 3, y = 4)).x
def q = shift(Point(x = 3, y = 4))
def b: int = q.y""")
    Assert.True(declared.Diagnostics.IsEmpty, sprintf "%A" declared.Diagnostics)
    let inferred =
        Compiler.compile (
            withFinalNewline """def make = { n -> (value = n, label = "v") }
def m = make(1)
def s: string = m.label
def t = (1, "two")
def pick = { p -> p }
def u: string = pick(t).1""")
    Assert.True(inferred.Diagnostics.IsEmpty, sprintf "%A" inferred.Diagnostics)
    let missing = Compiler.compile "type Point = (x: int, y: int)\ndef id: Point -> Point = { p -> p }\ndef z = id(Point(x = 1, y = 2)).z"
    Assert.False(missing.Diagnostics.IsEmpty, "a field the result lacks should still be an error")
