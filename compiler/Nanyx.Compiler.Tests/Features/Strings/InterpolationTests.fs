module Nanyx.Compiler.Tests.Features.Strings.Interpolation

open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``A hole of a built-in type uses the compiler's own ToString`` () =
    let typed = compileClean "def s = \"{1} {1.5} {true} {\"text\"} {#red}\"\n"
    let holes =
        typed.Module
        |> List.collect (function
            | Kind (Def (ValueDef(_, "s", _, Kind (InterpolatedString parts)))) ->
                parts |> List.choose (function StringExpr hole -> Some hole.Id | StringText _ -> None)
            | _ -> [])
    Assert.Equal(5, holes.Length)
    for hole in holes do
        Assert.Equal<ContextInstance option list>([ Some InstanceBuiltIn ], typed.ContextInstances.[hole])

[<Fact>]
let ``A hole uses the ToString synthesized from a type's attached toString`` () =
    let typed =
        compileClean (
            "type Point = (x: int, y: int)\n" +
            "def Point.toString = { p -> \"({p.x}, {p.y})\" }\n" +
            "def p = Point(x = 1, y = 2)\n" +
            "def s = \"at {p}\"\n")
    Assert.Contains(InstanceSynthesized [ "toString", "Point.toString" ], instancesOf typed)

[<Fact>]
let ``A hole uses an instance loaded with use ahead of the compiler's own`` () =
    let typed =
        compileClean (
            "def s = {\n" +
            "  def hashInt: ToString(int) = ( toString = { n -> \"#{n}\" } )\n" +
            "  use hashInt\n" +
            "  \"{1}\"\n" +
            "}\n")
    let loaded =
        instancesOf typed
        |> List.choose (function InstanceInScope (Kind (TypeApply("ToString", [ Kind (TypeName "int") ]))) -> Some () | _ -> None)
    Assert.Equal(1, loaded.Length)

[<Fact>]
let ``A def that does not write its type requires ToString of a type variable it interpolates`` () =
    let typed = compileClean "def show = { value -> \"<{value}>\" }\ndef s = show(3) ++ show(\"x\")\n"
    match typed.InferredContextTypes |> Map.values |> List.ofSeq with
    | [ [ TyApply("ToString", [ TyVar _ ]) ] ] -> ()
    | other -> failwith $"Unexpected inferred contexts: %A{other}"

[<Fact>]
let ``A def that writes its type must require ToString of a type variable it interpolates`` () =
    let message, at = missingContext "def show: a -> string = { value -> \"<{value}>\" }\n"
    Assert.Equal(
        "Interpolating a value of type 'a' requires the context 'ToString(a)', which is not available here: add [ToString(a)] to the type of 'show'",
        message)
    Assert.Equal("value", at)

[<Fact>]
let ``A def that requires ToString of a type variable can interpolate it`` () =
    compileClean "def show: [ToString(a)] a -> string = { value -> \"<{value}>\" }\ndef s = show(3)\n" |> ignore

[<Fact>]
let ``A hole of a parameter typed by the call it is passed to finds its ToString once the module is inferred`` () =
    compileClean "def labels: list(int) -> list(string) = { xs -> xs \\List.map({ x -> \"#{x}\" }) }\n" |> ignore

[<Fact>]
let ``A local function generic in the type it interpolates is not an error`` () =
    compileClean "def f = { n ->\n  def g = { y -> \"{y}!\" }\n  g(n + 1)\n}\n" |> ignore

[<Fact>]
let ``A hole of a type with no ToString is an error at the hole`` () =
    let cases =
        [ "def f = { n -> n }\ndef s = \"{f}\"\n",
          "'a -> a' cannot be interpolated: it is a function, and functions have no toString",
          "f"
          "type $Money = private int\ndef m = $Money(5)\ndef s = \"{m}\"\n",
          "'$Money' cannot be interpolated because it has no toString: attach '$Money.toString', or load an instance of `ToString($Money)` with `use`",
          "m" ]
    for source, expectedMessage, expectedAt in cases do
        let message, at = missingContext source
        Assert.Equal(expectedMessage, message)
        Assert.Equal(expectedAt, at)

[<Fact>]
let ``A function that requires ToString is passed the compiler's own for a tag`` () =
    let typed = compileClean "def angled = { v -> \"<{v}>\" }\ndef color: #red | #green = #green\ndef s = angled(#red) ++ angled(color)\n"
    Assert.Equal<ContextInstance list>(
        [ InstanceSynthesized [ "toString", "__toString" ]; InstanceSynthesized [ "toString", "__toString" ] ],
        instancesOf typed |> List.filter (function InstanceSynthesized _ -> true | _ -> false))

[<Fact>]
let ``An attached toString of the wrong type is reported at the hole`` () =
    let message, _ =
        missingContext "type Point = (x: int)\ndef Point.toString: Point -> int = { p -> p.x }\ndef s = \"{Point(x = 1)}\"\n"
    Assert.Equal(
        "Interpolating a value of type 'Point' requires the context 'ToString(Point)', but 'Point.toString' has the type 'Point -> int', where 'toString' must be 'Point -> string'",
        message)
