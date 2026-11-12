module Nanyx.Compiler.Tests.Features.Tooling.Hover

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

// Hover: the type of the expression at a position.

let private hoverWith (names: TypeNotation) (source: string) (offset: int) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Hover.typeAt names result offset

let private hoverAt source offset = hoverWith Unicode source offset

let private hoverType source offset =
    hoverAt source offset |> Option.map (fun info -> info.Type)

let private latinHoverType source offset =
    hoverWith Ascii source offset |> Option.map (fun info -> info.Type)

[<Fact>]
let ``Reading fields of a parameter makes it a record with at least those fields`` () =
    let source =
        "def name = { p -> p.name }\n" +
        "def label = { p -> p.name ++ \" \" ++ p.city }\n"
    Assert.Equal(Some "(name: α, …) -> α", hoverType source (offsetOf source "{ p -> p.name }" 0 0))
    Assert.Equal(Some "(city: string, name: string, …) -> string", hoverType source (offsetOf source "{ p -> p.name ++" 0 0))

[<Fact>]
let ``A record's rest is named only where it appears more than once`` () =
    let source =
        "def names = { people -> match people\n" +
        "  | [] -> []\n" +
        "  | [p, ...rest] -> [p.name, ...names(rest)] }\n" +
        "def keep = { p -> (p.name, p) }\n"
    Assert.Equal(Some "def names: list(name: α, …) -> list(α)", hoverType source (offsetOf source "names" 0 0))
    // What it is given it gives back, other fields and all, so the rest is named to say it is the same in both.
    Assert.Equal(Some "def keep: (name: α, …ω) -> (α, (name: α, …ω))", hoverType source (offsetOf source "keep" 0 0))

[<Fact>]
let ``A function reading fields takes any record that has them`` () =
    let source =
        "def label = { p -> p.name ++ p.city }\n" +
        "def show = {\n" +
        "  | #some(p), n -> p.name ++ n\n" +
        "  | _, _ -> \"none\"\n" +
        "}\n" +
        "def a = label((name = \"a\", city = \"b\"))\n" +
        "def b = label((city = \"b\", name = \"a\", age = 3))\n" +
        "def c = show(#some((name = \"pat\", age = 3)), \"!\")\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal(Some "def a: string", hoverType source (offsetOf source "a = " 0 0))
    Assert.Equal(Some "def c: string", hoverType source (offsetOf source "c = " 0 0))

[<Fact>]
let ``A function reading a field rejects a record without it`` () =
    let result = Compiler.compile "def name = { p -> p.name }\ndef a = name((age = 3))\n"
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Contains("Record field mismatch: {name} vs {age}", (result.Diagnostics |> List.head).Message)

[<Fact>]
let ``Expressions at a position are innermost first`` () =
    let source = "def value = 1 + 2 * 3"
    let syntax =
        match Parser.Program.Api.parseModule source with
        | Ok syntax -> syntax
        | Error message -> failwith message
    let found = Hover.expressionsAt (offsetOf source "2" 0 0) syntax
    match found |> List.map (fun expr -> expr.Kind) with
    | [ LiteralExpr (IntLit 2); BinaryOp("*", _, _); BinaryOp("+", _, _) ] -> ()
    | kinds -> failwithf "Unexpected expressions: %A" kinds
    // Each one encloses the one before it.
    found
    |> List.pairwise
    |> List.iter (fun (inner, outer) ->
        Assert.True(outer.Span.Start <= inner.Span.Start && inner.Span.End <= outer.Span.End))

[<Fact>]
let ``Nothing is found outside every expression`` () =
    let source = "def value = 1 + 2"
    Assert.Equal(None, hoverType source 0)

[<Fact>]
let ``Hover shows the type of literals, locals, parameters, calls and lambdas`` () =
    let source =
        "def double = { n -> n * 2 }\n" +
        "def answer = {\n" +
        "  def half = 21\n" +
        "  double(half)\n" +
        "}\n"
    // The innermost expression wins: the literal, not the multiplication around it.
    Assert.Equal(Some "int", hoverType source (offsetOf source "2" 0 0))
    Assert.Equal(Some "n: int", hoverType source (offsetOf source "n *" 0 0))
    Assert.Equal(Some "int -> int", hoverType source (offsetOf source "{ n" 0 0))
    Assert.Equal(Some "half: int", hoverType source (offsetOf source "half)" 0 0))
    Assert.Equal(Some "int", hoverType source (offsetOf source "(half)" 0 0))
    // On the call's function name, the function's type.
    Assert.Equal(Some "def double: int -> int", hoverType source (offsetOf source "double(" 0 1))

