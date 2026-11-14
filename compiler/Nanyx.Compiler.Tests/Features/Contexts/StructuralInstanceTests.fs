module Nanyx.Compiler.Tests.Features.StructuralInstances

open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

/// `source` compiled, with no diagnostics, whatever line endings it was checked out with.
let private compileClean (source: string) =
    let result = Compiler.compile (source.ReplaceLineEndings "\n")
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    result.Typed.Value

/// The one diagnostic `source` has, which must be a missing context: its message, and the text it is reported at.
let private missingContext (source: string) =
    let source = source.ReplaceLineEndings "\n"
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
        let span = diagnostic.Span.Value
        diagnostic.Message, source.Substring(span.Start, span.End - span.Start)
    | other -> failwith $"Expected one diagnostic, got %A{other}"

/// The definitions the instances of `typed` name as the member `memberName`, each once.
let private membersNamed (memberName: string) (typed: TypedModule) =
    typed.ContextInstances
    |> Map.toList
    |> List.collect snd
    |> List.choose (function
        | Some (InstanceSynthesized [ name, definition ]) when name = memberName -> Some definition
        | _ -> None)
    |> List.distinct

/// The definitions of `typed` the compiler derived, whose names start with `prefix`.
let private derivedDefs (prefix: string) (typed: TypedModule) =
    typed.Module
    |> List.choose (function
        | Kind (Def (ValueDef(_, name, _, _))) when name.StartsWith prefix -> Some name
        | _ -> None)

[<Fact>]
let ``A hole of a structure with nothing left open uses a toString derived from its parts`` () =
    for value, structures in [ "#some(1)", 1; """(1, "a")""", 1; "(a = 1)", 1; "[1, 2]", 1; "[#pair(1, (x = [2.5]))]", 4 ] do
        let typed = compileClean $"def s = \"{{{value}}}\"\n"
        let derived = derivedDefs "__toString_" typed
        Assert.Equal(structures, derived.Length)
        Assert.Equal<string list>(List.sort derived, List.sort (membersNamed "toString" typed))

[<Fact>]
let ``Two holes of the same type share one derived toString`` () =
    let typed = compileClean "def s = \"{#some(1)} {#some(2)}\"\ndef t = \"{#some(3)}\"\n"
    Assert.Equal(1, (derivedDefs "__toString_" typed).Length)

[<Fact>]
let ``A def that does not write its type requires ToString of a variable in a structure it interpolates`` () =
    let typed = compileClean (withFinalNewline """def show = { value -> "{#some(value)}" }
def s = show(3) ++ show("x")""")
    match typed.InferredContextTypes |> Map.values |> List.ofSeq with
    | [ [ TyApply("ToString", [ TyVar _ ]) ] ] -> ()
    | other -> failwith $"Unexpected inferred contexts: %A{other}"

[<Fact>]
let ``A def that requires ToString of a variable can interpolate a structure that holds it`` () =
    compileClean "def show: [ToString(a)] a -> string = { value -> \"{[(value, 1)]}\" }\ndef s = show(3)\n" |> ignore

[<Fact>]
let ``A def that writes its type must require ToString of a variable in a structure it interpolates`` () =
    let message, at = missingContext (withFinalNewline """def show: a -> string = { value -> "{#some(value)}" }""")
    Assert.Equal(
        "Interpolating a value of type 'a' requires the context 'ToString(a)', which is not available here: add [ToString(a)] to the type of 'show'",
        message)
    Assert.Equal("#some(value)", at)

[<Fact>]
let ``A hole of a structure with a part that has no ToString is an error at the hole`` () =
    let cases =
        [ "def f = { n -> n }\ndef s = \"{#some(f)}\"\n",
          "'a -> a' cannot be interpolated: it is a function, and functions have no toString",
          "#some(f)"
          "def f: int -> int = { n -> n }\ndef s = \"{(1, [f])}\"\n",
          "'int -> int' cannot be interpolated: it is a function, and functions have no toString",
          "(1, [f])"
          "type Money = private int\ndef m = Money(5)\ndef s = \"{[m]}\"\n",
          "'Money' cannot be interpolated because it has no toString: attach 'Money.toString', or load an instance of `ToString(Money)` with `use`",
          "[m]" ]
    for source, expectedMessage, expectedAt in cases do
        let message, at = missingContext source
        Assert.Equal(expectedMessage, message)
        Assert.Equal(expectedAt, at)

