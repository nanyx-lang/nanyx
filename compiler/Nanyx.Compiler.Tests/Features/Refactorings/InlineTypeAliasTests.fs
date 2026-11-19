module Nanyx.Compiler.Tests.Features.Refactorings.InlineTypeAlias

open Xunit
open NyxCompiler
open NyxCompiler.Refactorings
open Nanyx.Formatter
open Nanyx.Compiler.Tests.TestSupport

// Inline a type alias: its uses become the type it stands for.

/// The offers at `$$` in `marked`, and the source without the mark.
let private offersAt (marked: string) =
    let source, offset = unmarked marked
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    source, InlineTypeAlias.at Formatter.Options.defaults "" source result offset Compiler.compile |> List.map Offer.decided

/// The text after the offer titled `title` at `$$`, checked to compile and to be formatted.
let private rewrittenBy (title: string) (marked: string) =
    let source, offers = offersAt marked
    match offers |> List.tryPick (function Available refactoring when refactoring.Title = title -> Some refactoring | _ -> None) with
    | Some refactoring ->
        refactoredText source refactoring
    | None -> failwithf "expected %s, got %A" title offers

/// The reason the offer titled `title` at `$$` is refused.
let private refusal (title: string) (marked: string) =
    match snd (offersAt marked) |> List.tryPick (function Refused(t, RefactoringKind.Inline, reason) when t = title -> Some reason | _ -> None) with
    | Some reason -> reason
    | None -> failwithf "expected %s to be refused, got %A" title (snd (offersAt marked))

[<Fact>]
let ``Every use becomes the type, and the alias and its doc comment go`` () =
    Assert.Equal(
        normalized
            """def keep: list(#some(int) | #missing), int -> list(#some(int) | #missing) = { xs, n -> xs }
""",
        rewrittenBy
            "Inline `Result`"
            """-- What a lookup finds.
type Result$$ = list(#some(int) | #missing)

def keep: Result, int -> Result = { xs, n -> xs }
""")

[<Fact>]
let ``On a use, the alias is inlined everywhere or at that use`` () =
    let marked =
        """type Ints = list(int)

def keep: Ints$$ -> Ints = { xs -> xs }
"""
    Assert.Equal("def keep: list(int) -> list(int) = { xs -> xs }\n", rewrittenBy "Inline `Ints`" marked)
    Assert.Equal(
        normalized
            """type Ints = list(int)

def keep: list(int) -> Ints = { xs -> xs }
""",
        rewrittenBy "Inline this use" marked)

[<Fact>]
let ``Parameters are replaced by the use's arguments`` () =
    Assert.StartsWith(
        // A function's parameters are a tuple, so the formatter leaves the brackets out.
        "def swap: int, string -> (string, int) = ",
        rewrittenBy
            "Inline `Pair`"
            """type Pair$$(a, b) = (a, b)

def swap: Pair(int, string) -> Pair(string, int) = { p -> (p.1, p.0) }
""")

[<Fact>]
let ``A function is bracketed where it is a parameter`` () =
    Assert.StartsWith(
        "def apply: (int -> int), int -> int = ",
        rewrittenBy
            "Inline `Step`"
            """type Step$$ = int -> int

def apply: Step, int -> int = { f, n -> f(n) }
""")

[<Fact>]
let ``A nominal type is refused`` () =
    Assert.Equal(
        "`Email` is a nominal type, not an alias of its shape",
        refusal "Inline `Email`" "type Email$$ = protected string\n\ndef send: Email -> int = { e -> 1 }\n")

[<Fact>]
let ``An alias that names itself is refused`` () =
    Assert.Equal(
        "`Tree` names itself",
        refusal "Inline `Tree`" "type Tree$$ = #leaf | #node(list(Tree))\n\ndef size: Tree -> int = { t -> 1 }\n")

[<Fact>]
let ``An exported alias is not inlined everywhere, but a use is`` () =
    let marked =
        """export type Ints = list(int)

def keep: Ints$$ -> Ints = { xs -> xs }
"""
    Assert.Equal("`Ints` is exported, so another module may use it", refusal "Inline `Ints`" marked)
    Assert.StartsWith("export type Ints = list(int)\n\ndef keep: list(int) -> Ints", rewrittenBy "Inline this use" marked)

[<Fact>]
let ``Nothing is offered on a name that is not an alias`` () =
    Assert.Empty(snd (offersAt "def keep: list(int$$) -> list(int) = { xs -> xs }\n"))