[<Fact>]
let ``Hover on a value passed to dbg shows the value's type`` () =
    // Not a triple-quoted string: a checkout with CRLF line endings would move the offsets the parser sees.
    let source =
        "def main = {\n" +
        "  def x: #some(int) | #none = #none\n" +
        "  dbg(x)\n" +
        "  ()\n" +
        "}\n"
    Assert.Equal(Some "x: #some(int) | #none", hoverType source (offsetOf source "x)" 0 0))

[<Fact>]
let ``Hover inside a guard pattern shows the predicate's types`` () =
    let source =
        "def size: string -> int = { s ->\n" +
        "  match s\n" +
        "    | { .length > 5 } -> 2\n" +
        "    | { x -> x == \"\" } -> 1\n" +
        "    | _ -> 0\n" +
        "}\n"
    Assert.Equal(Some "string -> bool", hoverType source (offsetOf source "{ ." 0 0))
    Assert.Equal(Some "int", hoverType source (offsetOf source "length" 0 0))
    Assert.Equal(Some "x: string", hoverType source (offsetOf source "x ==" 0 0))

[<Fact>]
let ``Hover inside an and-pattern shows each part's types`` () =
    let source =
        "def size: string -> int = { s ->\n" +
        "  match s\n" +
        "    | t & { .length > 5 } -> t.length\n" +
        "    | _ -> 0\n" +
        "}\n"
    Assert.Equal(Some "t: string", hoverType source (offsetOf source "t &" 0 0))
    Assert.Equal(Some "string -> bool", hoverType source (offsetOf source "{ ." 0 0))
    Assert.Equal(Some "int", hoverType source (offsetOf source "length >" 0 0))

[<Fact>]
let ``Hover on a declaration's name shows the type of what it declares`` () =
    let source =
        "def id = { x -> x }\n" +
        "def answer = {\n" +
        "  def half = 21\n" +
        "  half * 2\n" +
        "}\n" +
        "def describe = { o ->\n" +
        "  match o\n" +
        "  | #some(n) -> n + 1\n" +
        "  | _ -> 0\n" +
        "}\n"
    // Top-level defs, generalised.
    Assert.Equal(Some "def id: α -> α", hoverType source (offsetOf source "id" 0 1))
    Assert.Equal(Some "def answer: () -> int", hoverType source (offsetOf source "answer" 0 0))
    // A parameter, a local def and a pattern variable.
    Assert.Equal(Some "x: α", hoverType source (offsetOf source "x ->" 0 0))
    Assert.Equal(Some "half: int", hoverType source (offsetOf source "half" 0 2))
    Assert.Equal(Some "n: int", hoverType source (offsetOf source "(n)" 0 1))
    Assert.Equal(Some "def describe: #some(int) | … -> int", hoverType source (offsetOf source "describe" 0 0))
    // The range is the declared name.
    let info = (hoverAt source (offsetOf source "half" 0 2)).Value
    Assert.Equal(offsetOf source "half" 0 0, info.Span.Start)
    Assert.Equal(offsetOf source "half" 0 4, info.Span.End)

[<Fact>]
let ``Hover range is the span of the expression`` () =
    let source = "def value = 1 + 23"
    let info = (hoverAt source (offsetOf source "23" 0 1)).Value
    Assert.Equal("int", info.Type)
    Assert.Equal(offsetOf source "23" 0 0, info.Span.Start)
    Assert.Equal(offsetOf source "23" 0 2, info.Span.End)

[<Fact>]
let ``Hover on a polymorphic function shows it instantiated at the call`` () =
    let source =
        "def id = { x -> x }\n" +
        "def n = id(5)\n" +
        "def s = id(\"a\")\n"
    Assert.Equal(Some "α -> α", hoverType source (offsetOf source "{ x" 0 0))
    Assert.Equal(Some "x: α", hoverType source (offsetOf source "-> x" 0 3))
    Assert.Equal(Some "def id: int -> int", hoverType source (offsetOf source "id(" 0 0))
    Assert.Equal(Some "def id: string -> string", hoverType source (offsetOf source "id(" 1 0))
    Assert.Equal(Some "string", hoverType source (offsetOf source "id(" 1 4))

