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

/// A `+` or `-` before a number, as text: empty when there is none.
let private numberSign: Parser<string, ParseState> =
    opt (attempt (anyOf "+-" .>> followedBy digit)) |>> function Some sign -> string sign | None -> ""

/// Decimal digits, which may be grouped with a `_` between two of them (`1_000_000`), without the underscores. With
/// `first`, the first character must be a digit; otherwise there may be no digits at all. A `_` that is not between
/// two digits is an error.
let private digitGroups (first: bool) : Parser<string, ParseState> =
    let isDigitOrSeparator c = isDigit c || c = '_'
    let digits = if first then many1Satisfy2 isDigit isDigitOrSeparator else manySatisfy isDigitOrSeparator
    getPosition .>>. digits >>= fun (start, text) ->
        if text.StartsWith "_" || text.EndsWith "_" || text.Contains "__" then
            reportFocused start $"A `_` in a number goes between two digits: '{text}' is not a number"
        else
            preturn (text.Replace("_", ""))

let intLiteralNoWs: Parser<Literal, ParseState> =
    // Allow parsing int even if followed by dots (for range operator "..")
    // Just don't allow a SINGLE dot (which would make it a float)
    let notFloat = followedBy (pstring "..") <|> notFollowedBy (pchar '.')
    // Hexadecimal, octal and binary, `0x1F`, are FParsec's.
    let nonDecimal = followedBy (optional (anyOf "+-") >>. pchar '0' >>. anyOf "xXoObB") >>. pint32
    let decimal =
        pipe2 numberSign (digitGroups true) (+) >>= fun text ->
            match Int32.TryParse(text, Globalization.NumberStyles.AllowLeadingSign, Globalization.CultureInfo.InvariantCulture) with
            | true, value -> preturn value
            | false, _ -> fail "The number is too large for an int"
    (attempt nonDecimal <|> decimal) .>> notFloat |>> IntLit
    <?> "integer literal"

let intLiteral: Parser<Literal, ParseState> = intLiteralNoWs  // Don't consume ws - let caller decide

// Only decimal floats: digits, an optional fraction and an optional exponent, each of which may be grouped with
// underscores. FParsec's `pfloat` also reads `NaN`, `Inf`, `Infinity` and hexadecimal floats, which would take names
// such as `nan` and `inf` as numbers.
let floatLiteralNoWs: Parser<Literal, ParseState> =
    let fraction = pchar '.' >>. digitGroups false |>> fun digits -> "." + digits
    let exponent = anyOf "eE" >>. pipe2 numberSign (digitGroups true) (fun sign digits -> "e" + sign + digits)
    pipe4 numberSign (digitGroups true) (opt fraction) (opt exponent) (fun sign whole fraction exponent ->
        sign + whole + Option.defaultValue "" fraction + Option.defaultValue "" exponent)
    |>> fun text -> FloatLit (Double.Parse(text, Globalization.CultureInfo.InvariantCulture))
    <?> "float literal"

let floatLiteral: Parser<Literal, ParseState> = floatLiteralNoWs  // Don't consume ws - let caller decide

let boolLiteralNoWs: Parser<Literal, ParseState> =
    let word text value = attempt (stringReturn text value .>> notFollowedBy (satisfy isIdentifierChar))
    (word "true" (BoolLit true) <|> word "false" (BoolLit false))
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
