module Nanyx.Compiler.Tests.Features.Equality

open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

/// The contexts the one def of `typed` that requires any was inferred to require.
let private inferredContexts (typed: TypedModule) =
    match typed.InferredContextTypes |> Map.values |> List.ofSeq with
    | [ contexts ] -> contexts
    | other -> failwith $"Expected one def with inferred contexts, got %A{other}"

[<Fact>]
let ``A def that does not write its type requires Eq of a type variable it compares`` () =
    for op in [ "=="; "!=" ] do
        let typed = compileClean $"def same = {{ x, y -> x {op} y }}\ndef b = same(1, 2)\n"
        match inferredContexts typed with
        | [ TyApply("Eq", [ TyVar _ ]) ] -> ()
        | other -> failwith $"Unexpected inferred contexts: %A{other}"

[<Fact>]
let ``Comparing a type built from a type variable requires Eq of the variable`` () =
    let typed = compileClean "def holds = { xs, x -> xs == [x] }\ndef b = holds([1], 1)\n"
    match inferredContexts typed with
    | [ TyApply("Eq", [ TyVar _ ]) ] -> ()
    | other -> failwith $"Unexpected inferred contexts: %A{other}"

[<Fact>]
let ``A def that calls one requiring Eq with a list of its own type variable requires Eq of the variable`` () =
    let typed = compileClean "def same: [Eq(a)] a, a -> bool = { x, y -> x == y }\ndef wrapped = { x, y -> same([x], [y]) }\n"
    match inferredContexts typed with
    | [ TyApply("Eq", [ TyVar _ ]) ] -> ()
    | other -> failwith $"Unexpected inferred contexts: %A{other}"

[<Fact>]
let ``A def that writes its type must require Eq of a type variable it compares`` () =
    let cases =
        [ "def same: a, a -> bool = { x, y -> x == y }\n",
          "'same' compares values of type 'a' with ==, so its type must require Eq(a): add [Eq(a)] to the type of 'same'",
          "x == y"
          "def differ: list(a), list(a) -> bool = { xs, ys -> xs != ys }\n",
          "'differ' compares values of type 'a' with !=, so its type must require Eq(a): add [Eq(a)] to the type of 'differ'",
          "xs != ys" ]
    for source, expectedMessage, expectedAt in cases do
        let message, at = missingContext source
        Assert.Equal(expectedMessage, message)
        Assert.Equal(expectedAt, at)

[<Fact>]
let ``A def that requires Eq of a type variable can compare it, and types built from it`` () =
    compileClean (
        withFinalNewline """def same: [Eq(a)] a, a -> bool = { x, y -> x == y }
def differ: [Eq(a)] a, a -> bool = { x, y -> x != y }
def holds: [Eq(a)] list(a), a -> bool = { xs, x -> x != x or xs == [x] }
def b = same(1, 2) or differ("a", "b") or holds([#red], #red)""")
    |> ignore

[<Fact>]
let ``A def that requires an imported context checks after one that had a context inferred`` () =
    // The context's own type variable is numbered by the prelude, and must not be taken for one of this module's.
    compileClean "def inferred = { p, q -> p == q }\ndef written: [Eq(a)] a, a -> bool = { x, y -> x == y }\n" |> ignore

[<Fact>]
let ``Comparing values with no type variable in their type requires nothing`` () =
    let typed =
        compileClean (
            withFinalNewline """type Point = (x: int, y: int)
def same: Point, Point -> bool = { p, q -> p == q }
def firsts: list(int) -> bool = { xs -> xs \list.any({ x -> x == 1 }) }""")
    Assert.True(typed.InferredContextTypes.IsEmpty)

[<Fact>]
let ``A local function generic in the type it compares is not an error`` () =
    compileClean "def f = { n ->\n  def same = { x, y -> x == y }\n  same(n + 1, 2)\n}\n" |> ignore

