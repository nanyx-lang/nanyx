module Nanyx.Compiler.Tests.Features.Refactorings.Interpolation

open Xunit
open NyxCompiler
open NyxCompiler.Refactorings
open Nanyx.Formatter
open Nanyx.Compiler.Tests.TestSupport

// Turn a `++` chain into an interpolated string, and back.

/// The offers at `$$` in `marked`, and the source without the mark.
let private offersAt (marked: string) =
    let marked = normalized marked
    let offset = marked.IndexOf "$$"
    let source = marked.Remove(offset, 2)
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    source, Interpolation.at Formatter.Options.defaults "" source result offset Compiler.compile |> List.map Offer.decided

/// The text after the one offer at `$$`, checked to compile and to be formatted.
let private converted (marked: string) =
    match offersAt marked with
    | source, [ Available refactoring ] ->
        refactoredText source refactoring
    | _, offers -> failwithf "expected one offer, got %A" offers

/// The reason the one offer at `$$` is refused.
let private refusal (marked: string) =
    match offersAt marked with
    | _, [ Refused(_, _, reason) ] -> reason
    | _, offers -> failwithf "expected one refusal, got %A" offers

[<Fact>]
let ``A chain becomes an interpolated string`` () =
    Assert.Equal(
        "def greet: string, int -> string = { name, age ->\n  \"Hello, {name}! You are {age}.\"\n}\n",
        converted "def greet: string, int -> string = { name, age -> \"Hello, \" ++ name ++ \"! You are \" ++ age \\toString $$++ \".\" }\n")

[<Fact>]
let ``A call of toString becomes a hole on its argument`` () =
    Assert.Equal(
        "def show = { (n: int) -> \"n is {n}, and {n + 1}\" }\n",
        converted "def show = { (n: int) -> \"n is \" $$++ n \\toString ++ \", and \" ++ int.toString(n + 1) }\n")

[<Fact>]
let ``A string's escapes are kept`` () =
    Assert.Equal(
        "def quoted = { (s: string) -> \"\\\"{s}\\\"\\n\" }\n",
        converted "def quoted = { (s: string) -> \"\\\"\" $$++ s ++ \"\\\"\\n\" }\n")

[<Fact>]
let ``An interpolated string in the chain is merged in`` () =
    Assert.Equal(
        "def both: string, string -> string = { a, b ->\n  \"{a} and {b}\"\n}\n",
        converted "def both: string, string -> string = { a, b -> \"{a} and\" $$++ \" {b}\" }\n")

[<Fact>]
let ``A raw string is refused`` () =
    Assert.Equal(
        "It has a raw (single-quoted) string, which can't be interpolated",
        refusal "def path = { (s: string) -> 'C:\\' $$++ s }\n")

[<Fact>]
let ``A string of several lines is refused`` () =
    Assert.Equal("It has a string of several lines", refusal "def text = { (s: string) -> \"one\ntwo \" $$++ s }\n")

[<Fact>]
let ``A list's ++ is not offered`` () =
    Assert.Empty(snd (offersAt "def both = { (a: list(int)) -> a $$++ [1] }\n"))

[<Fact>]
let ``An interpolated string becomes a chain`` () =
    Assert.Equal(
        "def greet: string, int -> string = { name, age ->\n  \"Hello, \" ++ name ++ \"! You are \" ++ age \\toString ++ \".\"\n}\n",
        converted "def greet: string, int -> string = { name, age -> \"Hello, {name}! You$$ are {age}.\" }\n")

[<Fact>]
let ``A hole of an operator is bracketed`` () =
    Assert.Equal(
        "def next = { (n: int) -> \"next: \" ++ (n + 1) \\toString }\n",
        converted "def next = { (n: int) -> \"next: {$$n + 1}\" }\n")

[<Fact>]
let ``Elsewhere nothing is offered`` () =
    Assert.Empty(snd (offersAt "def greet = { (name: string) -> $$name }\n"))
