module Nanyx.Compiler.Tests.Features.Ordering

open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

/// The contexts the one def of `typed` that requires any was inferred to require.
let private inferredContexts (typed: TypedModule) =
    match typed.InferredContextTypes |> Map.values |> List.ofSeq with
    | [ contexts ] -> contexts
    | other -> failwith $"Expected one def with inferred contexts, got %A{other}"

/// Each of `cases`, a source with the message of its one diagnostic, a missing context, and the text it is at.
let private assertMissingContexts (cases: (string * string * string) list) =
    for source, expectedMessage, expectedAt in cases do
        let message, at = missingContext source
        Assert.Equal(expectedMessage, message)
        Assert.Equal(expectedAt, at)

[<Fact>]
let ``A def that does not write its type requires Ord of a type variable it orders`` () =
    for op in [ "<"; "<="; ">"; ">=" ] do
        let typed = compileClean $"def first = {{ x, y -> if x {op} y then x else y }}\ndef b = first(1, 2)\n"
        match inferredContexts typed with
        | [ TyApply("Ord", [ TyVar _ ]) ] -> ()
        | other -> failwith $"Unexpected inferred contexts: %A{other}"

[<Fact>]
let ``Ordering a type built from a type variable requires Ord of the variable`` () =
    let typed = compileClean "def before = { xs, x -> xs < [x] }\ndef b = before([1], 2)\n"
    match inferredContexts typed with
    | [ TyApply("Ord", [ TyVar _ ]) ] -> ()
    | other -> failwith $"Unexpected inferred contexts: %A{other}"

[<Fact>]
let ``A def that writes its type must require Ord of a type variable it orders`` () =
    assertMissingContexts
        [ "def less: a, a -> bool = { x, y -> x < y }\n",
          "'less' orders values of type 'a' with <, so its type must require Ord(a): add [Ord(a)] to the type of 'less'",
          "x < y"
          "def atLeast: list(a), list(a) -> bool = { xs, ys -> xs >= ys }\n",
          "'atLeast' orders values of type 'a' with >=, so its type must require Ord(a): add [Ord(a)] to the type of 'atLeast'",
          "xs >= ys"
          "def sorted: list(a) -> list(a) = { xs -> list.sort(xs) }\n",
          "'list.sort' requires the context 'Ord(a)', which is not available here: add [Ord(a)] to the type of 'sorted'",
          "list.sort" ]

[<Fact>]
let ``A def that requires Ord of a type variable orders through the instance it is passed`` () =
    let typed = compileClean "def less: [Ord(a)] a, a -> bool = { x, y -> x < y }\ndef b = less(\"a\", \"b\")\n"
    match instancesOf typed with
    | [ InstanceInScope({ Kind = TypeApply("Ord", _) }, _); _ ] -> ()
    | other -> failwith $"Unexpected instances: %A{other}"

[<Fact>]
let ``Numbers, bools, strings, tuples and lists of them are ordered by the compiler's own Ord`` () =
    let typed =
        compileClean (
            "def a = 1 < 2 and 1.5 >= 2.5 and false < true and \"a\" <= \"b\"\n" +
            "def b = (1, \"a\") < (1, \"b\") and [1, 2] > [1] and [(1, [\"x\"])] < [(2, [])]\n")
    Assert.Empty(instancesOf typed)

[<Fact>]
let ``Records, tags and functions have no order`` () =
    assertMissingContexts
        [ "def a = (x = 1) < (x = 2)\n",
          "'(x: int)' cannot be ordered with <: records have no order. Make it a nominal type to attach a 'compare' to it, or order by a key with list.sortBy",
          "(x = 1) < (x = 2)"
          "type Color = #red | #blue\ndef less: Color, Color -> bool = { p, q -> p > q }\n",
          "'Color' cannot be ordered with >: tags have no order. Make it a nominal type to attach a 'compare' to it, or order by a key with list.sortBy",
          "p > q"
          "def f = { x -> x }\ndef d = f < f\n",
          "'a -> a' cannot be ordered with <: functions have no order",
          "f < f"
          "def e = [(x = 1)] <= [(x = 2)]\n",
          "'list(x: int)' cannot be ordered with <=: its elements are records, and records have no order",
          "[(x = 1)] <= [(x = 2)]"
          "type Version = protected (major: int, minor: int)\ndef v = Version(major = 1, minor = 2) < Version(major = 1, minor = 3)\n",
          "'Version' cannot be ordered with <: records have no order. Attach 'Version.compare', or order by a key with list.sortBy",
          "Version(major = 1, minor = 2) < Version(major = 1, minor = 3)" ]

