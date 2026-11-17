module Nanyx.Compiler.Tests.Features.Refactorings.WriteHoleTypes

open Xunit
open NyxCompiler
open NyxCompiler.Refactorings
open Nanyx.Formatter
open Nanyx.Compiler.Tests.TestSupport

// Write the inferred types of the `_`s: each `_` in an annotation is replaced by what it was inferred to be.

/// The offers at `$$` in `marked`, and the source without the mark.
let private offersAt (marked: string) =
    let marked = normalized marked
    let offset = marked.IndexOf "$$"
    let source = marked.Remove(offset, 2)
    let result = Compiler.compile source
    source, result, WriteHoleTypes.at Formatter.Options.defaults "" source result offset Compiler.compile

/// The text after the offer titled `title` at `$$`, checked to compile and to be formatted.
let private rewrittenBy (title: string) (marked: string) =
    let source, before, offers = offersAt marked
    Assert.True(before.Diagnostics.IsEmpty, sprintf "%A" before.Diagnostics)
    match offers |> List.tryPick (function Available refactoring when refactoring.Title = title -> Some refactoring | _ -> None) with
    | Some refactoring ->
        let after = applyEdits source refactoring.Edits
        let result = Compiler.compile after
        Assert.True(result.Diagnostics.IsEmpty, sprintf "%s\n%A" after result.Diagnostics)
        match FormatCommand.formatText Formatter.Options.defaults "" after with
        | FormatCommand.Formatting.Formatted formatted -> Assert.Equal(formatted, after)
        | other -> failwithf "%A" other
        after
    | None -> failwithf "expected %s, got %A" title offers

let private one = rewrittenBy "Write the inferred type of this `_`"

let private every = rewrittenBy "Write the inferred types of every `_`"

[<Fact>]
let ``A _ is replaced by what it was inferred to be`` () =
    Assert.Equal("def xs: list(int) = [1, 2]\n", one "def xs: list($$_) = [1, 2]\n")

[<Fact>]
let ``Only the _ the offer is on is written, or every one`` () =
    Assert.StartsWith("def add: int, int -> _ = ", one "def add: int, $$_ -> _ = { a, b -> a + b }\n")
    Assert.StartsWith("def add: int, int -> int = ", every "def add: int, $$_ -> _ = { a, b -> a + b }\n")

[<Fact>]
let ``On the def's name every _ is written`` () =
    let _, _, offers = offersAt "def add$$: int, _ -> _ = { a, b -> a + b }\n"
    Assert.Equal<string list>([ "Write the inferred types of every `_`" ], offers |> List.map (function Available r -> r.Title | Refused(title, _, _) -> title))
    Assert.StartsWith("def add: int, int -> int = ", every "def add$$: int, _ -> _ = { a, b -> a + b }\n")

[<Fact>]
let ``A function in a parameter is bracketed`` () =
    // The function's context variable is not one of the def's contexts, so it is left out, as the `_` did not need it.
    Assert.StartsWith(
        "def reduce: [Default(a)] list(a), (a, a -> a) -> a = ",
        one "def reduce: [Default(a)] list(a), $$_ -> a = { xs, f -> xs \\fold(default(), f) }\n")

[<Fact>]
let ``A type variable the annotation names keeps its name`` () =
    Assert.Equal("def keep: list(a) -> list(a) = { xs -> xs }\n", one "def keep: list(a) -> $$_ = { xs -> xs }\n")

[<Fact>]
let ``A new type variable takes the next free letter`` () =
    Assert.Equal("def pick: a, b -> a = { x, y -> x }\n", one "def pick: a, $$_ -> a = { x, y -> x }\n")

[<Fact>]
let ``The contexts the annotation writes are kept, and the _s take their variables`` () =
    // The annotation must already list every context the def requires (NYX0205), so none is added.
    Assert.StartsWith(
        "def same: [Eq(a)] a, a -> bool = ",
        every "def same$$: [Eq(a)] _, a -> bool = { x, y -> x == y }\n")

[<Fact>]
let ``A _ in a local def's annotation is written`` () =
    Assert.Contains(
        "  def xs: list(int) = [1]\n",
        one
            """def f = { () ->
  def xs: list($$_) = [1]
  xs
}
""")

[<Fact>]
let ``A _ in a lambda parameter's annotation is written`` () =
    Assert.Equal("def toInt = { (b: bool) -> if b then 1 else 0 }\n", one "def toInt = { b: $$_ -> if b then 1 else 0 }\n")

[<Fact>]
let ``A _ that failed to check is refused`` () =
    match offersAt "def xs: list($$_) = [missing]\n" with
    | _, _, [ Refused(_, RefactoringKind.Rewrite, reason) ] -> Assert.Equal("It has a part that failed to check", reason)
    | _, _, offers -> failwithf "expected a refusal, got %A" offers

[<Fact>]
let ``Nothing is offered on a def that writes no _`` () =
    let _, _, offers = offersAt "def add$$: int, int -> int = { a, b -> a + b }\n"
    Assert.Empty(offers)
