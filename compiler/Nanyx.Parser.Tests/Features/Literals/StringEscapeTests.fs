/// Backslash escapes in double-quoted strings (#247). Single-quoted strings are raw.
module ParserTests.Features.StringEscapes

open Xunit
open Parser.Program

let private parse (source: string) = parseModuleFile "t.nyx" source

[<Fact>]
let ``The escapes in a double-quoted string stand for the characters they name`` () =
    match parse """def s = "a\nb\tc\rd\\e\"f\{g\}h\u{e9}\u{1F600}" """ with
    | Result.Ok [ Kind (Def (ValueDef (_, "s", _, Kind (LiteralExpr (StringLit value))))) ] ->
        Assert.Equal("a\nb\tc\rd\\e\"f{g}hé\U0001F600", value)
    | other -> Assert.Fail $"Unexpected parse: %A{other}"

[<Fact>]
let ``An escaped brace in an interpolated string is text, not a hole`` () =
    match parse """def s = "\{x} = {x}\n" """ with
    | Result.Ok [ Kind (Def (ValueDef (_, "s", _, Kind (InterpolatedString [ StringText "{x} = "; StringExpr (Kind (IdentifierExpr "x")); StringText "\n" ])))) ] -> ()
    | other -> Assert.Fail $"Unexpected parse: %A{other}"

[<Fact>]
let ``An escaped quote does not end the string`` () =
    let source = """
def s = "say \"hi\""
def t = 1
"""
    match parse source with
    | Result.Ok [ Kind (Def (ValueDef (_, "s", _, Kind (LiteralExpr (StringLit "say \"hi\""))))); _ ] -> ()
    | other -> Assert.Fail $"Unexpected parse: %A{other}"

[<Fact>]
let ``A single-quoted string is raw: a backslash is a backslash`` () =
    match parse """def s = 'a\nb\q{c}'""" with
    | Result.Ok [ Kind (Def (ValueDef (_, "s", _, Kind (LiteralExpr (StringLit value))))) ] ->
        Assert.Equal("a\\nb\\q{c}", value)
    | other -> Assert.Fail $"Unexpected parse: %A{other}"

[<Fact>]
let ``A string pattern decodes its escapes`` () =
    let source = """
def f = { s ->
  match s
    | "a\tb" -> 1
    | _ -> 2
}
"""
    match parse source with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (Lambda (_, Kind (Block [ Kind (ExprStatement (Kind (Match (_, [ [ Kind (LiteralPattern (StringLit "a\tb")) ], _; _ ])))) ])))))) ] -> ()
    | other -> Assert.Fail $"Unexpected parse: %A{other}"

[<Theory>]
[<InlineData("""def s = "a\qb" """, "`\\q` is not an escape")>]
[<InlineData("""def s = "a{x}\qb" """, "`\\q` is not an escape")>]
[<InlineData("""def s = "\u{D800}" """, "`\\u{...}` is a code point in hex")>]
[<InlineData("""def s = "\u{110000}" """, "`\\u{...}` is a code point in hex")>]
[<InlineData("""def s = "\u{zz}" """, "`\\u{...}` is a code point in hex")>]
[<InlineData("""def s = "a\
b" """, "A backslash on its own is not an escape")>]
let ``An unknown escape is an error at its backslash`` (source: string, expected: string) =
    match parseModuleWithTrivia "t.nyx" source with
    | Result.Error error ->
        // FParsec wraps the message across lines.
        let message = error.Message.Replace("\r\n", " ").Replace("\n", " ")
        Assert.Contains(expected, message)
        Assert.Contains("The escapes in a double-quoted string are", message)
        Assert.Equal(source.IndexOf '\\', error.Span.Start)
        Assert.Equal(source.IndexOf '\\' + 1, error.Span.End)
    | other -> Assert.Fail $"Expected a parse error, got %A{other}"
