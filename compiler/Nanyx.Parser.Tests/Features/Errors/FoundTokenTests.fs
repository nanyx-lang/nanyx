/// What a syntax error says it found where it expected something else: the token, as data, for the headline to name.
module FoundTokenTests

open Xunit
open Parser.Program

/// The message of the error a parse of `source` fails with, and the token it found.
let private failure (source: string) =
    match parseModuleWithTrivia "t.nyx" source with
    | Result.Error error -> error.Message, error.Found
    | Result.Ok parsed -> failwith $"Expected a parse error, got %A{parsed.Items}"

// The found token was only in the message's text, as "Expected `]`, found `xs`", so no headline could name it (#2156).
[<Fact>]
let ``A syntax error carries the token it found, which its message leaves to the headline`` () =
    Assert.Equal(("Expected `)`", Some "`]`"), failure "def x = (1 + 2]\n")
    Assert.Equal(("Expected a name after `def`", Some "`=`"), failure "def = 1\n")

[<Fact>]
let ``A syntax error at the end of the file says so in its message, and found no token`` () =
    Assert.Equal(("Expected `)`, found the end of the file", None), failure "def x = (1 + 2")

[<Fact>]
let ``A mistake the lexer finds names no token`` () =
    Assert.Equal(None, snd (failure "def x = \"abc\n"))
