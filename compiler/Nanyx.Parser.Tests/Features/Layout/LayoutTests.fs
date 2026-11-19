/// The layout pass: where it puts `Indent`, `Newline` and `Dedent`, by the rules on the Compiler internals page "The
/// parser".
module Nanyx.Parser.Tests.LayoutTests

open Xunit
open Parser.Program
open Parser.Program.Layout

/// `source` laid out, one code token or layout token after another: `>` for `Indent`, `;` for `Newline` and `<` for
/// `Dedent`, each code token as its text.
let private laidOut (source: string) =
    let source = ParserTestHelpers.normalized source
    let lexed = Lexer.lex "t.nyx" source
    layout source lexed.Tokens
    |> Array.choose (function
        | Code token when token.Kind = Lexer.EndOfFile -> None
        | Code token -> Some(source.Substring(token.Start, token.End - token.Start))
        | Indent _ -> Some ">"
        | Newline _ -> Some ";"
        | Dedent _ -> Some "<")
    |> String.concat " "

[<Fact>]
let ``The top level's items are a block at the column of the first`` () =
    Assert.Equal("def x = 1 ; def y = 2", laidOut "def x = 1\ndef y = 2")
    Assert.Equal("def x = 1 ; def y = 2", laidOut "  def x = 1\n  def y = 2")

[<Fact>]
let ``A def's = at the end of its line opens a block of the lines after it`` () =
    Assert.Equal("def f = > a ; b < ; def g = 1", laidOut "def f =\n  a\n  b\ndef g = 1")

[<Fact>]
let ``A line indented further than its item, after no opener, continues it with no layout token`` () =
    Assert.Equal("def f = > xs \\ map ( g ) \\ sum ; y <", laidOut "def f =\n  xs\n    \\map(g)\n    \\sum\n  y")
    Assert.Equal("def x = a and b", laidOut "def x = a\n  and b")

[<Fact>]
let ``The arrow, then, else, do, in, match and a brace each open a block at the end of their line`` () =
    Assert.Equal("def f = { x -> > a ; b < }", laidOut "def f = { x ->\n  a\n  b\n}")
    Assert.Equal("if c then > a ; b < else > d ; e <", laidOut "if c then\n  a\n  b\nelse\n  d\n  e")
    Assert.Equal("def f = { > a ; b < }", laidOut "def f = {\n  a\n  b\n}")
    Assert.Equal("def f = do > a ; b <", laidOut "def f = do\n  a\n  b")

[<Fact>]
let ``A brace's block may be at any column`` () =
    Assert.Equal("def f = { > a ; b < }", laidOut "def f = {\na\nb\n}")

[<Fact>]
let ``A match's arms are a block, indented or lined up with the match`` () =
    Assert.Equal("match x > | A -> 1 ; | B -> 2 <", laidOut "match x\n  | A -> 1\n  | B -> 2")
    Assert.Equal("def f = > match x > | A -> 1 ; | B -> 2 < ; y <", laidOut "def f =\n  match x\n  | A -> 1\n  | B -> 2\n  y")

[<Fact>]
let ``A nested match ends at an arm indented back to the outer one`` () =
    Assert.Equal(
        "match xs > | [ a ] -> > match a > | 0 -> z ; | _ -> o < < ; | _ -> other <",
        laidOut "match xs\n  | [a] ->\n      match a\n        | 0 -> z\n        | _ -> o\n  | _ -> other")

[<Fact>]
let ``then and else at the block's column continue the if`` () =
    Assert.Equal("def f = > if c ; g <", laidOut "def f =\n  if c\n  g")
    Assert.Equal("def f = > if c then a else b ; g <", laidOut "def f =\n  if c\n  then a\n  else b\n  g")

[<Fact>]
let ``The body after use ... in may be at the column of the use`` () =
    Assert.Equal("def f = > use a in b ; c <", laidOut "def f =\n  use a in\n  b\n  c")

[<Fact>]
let ``Brackets have no layout in them, and a closing one on its own line is part of them`` () =
    Assert.Equal("def xs = [ 1 , 2 ] ; def y = 3", laidOut "def xs = [\n  1,\n  2\n]\ndef y = 3")
    Assert.Equal("f ( a = 1 b = 2 )", laidOut "f(\n  a = 1\n  b = 2\n)")

[<Fact>]
let ``A table's rows in brackets are not arms`` () =
    Assert.Equal("def t = > [ | a | b | | 1 | 2 | ] <", laidOut "def t =\n  [| a | b |\n   | 1 | 2 |]")

[<Fact>]
let ``A block opened in brackets closes with them`` () =
    Assert.Equal("f ( { x -> > a ; b < } ) ; g", laidOut "f({ x ->\n  a\n  b\n})\ng")
    Assert.Equal("f ( match x > | A -> 1 ; | B -> 2 < ) ; g", laidOut "f(match x\n  | A -> 1\n  | B -> 2)\ng")

[<Fact>]
let ``The end of the text closes every block but the top level's`` () =
    Assert.Equal("def f = > match x > | A -> 1 < <", laidOut "def f =\n  match x\n    | A -> 1")

[<Fact>]
let ``The layout tokens are where the code token after them starts`` () =
    let source = "def f =\n  a\n  b\nc"
    let laid = layout source (Lexer.lex "t.nyx" source).Tokens
    let offsets = laid |> Array.choose (function Indent at -> Some('>', at) | Newline at -> Some(';', at) | Dedent at -> Some('<', at) | Code _ -> None)
    Assert.Equal<(char * int) list>([ '>', 10; ';', 14; '<', 16; ';', 16 ], List.ofArray offsets)
