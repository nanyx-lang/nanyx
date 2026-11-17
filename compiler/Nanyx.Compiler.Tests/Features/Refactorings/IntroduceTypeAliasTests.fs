module Nanyx.Compiler.Tests.Features.Refactorings.IntroduceTypeAlias

open Xunit
open NyxCompiler
open NyxCompiler.Refactorings
open Nanyx.Formatter
open Nanyx.Compiler.Tests.TestSupport

// Introduce type alias: the selected type becomes a `type` definition before the item that holds it.

/// The offers for the selection between `[[` and `]]` in `marked`, and the source without the marks.
let private offersAt (marked: string) =
    let marked = normalized marked
    let start = marked.IndexOf "[["
    let finish = marked.IndexOf "]]" - 2
    let source = marked.Replace("[[", "").Replace("]]", "")
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let selection : Parser.Program.SourceSpan = { File = ""; Start = start; End = finish; StartLine = 0; StartColumn = 0 }
    source, IntroduceTypeAlias.at Formatter.Options.defaults "" source result selection Compiler.compile |> List.map Offer.decided

/// The text after the offer titled starting `title` at the selection, checked to compile and to be formatted.
let private rewrittenBy (title: string) (marked: string) =
    match offersAt marked with
    | source, offers ->
        match offers |> List.tryPick (function Available refactoring when refactoring.Title.StartsWith title -> Some refactoring | _ -> None) with
        | Some refactoring ->
            let after = applyEdits source refactoring.Edits
            let result = Compiler.compile after
            Assert.True(result.Diagnostics.IsEmpty, sprintf "%s\n%A" after result.Diagnostics)
            match FormatCommand.formatText Formatter.Options.defaults "" after with
            | FormatCommand.Formatting.Formatted formatted -> Assert.Equal(formatted, after)
            | other -> failwithf "%A" other
            // The rename starts on the alias's name.
            Assert.Equal(Some "Extracted", refactoring.Rename |> Option.map (fun at -> after.Substring(at, "Extracted".Length)))
            after
        | None -> failwithf "expected %s, got %A" title offers

let private rewritten (marked: string) = rewrittenBy "Introduce type alias `Extracted`" marked

/// The reason the refactoring is refused at the selection.
let private refusal (marked: string) =
    match snd (offersAt marked) with
    | [ Refused(_, RefactoringKind.Extract, reason) ] -> reason
    | offers -> failwithf "expected a refusal, got %A" offers

[<Fact>]
let ``The selected type becomes an alias before the def`` () =
    Assert.Equal(
        normalized
            """type Extracted = list(#some(int) | #missing)

def keep: Extracted, int -> list(#some(int) | #missing) = { xs, n -> xs }
""",
        rewritten
            """def keep: [[list(#some(int) | #missing)]], int -> list(#some(int) | #missing) = { xs, n -> xs }
""")

[<Fact>]
let ``Every occurrence written the same can be replaced`` () =
    Assert.Equal(
        normalized
            """type Extracted = list(#some(int) | #missing)

-- Keeps them.
def keep: Extracted, int -> Extracted = { xs, n -> xs }

def drop: Extracted -> int = { xs -> 0 }
""",
        rewrittenBy
            "Introduce type alias `Extracted` for all 3 occurrences"
            """-- Keeps them.
def keep: list(#some(int) | #missing), int -> list(#some(int) | #missing) = { xs, n -> xs }

def drop: [[list(#some(int) | #missing)]] -> int = { xs -> 0 }
""")

[<Fact>]
let ``Type variables become the alias's parameters`` () =
    Assert.Equal(
        normalized
            """type Extracted(a, b) = list((a, b))

def pairs: Extracted(a, b) -> int = { xs -> 0 }
""",
        rewritten
            """def pairs: [[list((a, b))]] -> int = { xs -> 0 }
""")

[<Fact>]
let ``Occurrences that differ only by their variables' names are the same`` () =
    Assert.Equal(
        normalized
            """type Extracted(a) = list(a)

def firstOf: Extracted(a) -> Extracted(a) = { xs -> xs }

def lastOf: Extracted(b) -> Extracted(b) = { ys -> ys }
""",
        rewrittenBy
            "Introduce type alias `Extracted` for all 4 occurrences"
            """def firstOf: [[list(a)]] -> list(a) = { xs -> xs }

def lastOf: list(b) -> list(b) = { ys -> ys }
""")

[<Fact>]
let ``A type in a lambda parameter's annotation can be made an alias`` () =
    Assert.Contains(
        "def total = { (xs: Extracted) -> xs \\map { x -> x + 1 } }",
        rewritten
            """def total = { (xs: [[list(int)]]) -> xs \map { x -> x + 1 } }
""")

[<Fact>]
let ``An alias an exported signature uses is exported`` () =
    Assert.StartsWith(
        "export type Extracted = list(int)\n",
        rewritten
            """export def total: [[list(int)]] -> list(int) = { xs -> xs \map { x -> x + 1 } }
""")

[<Fact>]
let ``A name already taken is numbered`` () =
    Assert.Contains(
        "type Extracted2 = list(int)",
        snd (
            offersAt
                """type Extracted = int

def total: [[list(int)]] -> list(Extracted) = { xs -> xs }
""")
        |> List.pick (function Available refactoring -> Some refactoring.Edits.Head.NewText | _ -> None))

[<Fact>]
let ``A type variable alone is refused`` () =
    Assert.Equal("A type variable alone is already a name", refusal "def same: [[a]] -> a = { x -> x }\n")

[<Fact>]
let ``A built-in type's name alone is refused`` () =
    Assert.Equal("A built-in type's name alone is already a name", refusal "def same: [[int]] -> int = { x -> x }\n")

[<Fact>]
let ``A type in a list of contexts is refused`` () =
    Assert.Equal(
        "It is in a list of contexts, which names contexts rather than types",
        refusal "def same: [ [[Eq(a)]] ] a, a -> bool = { x, y -> x == y }\n")

[<Fact>]
let ``A hole is refused`` () =
    Assert.Equal("A `_` is left for inference to fill in", refusal "def total: list(int) -> [[_]] = { xs -> xs \\map { x -> x + 1 } }\n")

[<Fact>]
let ``Nothing is offered where the selection is not a written type`` () =
    Assert.Empty(snd (offersAt "def total: list(int) -> list(int) = { xs -> [[xs]] \\map { x -> x + 1 } }\n"))