[<Fact>]
let ``Type variables are shown as α, β in order of appearance`` () =
    let source = "def pair = { a, b -> (b, a) }"
    Assert.Equal(Some "α, β -> (β, α)", hoverType source (offsetOf source "{" 0 0))

[<Fact>]
let ``Types are shown in ASCII only when it is asked for`` () =
    let source = "def pair = { a, b -> (b, a) }\ndef keep = { p -> (p.name, p) }\ndef name = { p -> p.name }\n"
    Assert.Equal(Some "a, b -> (b, a)", latinHoverType source (offsetOf source "{" 0 0))
    // A record's rest is spread with three dots rather than an ellipsis.
    Assert.Equal(Some "def keep: (name: a, ...z) -> (a, (name: a, ...z))", latinHoverType source (offsetOf source "keep" 0 0))
    Assert.Equal(Some "def name: (name: a, ...) -> a", latinHoverType source (offsetOf source "def name" 0 4))
    Assert.Equal(Some "def name: (name: α, …) -> α", hoverType source (offsetOf source "def name" 0 4))

[<Fact>]
let ``Greek type variable names go through the alphabet, then start again with a subscript`` () =
    let vars = [ for id in 1..26 -> TyVar { Id = id; Name = None } ]
    let shown = Unifier.tyToDisplayString Unicode (TyApply("f", vars))
    Assert.Equal("f(α, β, γ, δ, ε, ζ, η, θ, ι, κ, λ, μ, ν, ξ, ο, π, ρ, σ, τ, υ, φ, χ, ψ, ω, α₁, β₁)", shown)

[<Fact>]
let ``A union's rest variable is named from the end of the alphabet`` () =
    // The catch-all arm leaves the union open for the tags the arms do not name.
    let source =
        "def mapOption = { o, f ->\n" +
        "  match o\n" +
        "  | #some(a) -> #some(f(a))\n" +
        "  | other -> other\n" +
        "}\n"
    Assert.Equal(Some "#some(α) | ω, ([c] α -> β) -> #some(β) | ω", hoverType source (offsetOf source "{" 0 0))
    Assert.Equal(Some "#some(a) | z, ([c] a -> b) -> #some(b) | z", latinHoverType source (offsetOf source "{" 0 0))

[<Fact>]
let ``A union's rest is named only where it appears more than once`` () =
    let source =
        "def describe = { o ->\n" +
        "  match o\n" +
        "  | #some(n) -> n\n" +
        "  | _ -> 0\n" +
        "}\n"
    Assert.Equal(Some "def describe: #some(int) | … -> int", hoverType source (offsetOf source "describe" 0 0))
    Assert.Equal(Some "def describe: #some(int) | ... -> int", latinHoverType source (offsetOf source "describe" 0 0))

[<Fact>]
let ``A def's type is resolved before it is generalised, so a use of it has the inferred type`` () =
    let source = "def increment = { x -> x + 1 }\ndef f = increment\n"
    Assert.Equal(Some "def increment: int -> int", hoverType source (offsetOf source "increment" 1 0))

[<Fact>]
let ``A shorthand match lambda shows its own type, not that of the parameters it makes up`` () =
    let source =
        "def mapOption = { | #some(a), f -> #some(f(a)) | other, _ -> other }\n" +
        "def g = mapOption\n"
    Assert.Equal(Some "#some(α) | ω, ([c] α -> β) -> #some(β) | ω", hoverType source (offsetOf source "{" 0 0))
    Assert.Equal(Some "def mapOption: #some(α) | ω, (α -> β) -> #some(β) | ω", hoverType source (offsetOf source "mapOption" 1 0))

[<Fact>]
let ``A lambda with a pattern parameter shows its own type, and the pattern what it takes apart`` () =
    let source =
        "def add = { (a, b), c -> a + b * c + 1 }\n" +
        "def g = add\n"
    Assert.Equal(Some "(int, int), int -> int", hoverType source (offsetOf source "{" 0 0))
    Assert.Equal(Some "(int, int)", hoverType source (offsetOf source "(a, b)" 0 0))
    Assert.Equal(Some "b: int", hoverType source (offsetOf source "b)" 0 0))
    Assert.Equal(Some "c: int", hoverType source (offsetOf source "c ->" 0 0))