/// The derived instances of `typed` passed to a function, each with the instances its definition is passed.
let private derivedInstances (typed: TypedModule) =
    typed.ContextInstances
    |> Map.values
    |> Seq.concat
    |> Seq.choose (function
        | Some (InstanceDerived(memberName, definition, at)) -> Some (memberName, definition, typed.ContextInstances.[at])
        | _ -> None)
    |> List.ofSeq

/// Whether `instance` is the one the function around a call requires of its variable `a`, `ctxName(a)`.
let private isRequired (ctxName: string) (instance: ContextInstance option) =
    match instance with
    | Some (InstanceInScope(Kind (TypeApply(name, [ Kind (TypeVar "a") ])), _)) -> name = ctxName
    | _ -> false

[<Fact>]
let ``A function is passed the ToString derived for a type with variables in it, built from the instance in scope`` () =
    let typed =
        compileClean """def show: [ToString(a)] a -> string = { x -> "{x}" }
def wrap: [ToString(a)] a -> string = { x -> show(#some(x)) }
def s = wrap(1)
"""
    match derivedInstances typed with
    | [ "toString", definition, [ instance ] ] ->
        Assert.StartsWith("__toString_", definition)
        Assert.True(isRequired "ToString" instance, $"Unexpected instance: %A{instance}")
    | other -> failwith $"Expected one derived ToString, got %A{other}"

[<Fact>]
let ``A def that does not write its type requires ToString of a variable in a structure it passes to a function`` () =
    let typed =
        compileClean """def show: [ToString(a)] a -> string = { x -> "{x}" }
def wrap = { x -> show((x, 1)) }
def s = wrap(1)
"""
    match typed.InferredContextTypes |> Map.values |> List.ofSeq with
    | [ [ TyApply("ToString", [ TyVar _ ]) ] ] -> ()
    | other -> failwith $"Unexpected inferred contexts: %A{other}"

[<Fact>]
let ``A function that passes a structure of a variable it has no ToString of says which to require`` () =
    let message, at =
        missingContext """def show: [ToString(a)] a -> string = { x -> "{x}" }
def wrap: a -> string = { x -> show(#some(x)) }
"""
    Assert.Equal(
        "'show' requires the context 'ToString(#some(a))', which is written through 'ToString(a)', and that is not available here: add [ToString(a)] to the type of 'wrap'",
        message)
    Assert.Equal("show", at)

[<Fact>]
let ``A function that requires Eq is passed the one derived from the Eq of a variable in scope`` () =
    let typed =
        compileClean """def same: [Eq(a)] a, a -> bool = { x, y -> x == y }
def listed: [Eq(a)] a, a -> bool = { x, y -> same([x], [y]) }
def b = listed(1, 2)
"""
    match derivedInstances typed with
    | [ "==", definition, [ instance ] ] ->
        Assert.StartsWith("__equals_", definition)
        Assert.True(isRequired "Eq" instance, $"Unexpected instance: %A{instance}")
    | other -> failwith $"Expected one derived ==, got %A{other}"

[<Fact>]
let ``A hole of a structure writes a part through a ToString loaded with use in its block`` () =
    let typed =
        compileClean """def pounds: ToString(int) = (toString = { p -> "£{p}" })
def s = {
  use pounds
  "{[1, 2]}"
}
"""
    let loaded =
        typed.ContextInstances
        |> Map.values
        |> Seq.concat
        |> Seq.exists (function
            | Some (InstanceInScope(Kind (TypeApply("ToString", [ Kind (TypeName "int") ])), _)) -> true
            | _ -> false)
    Assert.True(loaded, "The derived toString is not passed the ToString(int) in scope")

