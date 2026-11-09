[<AutoOpen>]
module Parser.Program.Lexical

open System
open FParsec
// After the libraries, so that the parser's names shadow theirs (`identifier`, `Attribute`) as they did in one file.
open Parser.Program

// Parser for literals
let stringLiteralNoWs: Parser<Literal, ParseState> =
    let doubleQuoted =
        between (pstring "\"") (pstring "\"") (stringText (manyChars (noneOf "\"")))
        |>> StringLit
    let singleQuoted =
        between (pstring "'") (pstring "'") (stringText (manyChars (noneOf "'")))
        |>> StringLit
    attempt doubleQuoted <|> singleQuoted
    <?> "string literal"

let stringLiteral: Parser<Literal, ParseState> = stringLiteralNoWs  // Don't consume ws

let intLiteralNoWs: Parser<Literal, ParseState> =
    // Allow parsing int even if followed by dots (for range operator "..")
    // Just don't allow a SINGLE dot (which would make it a float)
    let notFloat = followedBy (pstring "..") <|> notFollowedBy (pchar '.')
    pint32 .>> notFloat |>> IntLit
    <?> "integer literal"

let intLiteral: Parser<Literal, ParseState> = intLiteralNoWs  // Don't consume ws - let caller decide

let floatLiteralNoWs: Parser<Literal, ParseState> =
    pfloat |>> FloatLit
    <?> "float literal"

let floatLiteral: Parser<Literal, ParseState> = floatLiteralNoWs  // Don't consume ws - let caller decide

let boolLiteralNoWs: Parser<Literal, ParseState> =
    (stringReturn "true" (BoolLit true) <|> stringReturn "false" (BoolLit false))
    <?> "boolean literal"

let boolLiteral: Parser<Literal, ParseState> = boolLiteralNoWs  // Don't consume ws - let caller decide

let literalNoWs: Parser<Literal, ParseState> =
    choice [
        attempt intLiteralNoWs
        attempt floatLiteralNoWs
        boolLiteralNoWs
        stringLiteralNoWs
    ]
    <?> "literal"

let literal: Parser<Literal, ParseState> =
    choice [
        attempt intLiteral
        attempt floatLiteral
        boolLiteral
        stringLiteral
    ]
    <?> "literal"

let modulePath: Parser<string, ParseState> =
    pipe2
        identifierNoWs
        (many (pipe2 (pchar '.' <|> pchar '/') identifierNoWs (fun sep seg -> string sep + seg)))
        (fun head tail -> head + String.concat "" tail)

let importSource: Parser<ImportSource, ParseState> =
    attempt (
        stringLiteralNoWs |>> function
            | StringLit s -> PathImport s
            | _ -> failwith "Expected string literal for import path"
    ) <|> (modulePath |>> ModuleImport)

let importItem: Parser<ImportItem, ParseState> =
    withSpan (importSource .>>. opt (attempt (ws >>. pstring "as" >>. ws >>. identifierNoWs)))
    |>> fun ((source, alias), span) -> { Source = source; Alias = alias; Span = span }

let importItems: Parser<ImportItem list, ParseState> =
    let importParenItems =
        between
            (pstring "(")
            (ws >>. pstring ")")
            (pipe2
                (ws >>. importItem)
                (many (attempt (ws >>. importItem)))
                (fun first rest -> first :: rest))

    let importSingleItems =
        importItem |>> fun item -> [item]

    choice [
        attempt importParenItems
        importSingleItems
    ]
