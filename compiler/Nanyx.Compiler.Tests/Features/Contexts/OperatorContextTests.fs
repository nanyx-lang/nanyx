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

/// The one diagnostic `source` has: its code, its message and the text it is reported at.
let private onlyDiagnostic (source: string) =
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] ->
        let span = diagnostic.Span.Value
        diagnostic.Code, diagnostic.Message, source.Substring(span.Start, span.End - span.Start)
    | other -> failwith $"Expected one diagnostic, got %A{other}"

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
let ``Operators on two type variables are on ints, as a def cannot yet require two contexts with a member of one name`` () =
    let typed = compileClean "def r = { x, y, p, q -> (x + y, p * q) }\n"
    Assert.True(typed.InferredContextTypes.IsEmpty)
    Assert.Equal("int, int, int, int -> (int, int)", Unifier.tyToString typed.Types.["r"])
    // The call requires `Add` of the list's elements, so the operator on the other two parameters is on ints.
    let typed = compileClean "def r = { x, y, xs -> (x + y, list.sum(xs)) }\n"
    Assert.Equal<string list>([ "Add" ], inferredContexts typed |> List.map fst)
    Assert.StartsWith("int, int, list(", Unifier.tyToString typed.Types.["r"])

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
    Assert.Equal<TypeExprKind list>([ num ], writtenContexts "def lerp: [`+`: a, a -> a + `*`: a, a -> a] a, a, a -> a = { a, b, t -> a + b * t }\n" "lerp")
    Assert.Equal<TypeExprKind list>([ add; eq ], writtenContexts "def f: [`+`: a, a -> a + `==`: a, a -> bool] a, a -> a = { x, y -> if x == y then x + x else x }\n" "f")
    // One written both ways is required once.
    Assert.Equal<TypeExprKind list>([ add ], writtenContexts "def add: [Add(a) + `+`: a, a -> a] a, a -> a = { x, y -> x + y }\n" "add")

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