[<Fact>]
let ``A call that requires Ord of a type with no order is an error at the call`` () =
    assertMissingContexts
        [ "def g = list.sort([(x = 1)])\n",
          "'list.sort' orders values of type '(x: int)', which cannot be ordered: records have no order",
          "list.sort" ]

[<Fact>]
let ``A nominal type orders with the compare attached to it`` () =
    let typed =
        compileClean (
            "type Version = protected (major: int, minor: int)\n" +
            "def Version.compare: Version, Version -> #less | #equal | #greater = { a, b -> int.compare(a.major, b.major) }\n" +
            "def v = { major -> Version(major = major, minor = 0) }\n" +
            "def a = v(1) < v(2)\n" +
            "def b = list.sort([v(2), v(1)])\n")
    Assert.Contains(InstanceSynthesized [ "compare", "Version.compare" ], instancesOf typed)

[<Fact>]
let ``An Ord loaded with use orders a built-in type in its block`` () =
    let typed =
        compileClean (
            "def backwards: Ord(string) = (compare = { a, b -> string.compare(b, a) })\n" +
            "def a = {\n  use backwards\n  \"a\" < \"b\"\n}\n" +
            "def b = \"a\" < \"b\"\n")
    match instancesOf typed with
    | [ InstanceInScope({ Kind = TypeApply("Ord", _) }, _) ] -> ()
    | other -> failwith $"Unexpected instances: %A{other}"

[<Fact>]
let ``List.sortBy takes a key of any type with an order`` () =
    let typed = compileClean "def sorted = list.sortBy([\"bb\", \"a\"], { s -> s })\ndef byPair = list.sortBy([1, 2], { n -> (n, \"x\") })\n"
    Assert.Equal("list(string)", Unifier.tyToString typed.Types.["sorted"])
    Assert.Equal("list(int)", Unifier.tyToString typed.Types.["byPair"])

[<Fact>]
let ``Only a nominal type may attach compare, and a built-in type's attached compare is not read`` () =
    let source = "type Point = (x: int, y: int)\ndef Point.compare: Point, Point -> #less | #equal | #greater = { p, q -> int.compare(p.x, q.x) }\n"
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.UnattachableDefinition.Code, diagnostic.Code)
    Assert.Equal(
        "'Point.compare' cannot be defined: 'Point' is a structural type, so it is only ordered by its structure, and records and tags have none. Declare it `protected` to define its own order",
        diagnostic.Message)
    // The prelude attaches `Int.compare`, but `List.sort` of ints uses the compiler's own.
    let typed = compileClean "def sorted = list.sort([2, 1])\n"
    Assert.Equal<ContextInstance list>([ InstanceSynthesized [ "compare", "__compare" ] ], instancesOf typed)

[<Fact>]
let ``Calling a local function that orders with records is an error at the call`` () =
    let cases =
        [ "def b = {\n  def lt = { x, y -> x < y }\n  lt((a = 1), (a = 2))\n}\n",
          "'lt' orders values of type '(a: int)', which cannot be ordered: records have no order",
          "lt"
          "def g: a -> bool = { v ->\n  def lt = { x, y -> x < y }\n  lt(v, v)\n}\n",
          "'lt' requires the context 'Ord(a)', which is not available here: add [Ord(a)] to the type of 'g'",
          "lt" ]
    for source, expectedMessage, expectedAt in cases do
        let message, at = missingContext source
        Assert.Equal(expectedMessage, message)
        Assert.Equal(expectedAt, at)

[<Fact>]
let ``A def that calls a local function ordering its own type variable requires Ord of it`` () =
    let typed = compileClean "def g = { v, w ->\n  def lt = { x, y -> x < y }\n  lt(v, w)\n}\ndef b = g(1, 2)\n"
    match inferredContexts typed with
    | [ TyApply("Ord", [ TyVar _ ]) ] -> ()
    | other -> failwith $"Unexpected inferred contexts: %A{other}"

[<Fact>]
let ``A list of a type with its own compare is ordered through it, element by element`` () =
    let typed =
        compileClean (
            "type Version = protected (major: int, minor: int)\n" +
            "def Version.compare: Version, Version -> #less | #equal | #greater = { a, b -> int.compare(a.major, b.major) }\n" +
            "def v = { major -> Version(major = major, minor = 0) }\n" +
            "def a = [v(1), v(2)] < [v(3)]\n" +
            "def before: [Ord(a)] list(a), list(a) -> bool = { xs, ys -> xs >= ys }\n")
    Assert.Contains(InstanceSynthesized [ "compare", "Version.compare" ], instancesOf typed)
    let instances = typed.ContextInstances |> Map.values |> Seq.concat |> Seq.choose id |> List.ofSeq
    Assert.Contains(instances, function InstanceInScope(Kind (TypeApply("Ord", [ Kind (TypeVar "a") ])), _) -> true | _ -> false)
