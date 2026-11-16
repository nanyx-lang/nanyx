/// The lexer: each kind of token and trivia, and that the tokens give back the text they were read from.
module Nanyx.Parser.Tests.LexerTests

open Xunit
open Parser.Program
open Parser.Program.Lexer

/// The tokens of `source` as their kind and text, without the end of the file.
let private tokens (source: string) =
    let lexed = lex "t.nyx" source
    Assert.Equal(source, write source lexed.Tokens)
    lexed.Tokens
    |> Array.filter (fun token -> token.Kind <> EndOfFile)
    |> Array.map (fun token -> token.Kind, source.Substring(token.Start, token.End - token.Start))
    |> List.ofArray

/// The trivia of `source` as its kind and text, in order.
let private trivia (source: string) =
    (lex "t.nyx" source).Tokens
    |> Seq.collect (fun token -> Seq.append token.Leading token.Trailing)
    |> Seq.map (fun piece -> piece.Kind, source.Substring(piece.Start, piece.End - piece.Start))
    |> List.ofSeq

let private errorsOf (source: string) = (lex "t.nyx" source).Errors |> List.map (fun error -> error.Message, error.Span.Start)

[<Fact>]
let ``A keyword is a whole word, so a name that starts with one is a name`` () =
    Assert.Equal<(TokenKind * string) list>(
        [ Keyword, "type"; Name, "typeName"; Name, "trueValue"; Keyword, "and"; Name, "useful"; Name, "_x1"; Name, "true" ],
        tokens "type typeName trueValue and useful _x1 true")

[<Fact>]
let ``A name in backticks is one token`` () =
    Assert.Equal<(TokenKind * string) list>(
        [ Name, "Point"; Dot, "."; BacktickedName, "`+`"; Keyword, "def"; BacktickedName, "`adds points`" ],
        tokens "Point.`+` def `adds points`")

[<Fact>]
let ``Numbers have digit separators, bases, fractions and exponents, and no sign`` () =
    Assert.Equal<(TokenKind * string) list>(
        [ Number, "1_000_000"; Number, "0x1F"; Number, "0b1010"; Number, "2.5"; Number, "1e10"; Operator, "-"; Number, "2.5E-3" ],
        tokens "1_000_000 0x1F 0b1010 2.5 1e10 -2.5E-3")

[<Fact>]
let ``A number before a range is a whole number`` () =
    Assert.Equal<(TokenKind * string) list>([ Number, "1"; DotDot, ".."; Number, "10" ], tokens "1..10")

[<Fact>]
let ``A string's escapes are part of it, and a quote after a backslash does not end it`` () =
    Assert.Equal<(TokenKind * string) list>(
        [ StringLiteral, "\"a\\\"b\\n\\{\""; StringLiteral, "\"\\u{1F600}\"" ],
        tokens "\"a\\\"b\\n\\{\" \"\\u{1F600}\"")

[<Fact>]
let ``A single-quoted string is raw, and may run over lines`` () =
    Assert.Equal<(TokenKind * string) list>([ StringLiteral, "'a\\b\n{c}'" ], tokens "'a\\b\n{c}'")

[<Fact>]
let ``A string with holes is a start, the holes' tokens, the text between them and an end`` () =
    Assert.Equal<(TokenKind * string) list>(
        [ StringStart, "\"total: {"
          Name, "n"
          StringMiddle, "} of {"
          Name, "f"
          LeftParen, "("
          StringLiteral, "\"x\""
          RightParen, ")"
          StringEnd, "} items\"" ],
        tokens "\"total: {n} of {f(\"x\")} items\"")

[<Fact>]
let ``Braces in a hole are the hole's own`` () =
    Assert.Equal<(TokenKind * string) list>(
        [ StringStart, "\"{"; Name, "map"; LeftBrace, "{"; Name, "x"; Arrow, "->"; Name, "x"; RightBrace, "}"; StringEnd, "}\"" ],
        tokens "\"{map { x -> x }}\"")

[<Fact>]
let ``The longest symbol written is read, of those the language has`` () =
    Assert.Equal<(TokenKind * string) list>(
        [ Operator, ">>="; Operator, "<*>"; Ellipsis, "..."; ColonEquals, ":="; DoubleColon, "::"; Operator, "??"
          FatArrow, "=>"; Operator, "!="; Backslash, "\\"; Hash, "#"; Name, "some"; At, "@"; Name, "entry" ],
        tokens ">>= <*> ... := :: ?? => != \\#some @entry")