[<Fact>]
let ``A shorthand lambda shows its own type, and its operator the argument written there`` () =
    let source = "def isEven = { % 2 == 0 }\ndef name = { .name }\n"
    Assert.Equal(Some "int -> bool", hoverType source (offsetOf source "{" 0 0))
    // The argument is left out where the operator is, so the operator shows the argument's type.
    Assert.Equal(Some "int", hoverType source (offsetOf source "%" 0 0))
    Assert.Equal(Some "bool", hoverType source (offsetOf source "==" 0 0))
    Assert.Equal(Some "(name: α, …) -> α", hoverType source (offsetOf source "{ .name" 0 0))
    Assert.Equal(Some "α", hoverType source (offsetOf source "name }" 0 0))

[<Fact>]
let ``A rest variable nothing can add tags to is empty`` () =
    // `#some(5)` has no other tags, so neither has what `mapOption` gives back.
    let source =
        "def mapOption = { | #some(a), f -> #some(f(a)) | other, _ -> other }\n" +
        "def main = {\n" +
        "  def a = #some(5) \\mapOption { n -> \"{n}\" }\n" +
        "  a\n" +
        "}\n"
    Assert.Equal(Some "a: #some(string)", hoverType source (offsetOf source "a\n}" 0 0))
    Assert.Equal(Some "def mapOption: #some(int), (int -> string) -> #some(string)", hoverType source (offsetOf source "mapOption {" 0 0))
    // A rest variable that the input still shares stays: what the function is given more of, it gives back.
    Assert.Equal(Some "#some(α) | ω, ([c] α -> β) -> #some(β) | ω", hoverType source (offsetOf source "{" 0 0))
    // So does one shared with a parameter in scope, which a later use could still give tags to.
    let inLambda =
        "def mapOption = { | #some(a), f -> #some(f(a)) | other, _ -> other }\n" +
        "def g = { o ->\n" +
        "  def a = o \\mapOption { n -> n }\n" +
        "  a\n" +
        "}\n"
    Assert.Equal(Some "#some(α) | ω -> #some(α) | ω", hoverType inLambda (offsetOf inLambda "{ o" 0 0))

[<Fact>]
let ``A rest variable a closed union widened into is empty`` () =
    // `getCustomer` gives out three tags and no more, so `mapOption` gives back those three and no more.
    let source =
        "def mapOption = { | #some(a), f -> #some(f(a))\n" +
        "                  | other, _ -> other }\n" +
        "type Customer = (name: string)\n" +
        "def getCustomer = { name ->\n" +
        "  if true then #some(Customer(name = name))\n" +
        "  else if false then #none\n" +
        "  else #err\n" +
        "}\n" +
        "def main: () -> () = {\n" +
        "  def a = getCustomer(\"foo\") \\mapOption { c: Customer -> \"Mapped: {c.name}\" }\n" +
        "  dbg(a)\n" +
        "  ()\n" +
        "}\n"
    Assert.Equal(Some "a: #some(string) | #err | #none", hoverType source (offsetOf source "a =" 0 0))

[<Fact>]
let ``A record type is written in brackets, as in source`` () =
    let fields = [ "name", TyPrimitive "string"; "age", TyPrimitive "int" ] |> Map.ofList
    Assert.Equal("(age: int, name: string)", Unifier.tyToString (TyRecord fields))
    Assert.Equal("#some(name: string)",Unifier.tyToString (TyTag("some", Some (TyRecord (Map.ofList [ "name", TyPrimitive "string" ])))))

[<Fact>]
let ``The empty union is the bottom type`` () =
    Assert.Equal("undefined", Unifier.tyToString (TyUnion []))

