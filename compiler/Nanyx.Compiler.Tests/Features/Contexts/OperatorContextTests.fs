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

/// The contexts the def `name` of `source` requires, as its annotation writes them once it is checked.
let private writtenContexts (source: string) (name: string) =
    let typed = compileClean source
    typed.Items
    |> List.pick (function
        | TypedDef (TypedValueDef(_, defName, Some { Kind = TypeWithContext(contexts, _) }, _))
        | TypedDef (TypedValueDef(_, defName, Some { Kind = TypeContext contexts }, _)) when defName = name ->
            Some (contexts |> List.map _.Kind)
        | _ -> None)

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
let ``A def that writes two contexts with a member of one name calls each instance's member`` () =
    let source =
        """
def both: [Add(a) & Add(b)] a, a, b, b -> (a, b) = { x, y, p, q -> (x + y, p - q) }
def same: [Eq(a) & Eq(b)] a, a, b, b -> bool = { x, y, p, q -> x == y and p != q }
def negated: [Add(a) & Add(b)] a, b -> (a, b) = { x, p -> (-x, -p) }
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
let ``A def the module names as a value requires nothing, as a function that requires a context cannot be a value`` () =
    // #599: `add` passed to `fold` would need a wrapper the backend cannot make.
    let typed = compileClean "def add = { x, y -> x + y }\ndef total = [1, 2, 3] \\list.fold(0, add)\n"
    Assert.True(typed.InferredContextTypes.IsEmpty)
    Assert.Equal("int, int -> int", Unifier.tyToString typed.Types.["add"])

[<Fact>]
let ``A def inferred to require Add is called with ints, floats and a type that attaches the members`` () =
    let source =
        "type $Money = (pence: int)\n" +
        "def $Money.zero: $Money = $Money(pence = 0)\n" +
        "def $Money.`+`: $Money, $Money -> $Money = { p, q -> $Money(pence = p.pence + q.pence) }\n" +
        "def $Money.`-`: $Money, $Money -> $Money = { p, q -> $Money(pence = p.pence - q.pence) }\n" +
        "def $Money.negate: $Money -> $Money = { p -> $Money(pence = 0 - p.pence) }\n" +
        "def add = { x, y -> x + y }\n" +
        "def i = add(1, 2)\n" +
        "def f = add(1.5, 2.5)\n" +
        "def m = add($Money(pence = 1), $Money(pence = 2))\n"
    let typed = compileClean source
    Assert.Equal<string list>([ "int"; "float"; "$Money" ], [ "i"; "f"; "m" ] |> List.map (fun name -> Unifier.tyToString typed.Types.[name]))

[<Fact>]
let ``An operator required by its member is the prelude's context that has it`` () =
    let add = TypeApply("Add", [ TypeExpr.synthetic (TypeVar "a") ])
    let num = TypeApply("Num", [ TypeExpr.synthetic (TypeVar "a") ])
    let eq = TypeApply("Eq", [ TypeExpr.synthetic (TypeVar "a") ])
    Assert.Equal<TypeExprKind list>([ add ], writtenContexts "def add: [`+`: a, a -> a] = { + }\ndef f = add(1.5, 2.5)\n" "add")
    Assert.Equal<TypeExprKind list>([ add ], writtenContexts "def sub: [`-`: (a, a) -> a] a, a -> a = { x, y -> x - y }\n" "sub")
    Assert.Equal<TypeExprKind list>([ num ], writtenContexts "def lerp: [(`+`: a, a -> a) & (`*`: a, a -> a)] a, a, a -> a = { a, b, t -> a + b * t }\n" "lerp")
    Assert.Equal<TypeExprKind list>([ add; eq ], writtenContexts "def f: [(`+`: a, a -> a) & (`==`: a, a -> bool)] a, a -> a = { x, y -> if x == y then x + x else x }\n" "f")
    // One written both ways is required once.
    Assert.Equal<TypeExprKind list>([ add ], writtenContexts "def add: [Add(a) & (`+`: a, a -> a)] a, a -> a = { x, y -> x + y }\n" "add")

[<Fact>]
let ``Only an operator of a prelude context, at its member's type, can be required by its member`` () =
    Assert.Equal(
        (DiagnosticCodes.UnknownContext.Code, "Only an operator can be required by its member, and 'toString' is not one: it is a member of ToString, so require [ToString(a)]", "toString: a -> string"),
        onlyDiagnostic "def write: [toString: a -> string] a -> string = { x -> \"Wrote: {x}\" }\n")
    Assert.Equal(
        (DiagnosticCodes.UnknownContext.Code, "No context in the prelude has a member '`<`': it is read through Ord's 'compare', so require [Ord(a)]", "`<`: a, a -> bool"),
        onlyDiagnostic "def less: [`<`: a, a -> bool] a, a -> bool = { x, y -> x < y }\n")
    Assert.Equal(
        (DiagnosticCodes.UnknownContext.Code, "No context in the prelude has a member '`+++`'", "`+++`: a, a -> a"),
        onlyDiagnostic "def join: [`+++`: a, a -> a] a, a -> a = { x, y -> x }\n")
    Assert.Equal(
        (DiagnosticCodes.TypeMismatch.Code, "'`+`' is 'a, a -> a' in Add(a), which 'a -> a' is not for any 'a'", "a -> a"),
        onlyDiagnostic "def add: [`+`: a -> a] a -> a = { x -> x }\n")