[<Fact>]
let ``Calling a local function that compares with functions is an error at the call`` () =
    let cases =
        [ "def f = { n -> n + 1 }\ndef b = {\n  def same = { x, y -> x == y }\n  same(f, f)\n}\n",
          "'same' compares values of type 'int -> int', which cannot be compared: functions have no equality",
          "same"
          "def f = { n -> n + 1 }\ndef b = {\n  def holds = { xs, x -> xs == [x] }\n  holds([f], f)\n}\n",
          "'holds' compares values of type 'int -> int', which cannot be compared: functions have no equality",
          "holds"
          "def g: a -> bool = { v ->\n  def same = { x, y -> x == y }\n  same(v, v)\n}\n",
          "'same' requires the context 'Eq(a)', which is not available here: add [Eq(a)] to the type of 'g'",
          "same" ]
    for source, expectedMessage, expectedAt in cases do
        let message, at = missingContext source
        Assert.Equal(expectedMessage, message)
        Assert.Equal(expectedAt, at)

[<Fact>]
let ``Passing a local function that compares as a value is checked at the type it is passed as`` () =
    let message, at =
        missingContext """def check = { f -> f({ x -> x + 1 }, { x -> x + 1 }) }
def b = {
  def same = { a, b -> a == b }
  check(same)
}
"""
    Assert.Equal("'same' compares values of type 'int -> int', which cannot be compared: functions have no equality", message)
    Assert.Equal("same", at)
    compileClean
        """def check = { f -> f(1, 2) }
def b = {
  def same = { a, b -> a == b }
  check(same)
}
"""
    |> ignore

[<Fact>]
let ``A def that calls a local function comparing its own type variable requires Eq of it`` () =
    let typed = compileClean "def g = { v ->\n  def same = { x, y -> x == y }\n  same(v, v)\n}\ndef b = g(1)\n"
    match inferredContexts typed with
    | [ TyApply("Eq", [ TyVar _ ]) ] -> ()
    | other -> failwith $"Unexpected inferred contexts: %A{other}"
    let message, at = missingContext "def g = { v ->\n  def same = { x, y -> x == y }\n  same(v, v)\n}\ndef f = { n -> n + 1 }\ndef b = g(f)\n"
    Assert.Equal("'g' compares values of type 'int -> int', which cannot be compared: functions have no equality", message)
    Assert.Equal("g", at)

[<Fact>]
let ``Comparing functions is an error at the comparison`` () =
    let cases =
        [ "def f = { n -> n + 1 }\ndef b = f == f\n",
          "'int -> int' cannot be compared with ==: functions have no equality",
          "f == f"
          "def f = { n -> n + 1 }\ndef b = [f] != [f]\n",
          "'list(int -> int)' cannot be compared with !=: its elements are functions, and functions have no equality",
          "[f] != [f]"
          "type Button = (label: string, onClick: int -> int)\ndef f = { n -> n + 1 }\ndef button = Button(label = \"ok\", onClick = f)\ndef b = button == button\n",
          "'Button' cannot be compared with ==: its field 'onClick' is a function, and functions have no equality",
          "button == button"
          "def f = { n -> n + 1 }\ndef b = #some([f]) == #some([f])\n",
          "'#some(list(int -> int))' cannot be compared with ==: the payload of '#some' holds a function, and functions have no equality",
          "#some([f]) == #some([f])"
          "def applied = { f, g -> f == g or f(1) == g(1) }\n",
          "'int -> a' cannot be compared with ==: functions have no equality",
          "f == g" ]
    for source, expectedMessage, expectedAt in cases do
        let message, at = missingContext source
        Assert.Equal(expectedMessage, message)
        Assert.Equal(expectedAt, at)

[<Fact>]
let ``Passing functions to a def that requires Eq is an error at the call`` () =
    let cases =
        [ "def same = { x, y -> x == y }\ndef b = same(same, same)\n",
          "'same' compares values of type 'a, a -> bool', which cannot be compared: functions have no equality",
          "same"
          "def same = { x, y -> x == y }\ndef f = { n -> n + 1 }\ndef b = same([f], [f])\n",
          "'same' compares values of type 'list(int -> int)', which cannot be compared: its elements are functions, and functions have no equality",
          "same"
          "def f = { n -> n + 1 }\ndef b = list.distinct([f, f])\n",
          "'list.distinct' compares values of type 'int -> int', which cannot be compared: functions have no equality",
          "list.distinct" ]
    for source, expectedMessage, expectedAt in cases do
        let message, at = missingContext source
        Assert.Equal(expectedMessage, message)
        Assert.Equal(expectedAt, at)

