module Nanyx.Compiler.Tests.Features.Refactorings.ImplementContext

open Xunit
open NyxCompiler
open NyxCompiler.Refactorings
open Nanyx.Formatter
open Nanyx.Compiler.Tests.TestSupport

// Implement a context for a type: stubs for its missing attached definitions.

/// The offers at `$$` in `marked`, and the source without the mark.
let private offersAt (marked: string) =
    let source, offset = unmarked marked
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    source, ImplementContext.at Formatter.Options.defaults "" source result offset Compiler.compile |> List.map Offer.decided

/// `text` without the line break it starts with, with `\n` line endings.
let private program (text: string) = text.TrimStart().ReplaceLineEndings "\n"

/// The text after the offer titled `title` at `$$`, checked to be formatted and to compile but for its `???`s.
let private implemented (title: string) (marked: string) =
    let source, offers = offersAt marked
    match offers |> List.tryFind (fun offer -> fst (Offer.titleAndKind offer) = title) with
    | Some (Available refactoring) ->
        let after = applyEdits source refactoring.Edits
        let result = Compiler.compile after
        Assert.All(result.Diagnostics, fun diagnostic -> Assert.Equal(DiagnosticCodes.NotImplemented.Code, diagnostic.Code))
        Assert.NotEmpty(result.Diagnostics)
        match FormatCommand.formatText Formatter.Options.defaults "" after with
        | FormatCommand.Formatting.Formatted formatted -> Assert.Equal(formatted, after)
        | other -> failwithf "%A" other
        after
    | other -> failwithf "expected the offer %s, got %A" title offers

/// The titles of the offers at `$$`.
let private titles (marked: string) = snd (offersAt marked) |> List.map (Offer.titleAndKind >> fst)

[<Fact>]
let ``Implementing Ord writes compare`` () =
    Assert.Equal(
        program
            """
type Point = protected (x: int, y: int)

def Point.compare: Point, Point -> #less | #equal | #greater = { a, b -> ??? }
""",
        implemented "Implement `Ord`" (program """
type $$Point = protected (x: int, y: int)
"""))

[<Fact>]
let ``A context the type attaches some of is first, and only its missing members are written`` () =
    let source =
        program
            """
export context Shape(a) = (area: a -> float, name: a -> string)

type $$Square = protected (side: float)

def Square.area: Square -> float = { s ->
  s.side * s.side
}
"""
    Assert.Equal("Implement `Shape`", List.head (titles source))
    Assert.Equal(
        program
            """
export context Shape(a) = (area: a -> float, name: a -> string)

type Square = protected (side: float)

def Square.area: Square -> float = { s ->
  s.side * s.side
}

def Square.name: Square -> string = { a -> ??? }
""",
        implemented "Implement `Shape`" source)

[<Fact>]
let ``An operator member is written in backticks`` () =
    Assert.Equal(
        program
            """
type Point = protected (x: int, y: int)

def Point.`==`: Point, Point -> bool = { a, b -> ??? }
""",
        implemented "Implement `Eq`" (program """
type $$Point = protected (x: int, y: int)
"""))

[<Fact>]
let ``A context whose members are all attached is refused`` () =
    let source =
        program
            """
type $$Name = protected (value: string)

def Name.toString: Name -> string = { n -> n.value }
"""
    match snd (offersAt source) |> List.find (fun offer -> fst (Offer.titleAndKind offer) = "Implement `ToString`") with
    | Refused(_, _, reason) -> Assert.Equal("`Name` attaches every member of `ToString`", reason)
    | other -> failwithf "%A" other

[<Fact>]
let ``A type with parameters is not offered`` () =
    Assert.Empty(titles "type $$Box(a) = protected (value: a)\n")

[<Fact>]
let ``A context's own definition is not offered`` () =
    Assert.Empty(titles "export context $$Shape(a) = (area: a -> float)\n")

[<Fact>]
let ``Elsewhere nothing is offered`` () =
    Assert.Empty(titles "type Point = protected (x: int, $$y: int)\n")