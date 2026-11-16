module Nanyx.Compiler.Tests.Features.OperatorContexts

open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

/// The contexts the one def of `typed` that requires any was inferred to require, each with the variable it is of.
let private inferredContexts (typed: TypedModule) =
    match typed.InferredContextTypes |> Map.values |> List.ofSeq with
    | [ contexts ] ->
        contexts
        |> List.map (function
            | TyApply(context, [ TyVar v ]) -> context, v.Id
            | other -> failwith $"Unexpected inferred context: %A{other}")
    | other -> failwith $"Expected one def with inferred contexts, got %A{other}"

[<Fact>]
let ``A def that does not write its type requires the prelude's smallest context with the operator it uses on a type variable`` () =
    let cases =
        [ "{ x, y -> x + y }", "Add"
          "{ x, y -> x - y }", "Add"
          "{ x -> -x }", "Add"
          "{ x, y -> x * y }", "Num"
          "{ x, y -> x / y }", "Num"
          "{ x, y -> x % y }", "Num" ]
    for body, context in cases do
        Assert.Equal<string list>([ context ], inferredContexts (compileClean $"def f = {body}\n") |> List.map fst)

[<Fact>]
let ``Operators on one type variable require the contexts that cover them`` () =
    // `Num` has every member of `Add`, so adding and multiplying needs `Num` alone.
    Assert.Equal<string list>([ "Num" ], inferredContexts (compileClean "def lerp = { a, b, t -> a + (b - a) * t }\n") |> List.map fst)
    // No one context has `+` and `==`.
    Assert.Equal<string list>([ "Add"; "Eq" ], inferredContexts (compileClean "def f = { x, y -> if x == y then x + x else x }\n") |> List.map fst |> List.sort)
    // A call that requires the same context of the same variable adds nothing.
    Assert.Equal<string list>([ "Add" ], inferredContexts (compileClean "def f = { x, xs -> x + list.sum(xs) }\n") |> List.map fst)

[<Fact>]
let ``Operators on two type variables require the contexts of each`` () =
    // #690: each variable's operators call the members of its own instance.
    let cases =
        [ "def r = { x, y, p, q -> (x + y, p * q) }", [ "Add"; "Num" ], "a, a, b, b -> (a, b)"
          "def r = { x, y, p, q -> x == y and p == q }", [ "Eq"; "Eq" ], "a, a, b, b -> bool"
          "def r = { x, p -> (-x, -p) }", [ "Add"; "Add" ], "a, b -> (a, b)"
          // The call requires `Add` of the list's elements, another variable than the operator's.
          "def r = { x, y, xs -> (x + y, list.sum(xs)) }", [ "Add"; "Add" ], "a, a, list(b) -> (a, b)" ]
    for source, contexts, ty in cases do
        let typed = compileClean (source + "\n")
        let inferred = inferredContexts typed
        Assert.Equal<string list>(contexts, inferred |> List.map fst |> List.sort)
        Assert.Equal(2, inferred |> List.map snd |> List.distinct |> List.length)
        Assert.Equal(ty, Unifier.tyToDisplayString Ascii typed.Types.["r"])

[<Fact>]
let ``A def whose type has an open record in it requires the contexts its calls need`` () =
    // #958: the requirement is kept as the annotation the def would write, which can write an open record.
    let typed = compileClean """def total = { items -> items \list.map { .price } \list.sum }"""
    Assert.Equal<string list>([ "Add" ], inferredContexts typed |> List.map fst)
    Assert.Equal("list(price: a, ...) -> a", Unifier.tyToDisplayString Ascii typed.Types.["total"])

[<Fact>]
let ``A def that writes two contexts with a member of one name calls each instance's member`` () =
    let source =
        """
def both: [Add(a), Add(b)] a, a, b, b -> (a, b) = { x, y, p, q -> (x + y, p - q) }
def same: [Eq(a), Eq(b)] a, a, b, b -> bool = { x, y, p, q -> x == y and p != q }
def negated: [Add(a), Add(b)] a, b -> (a, b) = { x, p -> (-x, -p) }
def t = both(1, 2, 1.5, 2.0)
def u = same(1, 1, "a", "b")
def v = negated(1, 2.5)
"""
    let typed = compileClean source
    Assert.Equal<string list>([ "(int, float)"; "bool"; "(int, float)" ], [ "t"; "u"; "v" ] |> List.map (fun name -> Unifier.tyToString typed.Types.[name]))

[<Fact>]
let ``A def whose operands turn out to be numbers requires nothing`` () =
    let cases =
        [ "def r = { x -> x + 1 }\n", "int -> int"
          "def r = { x -> x * 1.5 }\n", "float -> float"
          "def r = { x, y ->\n  def s = x + y\n  s * 2\n}\n", "int, int -> int"
          "def r = { x, y ->\n  def s = x - y\n  int.toString(s)\n}\n", "int, int -> string" ]
    for source, expected in cases do
        let typed = compileClean source
        Assert.True(typed.InferredContextTypes.IsEmpty, $"Unexpected inferred contexts in: {source}")
        Assert.Equal(expected, Unifier.tyToString typed.Types.["r"])

[<Fact>]
let ``A local function requires no contexts, so its arithmetic is on numbers`` () =
    let typed = compileClean "def r = { n ->\n  def twice = { v -> v + v }\n  twice(n)\n}\n"
    Assert.True(typed.InferredContextTypes.IsEmpty)
    Assert.Equal("int -> int", Unifier.tyToString typed.Types.["r"])

[<Fact>]
let ``A def the module names as a value requires the context of its arithmetic all the same`` () =
    // #1693: `add` passed to `fold` is passed with the instance found where it is named (#599).
    let typed =
        compileClean
            """
def add = { x, y -> x + y }
def total = [1.5, 2.5] \list.fold(0.0, add)
def sum = add(1.5, 2.0)
"""
    Assert.Equal<string list>([ "Add" ], inferredContexts typed |> List.map fst)
    Assert.Equal("a, a -> a", Unifier.tyToDisplayString Ascii typed.Types.["add"])
    Assert.Equal<string list>([ "float"; "float" ], [ "total"; "sum" ] |> List.map (fun name -> Unifier.tyToString typed.Types.[name]))

[<Fact>]
let ``A def inferred to require Add is called with ints, floats and a type that attaches the members`` () =
    let source =
        normalized """type Money = protected (pence: int)
def Money.zero: Money = Money(pence = 0)
def Money.`+`: Money, Money -> Money = { p, q -> Money(pence = p.pence + q.pence) }
def Money.`-`: Money, Money -> Money = { p, q -> Money(pence = p.pence - q.pence) }
def Money.negate: Money -> Money = { p -> Money(pence = 0 - p.pence) }
def add = { x, y -> x + y }
def i = add(1, 2)
def f = add(1.5, 2.5)
def m = add(Money(pence = 1), Money(pence = 2))
"""
    let typed = compileClean source
    Assert.Equal<string list>([ "int"; "float"; "Money" ], [ "i"; "f"; "m" ] |> List.map (fun name -> Unifier.tyToString typed.Types.[name]))