[<Fact>]
let ``A call that requires Eq of a type with no == attached passes the compiler's own`` () =
    let typed = compileClean (withFinalNewline """def same = { x, y -> x == y }
def b = same(1, 2) or same(["a"], ["b"])""")
    Assert.Equal<ContextInstance list>(
        [ InstanceSynthesized [ "==", "__equals" ]; InstanceSynthesized [ "==", "__equals" ] ],
        instancesOf typed |> List.filter (function InstanceSynthesized _ -> true | _ -> false))

[<Fact>]
let ``A call that requires Eq of a type with == attached passes that`` () =
    let typed =
        compileClean (
            withFinalNewline """type Email = protected string
def Email.`==`: Email, Email -> bool = { a, b -> true }
def same = { x, y -> x == y }
def b = same(Email("a"), Email("b"))""")
    Assert.Contains(InstanceSynthesized [ "==", "Email.==" ], instancesOf typed)

[<Fact>]
let ``A call that requires Eq of a list of a type variable needs Eq of the variable`` () =
    let message, at =
        missingContext (
            withFinalNewline """def same: [Eq(a)] a, a -> bool = { x, y -> x == y }
def wrapped: a, a -> bool = { x, y -> same([x], [y]) }""")
    Assert.Equal("'same' requires the context 'Eq(list(a))', which needs 'Eq(a)', not available here: add [Eq(a)] to the type of 'wrapped'", message)
    Assert.Equal("same", at)

[<Fact>]
let ``Comparing with a tag joins the operands, leaving the other open to more tags`` () =
    let typed = compileClean "def isNone = { o -> o == #none }\ndef b = isNone(#some(3)) or isNone(#none)\n"
    let isNone = typed.SymbolTypes |> Map.values |> Seq.find (function TyFunc _ -> true | _ -> false)
    Assert.Equal("#none | ... -> bool", Unifier.tyToDisplayString Ascii isNone)
    compileClean "def isRed: #red | #green -> bool = { c -> c == #red }\ndef same = #some(1) != #some(2)\n" |> ignore

[<Fact>]
let ``Comparing tags with payloads of different types is still an error`` () =
    let diagnostic = Assert.Single((Compiler.compile (withFinalNewline """def b = #some(1) == #some("a")""")).Diagnostics)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)

[<Fact>]
let ``A concrete comparison calls the == attached to its type, except in that definition`` () =
    let typed =
        compileClean (
            withFinalNewline """type Point = protected (x: int, y: int)
def Point.`==`: Point, Point -> bool = { p, q -> p == q }
def b = Point(x = 1, y = 2) != Point(x = 1, y = 3)""")
    let calls =
        typed.Module
        |> List.collect (function
            | Kind (Def (ValueDef(_, name, _, value))) ->
                let rec calledIn (expr: Expression) =
                    match expr.Kind with
                    | FunctionCall(callee, _, args) -> callee :: List.collect calledIn args
                    | Lambda(_, body, _) -> calledIn body
                    | BinaryOp(_, _, left, right) -> calledIn left @ calledIn right
                    | _ -> []
                calledIn value |> List.map (fun callee -> name, callee)
            | _ -> [])
        |> List.filter (fun (_, callee) -> callee = "Point.==")
    Assert.Equal<(string * string) list>([ "b", "Point.==" ], calls)

[<Fact>]
let ``A list of a type with its own == is compared through it, element by element`` () =
    let typed =
        compileClean (
            withFinalNewline """type Email = protected string
def Email.`==`: Email, Email -> bool = { a, b -> true }
def b = [Email("a")] != [Email("b")]
def same: [Eq(a)] list(a), list(a) -> bool = { xs, ys -> xs == ys }""")
    Assert.Contains(InstanceSynthesized [ "==", "Email.==" ], instancesOf typed)
    let instances = typed.ContextInstances |> Map.values |> Seq.concat |> Seq.choose id |> List.ofSeq
    Assert.Contains(instances, function InstanceInScope(Kind (TypeApply("Eq", [ Kind (TypeVar "a") ])), _) -> true | _ -> false)
