module ParserTests.Features.TypeArguments

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers

// Type arguments: a parameter that takes a type, written `Type(a)` (or the old way, `type a` or `into: type`), and the
// value `type Point` passed for it (see the Functions page).

let private ty kind = TypeExpr.synthetic kind
let private named name = ty (TypeName name)
let private var name = ty (TypeVar name)

let private annotationOf source =
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, _, Some annotation, _))) ] -> annotation
    | other -> failwith $"Unexpected parse: %A{other}"

let private valueOf source =
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, _, _, value))) ] -> value
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A positional type parameter is written Type(a)`` () =
    annotationOf "def f: (Type(a), string) -> a = f"
    |> should equal (ty (TypeFunc (ty (TypeTuple [ ty (TypeApply ("Type", [ var "a" ])); named "string" ]), var "a")))

[<Fact>]
let ``A named type parameter is written into: Type(a)`` () =
    match annotationOf "def f: (string, into: Type(a)) -> a = f" with
    | Kind (TypeFunc (Kind (TypeRecord [ TypeField ("0", false, Some text, None); TypeField ("into", false, Some written, None) ]), result)) ->
        text |> should equal (named "string")
        written |> should equal (ty (TypeApply ("Type", [ var "a" ])))
        result |> should equal (var "a")
    | other -> failwith $"Unexpected annotation: %A{other}"

[<Fact>]
let ``The old positional type parameter type a parses, for the compiler to report with its fix`` () =
    annotationOf "def f: (type a, string) -> a = f"
    |> should equal (ty (TypeFunc (ty (TypeTuple [ ty (TypeArgument (var "a")); named "string" ]), var "a")))

[<Fact>]
let ``The old positional type parameter can follow the other positional parameters`` () =
    annotationOf "def f: (string, type a) -> a = f"
    |> should equal (ty (TypeFunc (ty (TypeTuple [ named "string"; ty (TypeArgument (var "a")) ]), var "a")))

[<Fact>]
let ``The old named type parameter into: type parses, for the compiler to report with its fix`` () =
    match annotationOf "def f: (string, into: type) -> into = f" with
    | Kind (TypeFunc (Kind (TypeRecord [ TypeField ("0", false, Some text, None); TypeField ("into", false, Some keyword, None) ]), result)) ->
        text |> should equal (named "string")
        keyword |> should equal (ty TypeKeyword)
        result |> should equal (named "into")
    | other -> failwith $"Unexpected annotation: %A{other}"

[<Fact>]
let ``The old positional type parameter is a type variable`` () =
    let message, at = parseErrorOf "def f: (type Point, string) -> Point = f\n"
    Assert.Contains("A parameter that takes a type is written with its type, as in `Type(a)`", message)
    Assert.StartsWith("Point", at)

[<Fact>]
let ``type Point is a value`` () =
    match valueOf "def p = deserialise(type Point, text)" with
    | Kind (FunctionCall ("deserialise", _, [ Kind (TupleExpr [ Kind (TypeValue written); Kind (IdentifierExpr "text") ]) ])) -> written |> should equal (named "Point")
    | other -> failwith $"Unexpected value: %A{other}"

[<Fact>]
let ``type takes one type, so a match's next arm is not part of it`` () =
    let source =
        """def t = { b ->
  match b
    | #yes -> type int
    | #no -> type list(int)
}
"""
    match valueOf (source.ReplaceLineEndings "\n") with
    | Kind (Lambda (_, Kind (Block [ Kind (ExprStatement (Kind (Match (_, [ _, Kind (TypeValue first); _, Kind (TypeValue second) ])))) ]), _)) ->
        first |> should equal (named "int")
        second |> should equal (ty (TypeApply ("list", [ named "int" ])))
    | other -> failwith $"Unexpected value: %A{other}"

[<Fact>]
let ``A named type argument is passed by name`` () =
    match valueOf "def p = parse(text, into = type Point)" with
    | Kind (FunctionCall ("parse", _, [ Kind (RecordExpr [ PositionalField _; NamedField ("into", Kind (TypeValue written)) ]) ])) ->
        written |> should equal (named "Point")
    | other -> failwith $"Unexpected value: %A{other}"
