module Nanyx.Compiler.Tests.Features.StructuralInstances

open Xunit
open NyxCompiler
open Parser.Program

/// `source` compiled, with no diagnostics.
let private compileClean (source: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    result.Typed.Value

/// The one diagnostic `source` has, which must be a missing context: its message, and the text it is reported at.
let private missingContext (source: string) =
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
    for value, structures in [ "#some(1)", 1; "(1, \"a\")", 1; "(a = 1)", 1; "[1, 2]", 1; "[#pair(1, (x = [2.5]))]", 4 ] do
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
    let typed = compileClean "def show = { value -> \"{#some(value)}\" }\ndef s = show(3) ++ show(\"x\")\n"
    match typed.InferredContextTypes |> Map.values |> List.ofSeq with
    | [ [ TyApply("ToString", [ TyVar _ ]) ] ] -> ()
    | other -> failwith $"Unexpected inferred contexts: %A{other}"

[<Fact>]
let ``A def that requires ToString of a variable can interpolate a structure that holds it`` () =
    compileClean "def show: [ToString(a)] a -> string = { value -> \"{[(value, 1)]}\" }\ndef s = show(3)\n" |> ignore

[<Fact>]
let ``A def that writes its type must require ToString of a variable in a structure it interpolates`` () =
    let message, at = missingContext "def show: a -> string = { value -> \"{#some(value)}\" }\n"
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
          "type $Money = private int\ndef m = $Money(5)\ndef s = \"{[m]}\"\n",
          "'$Money' cannot be interpolated because it has no toString: attach '$Money.toString', or load an instance of `ToString($Money)` with `use`",
          "[m]" ]
    for source, expectedMessage, expectedAt in cases do
        let message, at = missingContext source
        Assert.Equal(expectedMessage, message)
        Assert.Equal(expectedAt, at)

[<Fact>]
let ``A function cannot yet be passed the ToString derived for a type with variables in it`` () =
    let message, at =
        missingContext (
            "def show: [ToString(a)] a -> string = { x -> \"{x}\" }\n" +
            "def wrap: [ToString(a)] a -> string = { x -> show(#some(x)) }\n")
    Assert.Equal(
        "'show' requires the context 'ToString(#some(a))', which the compiler derives from the parts of the type, but it cannot yet pass a function the one for a type with type variables in it: turn the value into a string with a hole first, as in \"{value}\"",
        message)
    Assert.Equal("show", at)

[<Fact>]
let ``A function that requires ToString is passed the one derived for a type with nothing left open`` () =
    let typed = compileClean "def show: [ToString(a)] a -> string = { x -> \"{x}\" }\ndef s = show(#some(1))\n"
    Assert.Equal(1, (membersNamed "toString" typed).Length)

[<Fact>]
let ``A structure with no part that has an == of its own is compared by the compiler`` () =
    let typed = compileClean "def b = #some(1) == #some(2)\ndef c = [(1, \"a\")] == [(1, \"b\")]\n"
    Assert.Empty(derivedDefs "__equals_" typed)

[<Fact>]
let ``A structure with a part that has an == attached is compared by a derived ==`` () =
    let typed =
        compileClean (
            "type $Email = string\n" +
            "def $Email.`==`: $Email, $Email -> bool = { a, b -> true }\n" +
            "def b = [$Email(\"a\")] == [$Email(\"b\")]\n" +
            "def c = [$Email(\"a\")] != []\n")
    Assert.Equal(1, (derivedDefs "__equals_" typed).Length)

[<Fact>]
let ``A function that requires Eq is passed the one derived for a structure whose part has an == attached`` () =
    let typed =
        compileClean (
            "type $Email = string\n" +
            "def $Email.`==`: $Email, $Email -> bool = { a, b -> true }\n" +
            "def same: [Eq(a)] a, a -> bool = { x, y -> x == y }\n" +
            "def b = same(#some($Email(\"a\")), #some($Email(\"b\")))\n" +
            "def c = same(#some(1), #some(2))\n")
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
