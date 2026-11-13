[<AutoOpen>]
module Parser.Program.Lexical

open System
open FParsec
// After the libraries, so that the parser's names shadow theirs (`identifier`, `Attribute`) as they did in one file.
open Parser.Program

/// A backslash escape in a double-quoted string (see `StringEscapes`), as the text it stands for. Any other backslash
/// fails there, saying which escapes there are.
let escapeSequence: Parser<string, ParseState> =
    let simple = StringEscapes.simple |> List.map (fun (c, decoded) -> pchar c >>% string decoded) |> choice
    let codePoint =
        attempt (
            pstring "u{" >>. manyChars hex .>> pchar '}'
            >>= fun digits ->
                match StringEscapes.tryCodePoint digits with
                | Some decoded -> preturn decoded
                | None -> pzero)
    getPosition .>>? pchar '\\'
    >>= fun position ->
        simple
        <|> codePoint
        <|> (opt (lookAhead anyChar) >>= fun next ->
            let message =
                match next with
                | Some 'u' -> "`\\u{...}` is a code point in hex, from 0 to 10FFFF, and not a surrogate"
                | Some c when not (Char.IsControl c) -> $"`\\{c}` is not an escape"
                | _ -> "A backslash on its own is not an escape"
            reportFocused position
                $"{message}. The escapes in a double-quoted string are {StringEscapes.written}; write a backslash as `\\\\`, or use a single-quoted string, which is raw")

/// The text of a double-quoted string up to its closing quote or, with `stops`, any of those characters.
let internal doubleQuotedText (stops: string) : Parser<string, ParseState> =
    many1Strings (many1Chars (noneOf ("\"\\" + stops)) <|> escapeSequence)

// Parser for literals
let stringLiteralNoWs: Parser<Literal, ParseState> =
    let doubleQuoted =
        between (pstring "\"") (pstring "\"") (stringText (doubleQuotedText "" <|>% ""))
        |>> StringLit
    let singleQuoted =
        between (pstring "'") (pstring "'") (stringText (manyChars (noneOf "'")))
        |>> StringLit
    attempt doubleQuoted <|> singleQuoted
    <?> "string literal"

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
    let nonDecimal = followedBy (optional (anyOf "+-") >>. pchar '0' >>. anyOf "xXoObB") >>. pint64 |>> bigint
    // Any number of digits: whether the value fits the literal's type is the type checker's to say.
    let decimal =
        pipe2 numberSign (digitGroups true) (+)
        |>> fun text -> Numerics.BigInteger.Parse(text, Globalization.NumberStyles.AllowLeadingSign, Globalization.CultureInfo.InvariantCulture)
    (attempt nonDecimal <|> decimal) .>> notFloat |>> IntLit
    <?> "integer literal"

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

let boolLiteralNoWs: Parser<Literal, ParseState> =
    let word text value = attempt (stringReturn text value .>> notFollowedBy (satisfy isIdentifierChar))
    (word "true" (BoolLit true) <|> word "false" (BoolLit false))
    <?> "boolean literal"

let literalNoWs: Parser<Literal, ParseState> =
    choice [
        attempt intLiteralNoWs
        attempt floatLiteralNoWs
        boolLiteralNoWs
        stringLiteralNoWs
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

/// The names an import selects, in brackets, separated by commas or line breaks: `(sqrt, pi)`. A name may be a type's
/// (`Email`) or an attached definition's (`string.shout`).
let private importSelection: Parser<(Identifier * SourceSpan) list, ParseState> =
    let name = withSpan qualifiedIdentifier
    between
        (pstring "(" >>. ws)
        (ws >>. pstring ")")
        (pipe2 name (many (attempt (ws >>. optional (pstring "," >>. ws) >>. name))) (fun first rest -> first :: rest))

/// What follows `as`: an alias, a selection, or both, as `m & (a, b)`. `as` is a keyword, so once it is read, what
/// follows it is one of these and never a call of a function named `as`.
let private importAs: Parser<Identifier option * (Identifier * SourceSpan) list option, ParseState> =
    (importSelection |>> fun selection -> None, Some selection)
    <|> (identifierNoWs .>>. opt (attempt (ws >>. pstring "&") >>. ws >>. importSelection)
         |>> fun (alias, selection) -> Some alias, selection)

let importItem: Parser<ImportItem, ParseState> =
    withSpan (importSource .>>. opt (attempt (ws >>. pstring "as" >>. keywordEnd) >>. ws >>. importAs))
    |>> fun ((source, written), span) ->
        let alias, selection = written |> Option.defaultValue (None, None)
        { Source = source; Alias = alias; Selection = selection; Span = span }

/// What follows `export` in a re-export, `export collections.list (map, filter)`: the module, then on the same line the
/// names it exports that are exported again.
let reExportItem: Parser<ImportItem, ParseState> =
    withSpan (importSource .>>. (wsInline >>. importSelection))
    |>> fun ((source, selection), span) -> { Source = source; Alias = None; Selection = Some selection; Span = span }

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