[<Fact>]
let ``A local function can interpolate a structure of a variable it is generic in`` () =
    compileClean """def outer = { y ->
  def wrap = { x -> "{#some(x)}" }
  wrap(y) ++ wrap("s")
}
def s = outer(1)
"""
    |> ignore

[<Fact>]
let ``An empty list can be interpolated`` () =
    compileClean "def s = \"{[]}\"\n" |> ignore

[<Fact>]
let ``A union open to tags the caller chooses has no derived ToString, and the error says what to write`` () =
    let message, at = missingContext (withFinalNewline """def f: [ToString(a)] (#x(a) | r) -> string = { v -> "{v}" }""")
    Assert.Equal(
        "'#x(a) | r' cannot be interpolated: the caller of 'f' chooses the other tags its union may hold, and a toString is derived only for a union whose tags are all known: name every tag in the type, or match on the tags it names",
        message)
    Assert.Equal("v", at)

[<Fact>]
let ``Each hole of a structure with a part that has no ToString is an error`` () =
    let source = """def g = { n -> n }
def s = "{#some(g)}"
def t = "{#some(g)}"
"""
    let source = source.ReplaceLineEndings "\n"
    let diagnostics = (Compiler.compile source).Diagnostics
    Assert.Equal<string list>(
        [ "'a -> a' cannot be interpolated: it is a function, and functions have no toString"
          "'a -> a' cannot be interpolated: it is a function, and functions have no toString" ],
        diagnostics |> List.map _.Message)
    Assert.Equal<int list>([ 2; 3 ], diagnostics |> List.map (fun diagnostic -> source.Substring(0, diagnostic.Span.Value.Start).Split('\n').Length))

[<Fact>]
let ``A function that requires ToString is passed the one derived for a type with nothing left open`` () =
    let typed = compileClean (withFinalNewline """def show: [ToString(a)] a -> string = { x -> "{x}" }
def s = show(#some(1))""")
    Assert.Equal(1, (membersNamed "toString" typed).Length)

[<Fact>]
let ``A structure with no part that has an == of its own is compared by the compiler`` () =
    let typed = compileClean (withFinalNewline """def b = #some(1) == #some(2)
def c = [(1, "a")] == [(1, "b")]""")
    Assert.Empty(derivedDefs "__equals_" typed)

[<Fact>]
let ``A structure with a part that has an == attached is compared by a derived ==`` () =
    let typed =
        compileClean (
            withFinalNewline """type Email = protected string
def Email.`==`: Email, Email -> bool = { a, b -> true }
def b = #some(Email("a")) == #some(Email("b"))
def c = #some(Email("a")) != #some(Email("c"))""")
    Assert.Equal(1, (derivedDefs "__equals_" typed).Length)

[<Fact>]
let ``A list whose elements have an == attached is compared through it without a derived ==`` () =
    let typed =
        compileClean (
            withFinalNewline """type Email = protected string
def Email.`==`: Email, Email -> bool = { a, b -> true }
def b = [Email("a")] == [Email("b")]""")
    Assert.Empty(derivedDefs "__equals_" typed)

[<Fact>]
let ``A function that requires Eq is passed the one derived for a structure whose part has an == attached`` () =
    let typed =
        compileClean (
            withFinalNewline """type Email = protected string
def Email.`==`: Email, Email -> bool = { a, b -> true }
def same: [Eq(a)] a, a -> bool = { x, y -> x == y }
def b = same(#some(Email("a")), #some(Email("b")))
def c = same(#some(1), #some(2))""")
    match membersNamed "==" typed with
    | [ derived; builtIn ] ->
        Assert.StartsWith("__equals_", derived)
        Assert.Equal("__equals", builtIn)
    | other -> failwith $"Expected a derived == and the compiler's own, got %A{other}"

[<Fact>]
let ``A def that writes its type must require Eq of a variable in a structure it compares`` () =
    let message, at = missingContext "def same: a, a -> bool = { x, y -> #some(x) == #some(y) }\n"
    Assert.Equal(
        "'same' compares values of type 'a' with ==, so its type must require Eq(a): add [Eq(a)] to the type of 'same'",
        message)
    Assert.Equal("#some(x) == #some(y)", at)
