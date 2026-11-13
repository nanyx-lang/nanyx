module ParserTests.Features.DocComments

open Xunit
open Parser.Program

/// `source` with `\n` line breaks, whatever the line endings of this file's checkout.
let private normalized (source: string) = source.Replace("\r\n", "\n")

/// Each doc comment in `source`, as its text and what it documents: the text of the declared name or tag, `module`,
/// or `nothing`.
let private documented (source: string) =
    let source = normalized source
    (DocComments.parse "t.nyx" source).Comments
    |> List.map (fun comment ->
        let target =
            match comment.Target with
            | DocumentsDeclaration span
            | DocumentsTag span -> source.Substring(span.Start, span.End - span.Start)
            | DocumentsModule -> "module"
            | DocumentsNothing -> "nothing"
        comment.Text, target)

let private items (source: string) =
    match parseModule (normalized source) with
    | Result.Ok items -> items
    | Result.Error message -> failwith $"Parse failed: {message}"

[<Fact>]
let ``A doc comment documents the def after it`` () =
    Assert.Equal<_ list>(
        [ "The answer.", "answer" ],
        documented
            """--| The answer.
export def answer = 42
"""
    )

[<Fact>]
let ``A doc comment goes on for the lines indented further than its first, which are not code`` () =
    let source =
        """--| The `length` bytes of `s` starting at byte `start`. Out-of-range values are clamped, so the result
  is never longer than what `s` actually holds.
def substring = 1
"""
    Assert.Equal<_ list>(
        [ "The `length` bytes of `s` starting at byte `start`. Out-of-range values are clamped, so the result\nis never longer than what `s` actually holds.",
          "substring" ],
        documented source
    )
    Assert.Equal(1, (items source).Length)

[<Fact>]
let ``Continuation is measured from the start of the line, not from the bar`` () =
    // The second line is indented one space: further than the `--|` line, though not as far as its `|`.
    let source =
        """def f = {
  --| A local def.
   More about it.
  def g = 1
  g
}
"""
    Assert.Equal<_ list>([ "A local def.\nMore about it.", "g" ], documented source)

[<Fact>]
let ``Blank lines stay in a doc comment while indented text follows them`` () =
    let source =
        """--| Adds one.

  For example:

      inc(1) == 2

def inc = { n -> n + 1 }
"""
    Assert.Equal<_ list>([ "Adds one.\n\nFor example:\n\n    inc(1) == 2", "inc" ], documented source)

[<Fact>]
let ``A blank line after a doc comment does not stop it documenting what follows`` () =
    Assert.Equal<_ list>([ "One.", "one" ], documented "--| One.\n\ndef one = 1\n")

[<Fact>]
let ``A doc comment documents types, contexts, their members, fields and tags`` () =
    let source =
        """--| A shape.
type Shape =
  --| Round.
  | #circle(float)
  --| Not round.
  | #square(float)

--| A point.
type Point = (
  --| Across.
  x: int
  y: int
)

--| Things that count.
context Counter = (
  --| The next number.
  next: int -> int
)
"""
    Assert.Equal<_ list>(
        [ "A shape.", "Shape"
          "Round.", "#circle(float)"
          "Not round.", "#square(float)"
          "A point.", "Point"
          "Across.", "x"
          "Things that count.", "Counter"
          "The next number.", "next" ],
        documented source
    )

[<Fact>]
let ``A doc comment at the top of the file before module or an import documents the module`` () =
    Assert.Equal<_ list>([ "Shapes.", "module" ], documented "--| Shapes.\nmodule shapes\n\ndef a = 1\n")
    Assert.Equal<_ list>([ "Text.", "module" ], documented "--| Text.\nimport nanyx/text\n\ndef a = 1\n")
    Assert.Equal<_ list>([ "A.", "a" ], documented "--| A.\ndef a = 1\n")

[<Fact>]
let ``A doc comment skips the attributes before what it documents`` () =
    Assert.Equal<_ list>([ "The entry point.", "main" ], documented "--| The entry point.\n@entry\ndef main = 1\n")

[<Fact>]
let ``A doc comment before what it cannot document documents nothing`` () =
    let source =
        """def f = {
  --| Not a def.
  print(1)
}
--| At the end.
"""
    Assert.Equal<_ list>([ "Not a def.", "nothing"; "At the end.", "nothing" ], documented source)

[<Fact>]
let ``A --| after code on its line, or inside a block comment, is an ordinary comment`` () =
    let source =
        """def a = 1 --| not a doc comment
  + 1
---
--| commented out
---
def b = 2
"""
    Assert.Empty(documented source)

[<Fact>]
let ``A doc comment is kept as one comment for the formatter`` () =
    match parseModuleWithTrivia "" (normalized "--| One\n  two\n\ndef a = 1\n") with
    | Result.Ok parsed ->
        match parsed.Trivia with
        | [ DocLineComment(text, _); BlankLines(1, _) ] -> Assert.Equal("--| One\n  two", text)
        | other -> failwith $"Unexpected trivia: %A{other}"
    | Result.Error error -> failwith error.Message
