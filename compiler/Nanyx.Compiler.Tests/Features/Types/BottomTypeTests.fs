module Nanyx.Compiler.Tests.Features.Types.BottomType

open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

/// A function that never gives a value, for the tests to call: it calls itself for ever.
let private stop = "def stop: int -> undefined = { x -> stop(x) }\n"

[<Fact>]
let ``undefined can be written in an annotation`` () =
    Assert.Equal<string list>([ "int -> undefined" ], typesOf stop [ "stop" ])

[<Fact>]
let ``A branch that never gives a value takes the type of the other, whichever comes first`` () =
    let source =
        stop
        + "def first = { b -> if b then stop(0) else 1 }\n"
        + "def second = { b -> if b then \"a\" else stop(0) }\n"
        + "def matched = { n -> match n | 0 -> stop(0) | _ -> 2.5 }\n"
        + "def matchedLast = { n -> match n | 0 -> #zero | _ -> stop(n) }"
    Assert.Equal<string list>(
        [ "bool -> int"; "bool -> string"; "int -> float"; "int -> #zero" ],
        typesOf source [ "first"; "second"; "matched"; "matchedLast" ])

[<Fact>]
let ``A list item that never gives a value takes the type of the others, wherever it is`` () =
    let source =
        stop
        + """
def first = [stop(0), 1]
def last = [1, stop(0)]
"""
    Assert.Equal<string list>([ "list(int)"; "list(int)" ], typesOf source [ "first"; "last" ])

[<Fact>]
let ``A call that never gives a value can be given where any type is expected`` () =
    let source =
        stop
        + "def add: int, int -> int = { x, y -> x + y }\n"
        + "def argument = add(1, stop(0))\n"
        + "def result: int -> string = { x -> stop(x) }"
    Assert.Equal<string list>([ "int"; "int -> string" ], typesOf source [ "argument"; "result" ])

[<Fact>]
let ``Only undefined can be given where undefined is expected`` () =
    let source = "def ignore: undefined -> int = { _ -> 0 }\ndef n = ignore(1)"
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] -> Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    | other -> Assert.Fail($"Expected one diagnostic, got %A{other}")

let private others =
    [ TyPrimitive "int"
      TyFunc(TyPrimitive "int", TyPrimitive "string", [])
      TyTag("some", Some (TyPrimitive "int"))
      TyUnion [ TyTag("a", None); TyTag("b", None) ]
      TyApply("list", [ TyPrimitive "bool" ])
      TyRecord(Map.ofList [ "x", TyPrimitive "int" ], FieldOrder.none) ]

[<Fact>]
let ``undefined is assignable to every type, binding nothing`` () =
    for other in others do
        match Solving.unify [ (Unifier.bottomType, other, Assignable, SourceSpan.none) ] with
        | Ok subst -> Assert.True(subst.IsEmpty, $"undefined given for {other} bound %A{subst}")
        | Error (message, _) -> Assert.Fail($"undefined given for {other}: {message}")

[<Fact>]
let ``No other type is assignable to undefined, nor equal to one expected to be undefined`` () =
    // A closed union is left out: two closed unions agree when one's tags are among the other's, and `undefined` is
    // the union of no tags.
    for other in others |> List.filter (function TyUnion _ -> false | _ -> true) do
        for constraint' in
            [ (other, Unifier.bottomType, Assignable, SourceSpan.none)
              (other, Unifier.bottomType, Equal, SourceSpan.none) ] do
            match Solving.unify [ constraint' ] with
            | Ok _ -> Assert.Fail($"%A{constraint'} was satisfied")
            | Error _ -> ()

[<Fact>]
let ``undefined is equal to any type expected of it`` () =
    // `[crash("no")]` is a `list(int)` where one is expected (#2530).
    for other in others do
        Assert.True(Solving.unify [ (Unifier.bottomType, other, Equal, SourceSpan.none) ] |> Result.isOk, $"%A{other}")

[<Fact>]
let ``undefined given where a variable is expected binds nothing, so the variable takes the other value's type`` () =
    let var = TyVar.ordinary 1 None
    let int = TyPrimitive "int"
    let bottom = (Unifier.bottomType, TyVar var, Assignable, SourceSpan.none)
    let given = (int, TyVar var, Assignable, SourceSpan.none)
    for constraints in [ [ bottom; given ]; [ given; bottom ] ] do
        match Solving.unify constraints with
        | Ok subst -> Assert.Equal(Some int, subst |> Map.tryFind var.Id)
        | Error (message, _) -> Assert.Fail(message)

[<Fact>]
let ``The other branch is still checked against what is expected`` () =
    // `undefined` agrees with one type; it does not make the other branch agree with anything.
    let source = stop + "def bad = { b -> if b then stop(0) else 1 }\ndef text: string = bad(true)"
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] -> Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    | other -> Assert.Fail($"Expected one diagnostic, got %A{other}")

[<Fact>]
let ``A function whose result is a variable found nowhere else in its type gives back undefined`` () =
    // No value of the variable can be made, so the function never returns (#1864).
    let typed =
        compileClean (
            "def loop = { x -> loop(x) }\ndef curried = { a -> { b -> loop(b) } }\ndef first = { xs: list(int) -> list.head(xs) }\n"
            + "def empty = { -> [] }\ndef id = { x -> x }\n")
    Assert.Equal("a -> undefined", asciiTypeIn typed "loop")
    Assert.Equal("a -> b -> undefined", asciiTypeIn typed "curried")
    // A result that is in the input's type is as generic as before. A `[]` nothing says the elements of is a
    // `list(undefined)` (#3129).
    Assert.Equal("list(int) -> #some(int) | #emptyList", asciiTypeIn typed "first")
    Assert.Equal("() -> list(undefined)", asciiTypeIn typed "empty")
    Assert.Equal("a -> a", asciiTypeIn typed "id")

[<Fact>]
let ``A result variable an annotation names stays generic`` () =
    let typed = compileClean "def loop: a -> b = { x -> loop(x) }\n"
    Assert.Equal("a -> b", asciiTypeIn typed "loop")

[<Fact>]
let ``A call to a function that never returns fits wherever a value is expected`` () =
    compileClean "def loop = { x -> loop(x) }\ndef pick = { n: int -> if n > 0 then loop(n) else \"none\" }\ndef s: string = pick(1)\n" |> ignore