[<Fact>]
let ``An = before a minus sign is two tokens`` () =
    Assert.Equal<(TokenKind * string) list>([ Name, "x"; Equals, "="; Operator, "-"; Number, "1" ], tokens "x =-1")

[<Fact>]
let ``A comment runs to the end of its line and trails the code before it`` () =
    let lexed = lex "t.nyx" "def x = 1 -- one\ndef y = 2"
    let one = lexed.Tokens |> Array.find (fun token -> token.Start = 8)
    Assert.Equal<TriviaKind list>([ Whitespace; LineComment; LineBreak ], one.Trailing |> List.map _.Kind)
    Assert.Equal<(TriviaKind * string) list>(
        [ Whitespace, " "; Whitespace, " "; Whitespace, " "; Whitespace, " "; LineComment, "-- one"; LineBreak, "\n"; Whitespace, " "; Whitespace, " "; Whitespace, " " ],
        trivia "def x = 1 -- one\ndef y = 2")

[<Fact>]
let ``A doc comment takes the lines indented further than it, and the blank lines between them`` () =
    let source = "--| Adds.\n    More.\n\n    Still more.\n\ndef add = 1"
    Assert.Contains((DocComment, "--| Adds.\n    More.\n\n    Still more."), trivia source)

[<Fact>]
let ``A --| after code is a line comment`` () =
    Assert.Contains((LineComment, "--| not a doc"), trivia "x --| not a doc")

[<Fact>]
let ``A block comment runs to the line with the same dashes, and code inside it is not read`` () =
    let source = "---\ndef x = \"\n----\n  ---\ndef y = 1"
    Assert.Contains((BlockComment, "---\ndef x = \"\n----\n  ---"), trivia source)
    Assert.Equal<(TokenKind * string) list>([ Keyword, "def"; Name, "y"; Equals, "="; Number, "1" ], tokens source)
    Assert.Empty(errorsOf source)

[<Fact>]
let ``Directives and a #! line at the top are trivia, and :: in code is a token`` () =
    let source = "#!/usr/bin/env nanyx\n::host nanyx.console\n\ndef f: (x :: int) = 1"
    Assert.Equal<(TriviaKind * string) list>(
        [ Directive, "#!/usr/bin/env nanyx"; LineBreak, "\n"; Directive, "::host nanyx.console"; LineBreak, "\n"; LineBreak, "\n" ],
        trivia source |> List.truncate 5)
    Assert.Contains((DoubleColon, "::"), tokens source)

[<Fact>]
let ``Windows line breaks are one piece of trivia each`` () =
    let source = "def x = 1 -- one\r\n\r\ndef y = 2\r\n"
    Assert.Equal(3, trivia source |> List.filter (fun (kind, _) -> kind = LineBreak) |> List.length)
    Assert.Contains((LineComment, "-- one"), trivia source)

[<Fact>]
let ``The end of the file holds the trivia after the last line`` () =
    let lexed = lex "t.nyx" "x\n\n-- last\n"
    let last = Array.last lexed.Tokens
    Assert.Equal(EndOfFile, last.Kind)
    Assert.Equal<TriviaKind list>([ LineBreak; LineComment; LineBreak ], last.Leading |> List.map _.Kind)

[<Fact>]
let ``A string that is never closed runs to the end, with an error at its quote`` () =
    Assert.Equal<(TokenKind * string) list>([ Name, "x"; Equals, "="; StringLiteral, "\"abc\ndef y = 1" ], tokens "x = \"abc\ndef y = 1")
    Assert.Equal<(string * int) list>([ "This string is never closed: end it with a `\"`", 4 ], errorsOf "x = \"abc\ndef y = 1")

[<Fact>]
let ``A block comment that is never closed runs to the end, with an error at it`` () =
    let source = "---\ndef x = 1"
    Assert.Equal<(TriviaKind * string) list>([ BlockComment, source ], trivia source)
    Assert.Equal<int list>([ 0 ], errorsOf source |> List.map snd)

[<Fact>]
let ``A character that is no part of the syntax is a token of its own, with an error`` () =
    Assert.Equal<(TokenKind * string) list>([ Name, "x"; Unknown, "€"; Name, "y" ], tokens "x € y")
    Assert.Equal<(string * int) list>([ "'€' is not part of Nanyx's syntax", 2 ], errorsOf "x € y")

[<Fact>]
let ``A name in backticks that is never closed is an error at its backtick`` () =
    Assert.Equal<(TokenKind * string) list>([ Unknown, "`"; Name, "x" ], tokens "`x")
    Assert.Equal<int list>([ 0 ], errorsOf "`x" |> List.map snd)
