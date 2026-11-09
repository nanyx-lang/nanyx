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
        "type Person = (firstName: string surname: string)\n" +
        "def p = Person(firstName = \"Joe\", surname = \"Bloggs\")"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    match typed.Types.["p"] with
    | TyAlias("Person", [], TyRecord fields) ->
        Assert.Equal(TyPrimitive "string", fields.["firstName"])
        Assert.Equal(TyPrimitive "string", fields.["surname"])
    | other -> Assert.True(false, $"Unexpected constructor type: %A{other}")

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
        "def f = { x, y -> \"\" }\n" +
        "def args = 4, 5\n" +
        "def result = f(args)"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "string", typed.Types.["result"])

[<Fact>]
let ``Typecheck reports mismatch for missing tuple args`` () =
    let source =
        "def f = { x, y -> \"\" }\n" +
        "def result = f(4)"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Type mismatch", message)
    Assert.Contains("(", message)
    Assert.Contains("int vs (", message)

[<Fact>]
let ``Typecheck allows record width subtyping`` () =
    let source =
        "def f: (a: int) -> int = { r -> 1 }\n" +
        "def args = (a = 1, b = 2)\n" +
        "def result = f(args)"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["result"])

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
            "type Point = (x: int, y: int)\n" +
            "def shift: Point -> Point = { q -> Point(x = q.x + 1, y = q.y + 1) }\n" +
            "def a = shift(Point(x = 3, y = 4)).x\n" +
            "def q = shift(Point(x = 3, y = 4))\n" +
            "def b: int = q.y\n")
    Assert.True(declared.Diagnostics.IsEmpty, sprintf "%A" declared.Diagnostics)
    let inferred =
        Compiler.compile (
            "def make = { n -> (value = n, label = \"v\") }\n" +
            "def m = make(1)\n" +
            "def s: string = m.label\n" +
            "def t = (1, \"two\")\n" +
            "def pick = { p -> p }\n" +
            "def u: string = pick(t).1\n")
    Assert.True(inferred.Diagnostics.IsEmpty, sprintf "%A" inferred.Diagnostics)
    let missing = Compiler.compile "type Point = (x: int, y: int)\ndef id: Point -> Point = { p -> p }\ndef z = id(Point(x = 1, y = 2)).z"
    Assert.False(missing.Diagnostics.IsEmpty, "a field the result lacks should still be an error")