[<Fact>]
let ``Where the two ends of the alphabet meet, each goes round its own part again`` () =
    let var id = TyVar { Id = id; Name = None }
    // Each rest is given back as well as taken, so it is named.
    let open' id = TyFunc(TyUnion [ TyTag("x", None); var id ], TyUnion [ TyTag("x", None); var id ], [])
    // 20 variables and 4 rests take the whole alphabet the first time, so the front keeps α to υ and the back
    // φ to ω; the rests and variables after that go round their own parts again.
    let ty = TyApply("f", [ for id in 1..20 -> var id ] @ [ for id in 21..26 -> open' id ] @ [ var 27; var 28 ])
    Assert.Equal(
        "f(α, β, γ, δ, ε, ζ, η, θ, ι, κ, λ, μ, ν, ξ, ο, π, ρ, σ, τ, υ, "
        + "(#x | ω -> #x | ω), (#x | ψ -> #x | ψ), (#x | χ -> #x | χ), (#x | φ -> #x | φ), "
        + "(#x | ω₁ -> #x | ω₁), (#x | ψ₁ -> #x | ψ₁), α₁, β₁)",
        Unifier.tyToDisplayString Unicode ty)

[<Fact>]
let ``Hover inside a for loop's arm shows the arm's expression, not the loop element`` () =
    let source = "def f = for [\"a\"] | s -> s ++ \"!\""
    let info = (hoverAt source (offsetOf source "\"!\"" 0 1)).Value
    Assert.Equal("string", info.Type)
    Assert.Equal(offsetOf source "\"!\"" 0 0, info.Span.Start)
    Assert.Equal(Some "int", hoverType "def g = for 1..3 | i -> i * 2" (offsetOf "def g = for 1..3 | i -> i * 2" "2" 0 0))

[<Fact>]
let ``Hover inside a match arm shows the innermost expression`` () =
    let source =
        "def describe = { n ->\n" +
        "  match n\n" +
        "  | 0 -> \"zero\"\n" +
        "  | _ -> \"many\"\n" +
        "}\n"
    let info = (hoverAt source (offsetOf source "\"many\"" 0 2)).Value
    Assert.Equal("string", info.Type)
    Assert.Equal(offsetOf source "\"many\"" 0 0, info.Span.Start)

[<Fact>]
let ``A workflow bind is not given the type of the rest of the workflow`` () =
    let source =
        "context AsyncWorkflow = (await: (string, (string -> int)) -> int return: int -> int)\n" +
        "def async: AsyncWorkflow = (await = { x, f -> f(x) }, return = { x -> x })\n" +
        "def result = async {\n" +
        "  def x = await! \"a\"\n" +
        "  return! 1\n" +
        "}"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let bind =
        Hover.expressionsAt (offsetOf source "await!" 0 0) result.Syntax.Value
        |> List.find (fun expr -> match expr.Kind with WorkflowBindExpr _ -> true | _ -> false)
    Assert.False(result.Typed.Value.ExprTypes.ContainsKey bind.Id)
    Assert.Equal(Some "string", hoverType source (offsetOf source "\"a\"" 0 1))

[<Fact>]
let ``Type variables named in an annotation keep their names, written as in the annotation`` () =
    let source = "def first: (t, a) -> t = { x, y -> x }"
    Assert.Equal(Some "t, a -> t", hoverType source (offsetOf source "{" 0 0))
    let mixed =
        "def f: a -> int = { x ->\n" +
        "  def g = { y -> (y, x) }\n" +
        "  1\n" +
        "}\n"
    Assert.Equal(Some "α -> (α, a)", hoverType mixed (offsetOf mixed "{ y" 0 0))
    // A made-up Latin name skips the names an annotation took.
    Assert.Equal(Some "b -> (b, a)", latinHoverType mixed (offsetOf mixed "{ y" 0 0))

[<Fact>]
let ``Hover shows tags with their hash`` () =
    let source = "def found = #some(1)\ndef missing = #notFound"
    Assert.Equal(Some "#some(int)", hoverType source (offsetOf source "#some" 0 1))
    Assert.Equal(Some "#notFound", hoverType source (offsetOf source "#notFound" 0 1))
    let union =
        "def mapOption: (#some(a) | r), (a -> b) -> (#some(b) | r) = { | #some(a), f -> #some(f(a)) | other, _ -> other }\n" +
        "def result = mapOption(#some(1), { n -> \"Mapped: {n}\" })"
    Assert.Equal(
        Some "def mapOption: #some(int), (int -> string) -> #some(string)",
        hoverType union (offsetOf union "mapOption(" 0 0))

[<Fact>]
let ``Printed types have the fewest brackets the grammar allows`` () =
    let a = TyVar { Id = 1; Name = Some "a" }
    let b = TyVar { Id = 2; Name = Some "b" }
    let c = TyVar { Id = 3; Name = Some "c" }
    let union = TyUnion [ TyTag("some", Some a); TyTag("none", None) ]
    let pair x y = TyRecord(Map.ofList [ "0", x; "1", y ])
    // `->` groups to the right: a function input is bracketed, a function result is not.
    Assert.Equal("(a -> b) -> c", Unifier.tyToString (TyFunc(TyFunc(a, b, []), c, [])))
    Assert.Equal("a -> b -> c", Unifier.tyToString (TyFunc(a, TyFunc(b, c, []), [])))
    // `|` binds tighter than `->`: a union is never bracketed next to an arrow.
    Assert.Equal("a -> #some(a) | #none", Unifier.tyToString (TyFunc(a, union, [])))
    Assert.Equal("#some(a) | #none -> a", Unifier.tyToString (TyFunc(union, a, [])))
    // A signature's parameters are bare; a function among them, or anywhere items are separated by commas,
    // has brackets of its own.
    Assert.Equal("int, (a -> b) -> c", Unifier.tyToString (TyFunc(pair (TyPrimitive "int") (TyFunc(a, b, [])), c, [])))
    Assert.Equal("(int, (a -> b))", Unifier.tyToString (pair (TyPrimitive "int") (TyFunc(a, b, []))))
    Assert.Equal("map(string, (a -> b))", Unifier.tyToString (TyApply("map", [ TyPrimitive "string"; TyFunc(a, b, []) ])))
    // Only the signature itself writes parameters bare: a function nested in it, as its input or its result,
    // keeps its brackets, since a bare list can only come before the arrow.
    Assert.Equal("((a, b) -> c) -> (a, b) -> c", Unifier.tyToString (TyFunc(TyFunc(pair a b, c, []), TyFunc(pair a b, c, []), [])))
    Assert.Equal("a -> (a, b) -> c", Unifier.tyToString (TyFunc(a, TyFunc(pair a b, c, []), [])))
    // A lone argument or payload needs no brackets, and a tuple payload's items are the payload's.
    Assert.Equal("list(a -> b)", Unifier.tyToString (TyApply("list", [ TyFunc(a, b, []) ])))
    Assert.Equal("#some(int)", Unifier.tyToString (TyTag("some", Some (TyPrimitive "int"))))
    Assert.Equal("#pair(int, (a -> b))", Unifier.tyToString (TyTag("pair", Some (pair (TyPrimitive "int") (TyFunc(a, b, []))))))
    Assert.Equal("#apply((int, a) -> b)",Unifier.tyToString (TyTag("apply", Some (TyFunc(pair (TyPrimitive "int") a, b, [])))))
    // A lone record argument's fields are in the application's own brackets, as a payload's are; a tuple's are not,
    // as its items would read as more arguments.
    let named = Map.ofList [ "name", a ]
    Assert.Equal("list(name: a)", Unifier.tyToString (TyApply("list", [ TyRecord named ])))
    Assert.Equal("list(name: a, ...r)", Unifier.tyToString (TyApply("list", [ TyOpenRecord(named, { Id = 4; Name = Some "r" }) ])))
    Assert.Equal("list((a, b))", Unifier.tyToString (TyApply("list", [ pair a b ])))
    // The unit type is written `()`.
    Assert.Equal("() -> ()", Unifier.tyToString (TyFunc(TyPrimitive "unit", TyPrimitive "unit", [])))

[<Fact>]
let ``A def annotated with holes shows its inferred type, and each hole what it was inferred to be`` () =
    let source = "def xs: list(_) = [1, 2]\ndef add: (int, _) -> _ = { a, b -> a + b }\n"
    Assert.Equal(Some "def xs: list(int)", hoverType source (offsetOf source "xs" 0 0))
    Assert.Equal(Some "int", hoverType source (offsetOf source "_" 0 0))
    Assert.Equal(Some "def add: int, int -> int", hoverType source (offsetOf source "add" 0 0))
    Assert.Equal(Some "int", hoverType source (offsetOf source "_" 2 0))

[<Fact>]
let ``Hover on a top-level def's name shows it as its declaration writes it, contexts and all`` () =
    let source = """
def takeOrder: [Console] string, list(string) -> () = { customer, order -> println(customer) }
def greet = { -> println("hello") }
@entry
def main = {
  takeOrder("pat", [])
  greet()
}
"""
    let source = source.ReplaceLineEndings "\n"
    let expected = Some "def takeOrder: [Console] string, list(string) -> ()"
    // At its definition, and where it is called.
    Assert.Equal(expected, hoverType source (offsetOf source "takeOrder" 0 0))
    Assert.Equal(expected, hoverType source (offsetOf source "takeOrder(" 0 0))
    // A def that does not write its type shows the contexts it was inferred to need.
    Assert.Equal(Some "def greet: [Console] () -> ()", hoverType source (offsetOf source "greet" 0 0))

[<Fact>]
let ``Hover on a use of a def with parameterised contexts shows them as it is called`` () =
    let source = """
context Show(a) = (show: a -> string)
type Point = (x: int, y: int)
def describe: [Show(a)] a -> string = { value -> Show(a).show(value) }
def Point.show: Point -> string = { p -> "({p.x}, {p.y})" }
def s = describe(Point(x = 1, y = 2))
def f = describe
"""
    let source = source.ReplaceLineEndings "\n"
    Assert.Equal(Some "def describe: [Show(a)] a -> string", hoverType source (offsetOf source "describe" 0 0))
    Assert.Equal(Some "def describe: [Show(Point)] Point -> string", hoverType source (offsetOf source "describe(" 0 0))

[<Fact>]
let ``Hover on a use of an imported def shows the contexts it requires`` () =
    let shapes = """module shapes
export context Show(a) = (show: a -> string)
export def describe: [Show(a)] a -> string = { value -> Show(a).show(value) }
"""
    let main = """module main
import mod
type Point = (x: int, y: int)
def Point.show: Point -> string = { p -> "({p.x}, {p.y})" }
def s = describe(Point(x = 1, y = 2))
"""
    let main = main.ReplaceLineEndings "\n"
    let result = compileImporting (shapes.ReplaceLineEndings "\n") main
    Assert.Empty(result.Diagnostics)
    let hovered = Hover.typeAt Unicode result (offsetOf main "describe(" 0 0) |> Option.map _.Type
    Assert.Equal(Some "def describe: [Show(Point)] Point -> string", hovered)

[<Fact>]
let ``Hover on a use of a standard library def shows the contexts it requires`` () =
    let source = """
@entry
def main = { println("hello") }
"""
    let source = source.ReplaceLineEndings "\n"
    Assert.Equal(Some "def println: [Console, ToString(string)] string -> ()", hoverType source (offsetOf source "println" 0 0))

[<Fact>]
let ``An imported def's contexts name their variables together with its type`` () =
    let source = "def total = { xs -> xs \\list.sum }\n"
    Assert.Equal(Some "def list.sum: [Add(α)] list(α) -> α", hoverType source (offsetOf source "list.sum" 0 0))

[<Fact>]
let ``Hover on a local def shows the contexts it writes, at its declaration and as it is called`` () =
    let source = """
context Show(a) = (show: a -> string)
def int.show: int -> string = { n -> "{n}" }
def local = {
  def inner: [Show(a)] a -> string = { v -> Show(a).show(v) }
  inner(1)
}
"""
    let source = source.ReplaceLineEndings "\n"
    Assert.Equal(Some "inner: [Show(a)] a -> string", hoverType source (offsetOf source "inner" 0 0))
    Assert.Equal(Some "inner: [Show(int)] int -> string", hoverType source (offsetOf source "inner(" 0 0))

[<Fact>]
let ``Hover on a local def, a parameter or a pattern variable shows its name and type`` () =
    let source = """
def answer = {
  def half = 21
  half * 2
}
def bump = { n -> n + 1 }
def size = { o ->
  match o
  | #some(text) -> text ++ "!"
  | _ -> ""
}
"""
    let source = source.ReplaceLineEndings "\n"
    // Each at its declaration and at a use.
    Assert.Equal(Some "half: int", hoverType source (offsetOf source "half" 0 0))
    Assert.Equal(Some "half: int", hoverType source (offsetOf source "half *" 0 0))
    Assert.Equal(Some "n: int", hoverType source (offsetOf source "n ->" 0 0))
    Assert.Equal(Some "n: int", hoverType source (offsetOf source "n +" 0 0))
    Assert.Equal(Some "text: string", hoverType source (offsetOf source "text)" 0 0))
    Assert.Equal(Some "text: string", hoverType source (offsetOf source "text ++" 0 0))

[<Fact>]
let ``Hover on an expression that is not a name shows just its type`` () =
    let source = "def bump = { n -> n + 1 }\ndef two = bump(1) * 2\n"
    Assert.Equal(Some "int", hoverType source (offsetOf source "1)" 0 0))
    Assert.Equal(Some "int", hoverType source (offsetOf source "+" 0 0))
    Assert.Equal(Some "int -> int", hoverType source (offsetOf source "{ n" 0 0))
    Assert.Equal(Some "int", hoverType source (offsetOf source "(1)" 0 0))
