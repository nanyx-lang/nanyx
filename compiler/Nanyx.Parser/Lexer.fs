/// The lexer: source text in, tokens out, each with the comments, whitespace and line breaks around it. It is
/// lossless: the tokens and their trivia, in order, are the source exactly, whatever the source is, so a mistake in it
/// is a token too (`Unknown`, or a string or comment that runs to the end) with an error beside it.
module Parser.Program.Lexer

open System
open Parser.Program

/// What a piece of trivia is: text between tokens that is not code.
type TriviaKind =
    /// Spaces and tabs.
    | Whitespace
    /// `\n`, `\r\n` or `\r`.
    | LineBreak
    /// `--` to the end of its line, without the line break. A `--|` or a line of dashes after code on its line is one.
    | LineComment
    /// `--|` on a line of its own, and the lines after it indented further than it, with the blank lines between
    /// them, to the end of the last such line.
    | DocComment
    /// A line of three or more dashes on its own, to the end of the next line that holds exactly those dashes.
    | BlockComment
    /// A `::` directive line, or a `#!` first line, at the top of the file (see `Directives`), without its line break.
    | Directive

/// A piece of trivia: what it is, and the offsets it starts and ends at (`End` one past its last character).
type Trivia = { Kind: TriviaKind; Start: int; End: int }

/// What a token is. The text of a token is the source between its offsets.
type TokenKind =
    /// An identifier that is not a reserved word: `x`, `typeName`, `true`.
    | Name
    /// One of `Keywords.reserved`: `def`, `match`, `and`.
    | Keyword
    /// A name in backticks, with them: `` `+` ``, `` `adds points` ``.
    | BacktickedName
    /// An integer or a float, without a sign, and the unit written straight after it if it has one: `42`, `1_000`, `0x1F`,
    /// `2.5e-3`, `500ms`, `1.5e3ms` (see `numberEnd`).
    | Number
    /// A string with no holes, with its quotes: `"a\n"`, `'raw'`.
    | StringLiteral
    /// A double-quoted string's text up to its first hole, with the quote and the hole's `{`: `"total: {`.
    | StringStart
    /// The text between two holes, with the `}` before it and the `{` after it: `} and {`.
    | StringMiddle
    /// The text after a string's last hole, with its `}` and the closing quote: `} items"`.
    | StringEnd
    /// A hole's format spec, from the `:` that starts it, outside any bracket the hole opens, to the hole's `}`, as it
    /// is written: `:.2f` in `"{price:.2f}"` (see `FormatSyntax`).
    | FormatSpec
    /// A binary operator written with symbols (see `Operators.binaryOperators`), or `!` or `?`.
    | Operator
    | LeftParen
    | RightParen
    | LeftBracket
    | RightBracket
    | LeftBrace
    | RightBrace
    | Comma
    | Semicolon
    | Colon
    | DoubleColon
    | Dot
    | DotDot
    | Ellipsis
    | Bar
    | Ampersand
    | Backslash
    | At
    | Hash
    | Dollar
    | Equals
    | Arrow
    | FatArrow
    | ColonEquals
    /// A character that is no part of the syntax: an error is reported at it.
    | Unknown
    /// The end of the text: no characters, with the trivia after the last token as its leading trivia.
    | EndOfFile

/// A token: what it is, its offsets, the trivia before it (from the end of the previous token's trailing trivia)
/// and the trivia after it (on its own line, up to and including the line break that ends it).
type Token =
    { Kind: TokenKind
      Start: int
      End: int
      Leading: Trivia list
      Trailing: Trivia list }

/// The tokens of a text, the last of them `EndOfFile`, and the mistakes found in it.
type Lexed = { Tokens: Token array; Errors: ParseError list }

/// The symbols that are tokens, longest first so that the longest one written is read.
let private symbols =
    let fixedSymbols =
        [ "(", LeftParen; ")", RightParen; "[", LeftBracket; "]", RightBracket; "{", LeftBrace; "}", RightBrace
          ",", Comma; ";", Semicolon; ":", Colon; "::", DoubleColon; ".", Dot; "..", DotDot; "...", Ellipsis
          "|", Bar; "&", Ampersand; "\\", Backslash; "@", At; "#", Hash; "$", Dollar; "=", Equals; "->", Arrow
          "=>", FatArrow; ":=", ColonEquals; "!", Operator; "?", Operator ]
    let operators =
        binaryOperators
        |> List.filter (fun op -> not (Names.isIdentifier op.Text))
        |> List.map (fun op -> op.Text, Operator)
    fixedSymbols @ operators |> List.sortByDescending (fun (text, _) -> text.Length)

let private isIdentifierStart (c: char) = Char.IsLetter c || c = '_'
let private isIdentifierChar (c: char) = Char.IsLetter c || Char.IsAsciiDigit c || c = '_'
let private isFence (line: string) = line.Length >= 3 && line |> Seq.forall ((=) '-')

/// Whether the number at `start` of `text` is hexadecimal, octal or binary: `0x1F`, `0o17`, `0b101`.
let internal isRadixNumber (text: string) (start: int) =
    let at (i: int) = if i < text.Length then text[i] else '\000'
    at start = '0' && "xXoObB".IndexOf(at (start + 1)) >= 0 && (Uri.IsHexDigit(at (start + 2)) || at (start + 2) = '_')

/// The end of the number that starts at `start` of `text`, without a unit after it: its digits and `_`s, a fraction and
/// an exponent, or a hexadecimal, octal or binary number's digits.
let numberEnd (text: string) (start: int) : int =
    let at (i: int) = if i < text.Length then text[i] else '\000'
    let mutable j = start + 1
    let digits () =
        while Char.IsAsciiDigit(at j) || at j = '_' do
            j <- j + 1
    if isRadixNumber text start then
        j <- start + 2
        while Uri.IsHexDigit(at j) || at j = '_' do
            j <- j + 1
    else
        digits ()
        if at j = '.' && Char.IsAsciiDigit(at (j + 1)) then
            j <- j + 1
            digits ()
        if (at j = 'e' || at j = 'E')
           && (Char.IsAsciiDigit(at (j + 1)) || ((at (j + 1) = '+' || at (j + 1) = '-') && Char.IsAsciiDigit(at (j + 2)))) then
            j <- j + 2
            digits ()
    j

/// The end of the unit written straight after a number that ends at `numberEnd` of `text`, `ms` in `500ms`: a name
/// that starts with a letter, or `numberEnd` itself when there is none.
let private unitEnd (text: string) (numberEnd: int) : int =
    let mutable j = numberEnd
    if j < text.Length && Char.IsLetter text[j] then
        while j < text.Length && isIdentifierChar text[j] do
            j <- j + 1
    j

/// A piece of the text in order: a token, or a piece of trivia.
type private Piece =
    | TokenPiece of TokenKind * int * int
    | TriviaPiece of Trivia

/// Reads `source` into pieces, in order, with the mistakes found.
let private scan (filePath: string) (source: string) : Piece list * ParseError list =
    let lines = LineTable source
    let headerEnd = Directives.headerEnd source
    let pieces = Collections.Generic.List<Piece>()
    let errors = Collections.Generic.List<ParseError>()
    let error (start: int) (finish: int) (message: string) = errors.Add { Message = message; Span = lines.Span filePath start finish; Found = None }
    let at (i: int) = if i < source.Length then source[i] else '\000'
    let isLineEnd (i: int) = i >= source.Length || source[i] = '\n' || source[i] = '\r'
    let lineEnd (i: int) =
        let mutable j = i
        while not (isLineEnd j) do
            j <- j + 1
        j
    let lineBreakLength (i: int) = if at i = '\r' && at (i + 1) = '\n' then 2 elif isLineEnd i && i < source.Length then 1 else 0
    // The holes of the strings the text is in, innermost first: how many brackets and braces are open in each.
    let mutable holes: int list = []
    // Whether only spaces and tabs come before the current offset on its line, and where that line starts.
    let mutable atLineStart = true
    let mutable lineStart = 0
    let mutable i = 0

    let token (kind: TokenKind) (finish: int) =
        pieces.Add(TokenPiece(kind, i, finish))
        atLineStart <- false
        i <- finish
    let trivia (kind: TriviaKind) (finish: int) =
        pieces.Add(TriviaPiece { Kind = kind; Start = i; End = finish })
        i <- finish

    /// The end of a string's text from `from`, and whether a hole follows it rather than the closing quote.
    let stringText (from: int) =
        let mutable j = from
        let mutable result = None
        while result.IsNone do
            if j >= source.Length then
                error i source.Length "This string is never closed: end it with a `\"`"
                result <- Some(source.Length, false)
            else
                match source[j] with
                | '"' -> result <- Some(j + 1, false)
                | '{' -> result <- Some(j + 1, true)
                | '\\' when at (j + 1) = 'u' && at (j + 2) = '{' ->
                    j <- j + 3
                    while Uri.IsHexDigit(at j) do
                        j <- j + 1
                    if at j = '}' then j <- j + 1
                | '\\' -> j <- min source.Length (j + 2)
                | _ -> j <- j + 1
        result.Value

    let doubleQuoted (from: int) (first: bool) =
        let finish, opensHole = stringText from
        if opensHole then holes <- 0 :: holes
        let kind =
            match first, opensHole with
            | true, false -> StringLiteral
            | true, true -> StringStart
            | false, true -> StringMiddle
            | false, false -> StringEnd
        token kind finish

    /// A `--` comment at `i`, which is a block or doc comment when it starts its line.
    let comment () =
        let finish = lineEnd i
        let text = source.Substring(i, finish - i).TrimEnd()
        if atLineStart && isFence text then
            // To the end of the next line holding exactly these dashes.
            let mutable j = finish
            let mutable closed = -1
            while closed < 0 && j < source.Length do
                j <- j + lineBreakLength j
                let next = lineEnd j
                if source.Substring(j, next - j).Trim() = text then closed <- next
                j <- next
            if closed < 0 then
                error i finish $"This block comment is never closed: end it with a line of exactly the same dashes, {text}"
                trivia BlockComment source.Length
            else
                trivia BlockComment closed
        elif atLineStart && text.StartsWith "--|" then
            // To the end of the last line after it indented further than it, across blank lines.
            let indent = i - lineStart
            let mutable j = finish
            let mutable taken = finish
            let mutable going = true
            while going && j < source.Length do
                let next = j + lineBreakLength j
                let mutable content = next
                while at content = ' ' || at content = '\t' do
                    content <- content + 1
                let nextEnd = lineEnd content
                if isLineEnd content && content < source.Length then j <- nextEnd
                elif not (isLineEnd content) && content - next > indent then
                    taken <- nextEnd
                    j <- nextEnd
                else going <- false
            trivia DocComment taken
        else
            trivia LineComment finish

    while i < source.Length do
        let c = source[i]
        if c = ' ' || c = '\t' then
            let mutable j = i
            while at j = ' ' || at j = '\t' do
                j <- j + 1
            trivia Whitespace j
        elif c = '\n' || c = '\r' then
            trivia LineBreak (i + lineBreakLength i)
            atLineStart <- true
            lineStart <- i
        elif i = lineStart && i < headerEnd && (String.CompareOrdinal(source, i, "::", 0, 2) = 0 || (i = 0 && String.CompareOrdinal(source, i, "#!", 0, 2) = 0)) then
            trivia Directive (lineEnd i)
        elif c = '-' && at (i + 1) = '-' then
            comment ()
        elif isIdentifierStart c then
            let mutable j = i + 1
            while isIdentifierChar (at j) do
                j <- j + 1
            token (if Keywords.reserved.Contains(source.Substring(i, j - i)) then Keyword else Name) j
        elif Char.IsAsciiDigit c then
            let numberEnd = numberEnd source i
            if Char.IsLetter(at numberEnd) && isRadixNumber source i then
                error numberEnd (unitEnd source numberEnd) "A hexadecimal, octal or binary number takes no unit: put a space before a name after it"
                token Number numberEnd
            else
                token Number (unitEnd source numberEnd)
        elif c = '"' then
            doubleQuoted (i + 1) true
        elif c = '\'' then
            match source.IndexOf('\'', i + 1) with
            | -1 ->
                error i (i + 1) "This string is never closed: end it with a `'`"
                token StringLiteral source.Length
            | close -> token StringLiteral (close + 1)
        elif c = '`' then
            let mutable j = i + 1
            while j < source.Length && Names.isBacktickedChar source[j] do
                j <- j + 1
            if at j = '`' then
                token BacktickedName (j + 1)
            else
                error i (i + 1) "This name in backticks is never closed: end it with a `` ` `` on the same line, before any `.`"
                token Unknown (i + 1)
        elif c = '}' && not holes.IsEmpty && holes.Head = 0 then
            holes <- holes.Tail
            doubleQuoted (i + 1) false
        elif c = ':' && not holes.IsEmpty && holes.Head = 0 then
            // A `:` outside any bracket the hole opens starts its format spec, which runs to the hole's `}`, though the
            // spec starts with `:` or `=`, as `"{x::^5}"` and `"{n:=+6}"` do.
            let mutable j = i + 1
            while not (isLineEnd j) && source[j] <> '}' do
                j <- j + 1
            if isLineEnd j then error i j "This format spec is never closed: end it, and its hole, with a `}` on the same line"
            token FormatSpec j
        else
            match symbols |> List.tryFind (fun (text, _) -> String.CompareOrdinal(source, i, text, 0, text.Length) = 0) with
            | Some(text, kind) ->
                match kind, holes with
                | (LeftBrace | LeftParen | LeftBracket), depth :: outer ->
                    holes <- (depth + 1) :: outer
                    token kind (i + text.Length)
                | (RightBrace | RightParen | RightBracket), depth :: outer ->
                    holes <- max 0 (depth - 1) :: outer
                    token kind (i + text.Length)
                | _ -> token kind (i + text.Length)
            | None ->
                let width = if Char.IsHighSurrogate c && Char.IsLowSurrogate(at (i + 1)) then 2 else 1
                let character = source.Substring(i, width)
                error i (i + width) $"'{character}' is not part of Nanyx's syntax"
                token Unknown (i + width)

    if not holes.IsEmpty then
        error source.Length source.Length "A string's hole is never closed: end it with a `}`, then end the string with a `\"`"
    List.ofSeq pieces, List.ofSeq errors

/// The tokens of `source`, the text of the file at `filePath`, with the trivia around each and the mistakes found.
/// Line breaks may be `\n`, `\r\n` or `\r`.
let lex (filePath: string) (source: string) : Lexed =
    let pieces, errors = scan filePath source
    let tokens = Collections.Generic.List<Token>()
    let leading = Collections.Generic.List<Trivia>()
    // The trivia after the last token, while it is still on that token's line.
    let mutable trailing: Collections.Generic.List<Trivia> option = None
    let finishTrailing () =
        match trailing with
        | Some pieces ->
            let last = tokens[tokens.Count - 1]
            tokens[tokens.Count - 1] <- { last with Trailing = List.ofSeq pieces }
            trailing <- None
        | None -> ()
    for piece in pieces do
        match piece with
        | TokenPiece(kind, start, finish) ->
            finishTrailing ()
            tokens.Add { Kind = kind; Start = start; End = finish; Leading = List.ofSeq leading; Trailing = [] }
            leading.Clear()
            trailing <- Some(Collections.Generic.List())
        | TriviaPiece trivia ->
            match trailing with
            | Some pieces when trivia.Kind = Whitespace || trivia.Kind = LineComment -> pieces.Add trivia
            | Some pieces when trivia.Kind = LineBreak ->
                pieces.Add trivia
                finishTrailing ()
            | _ ->
                finishTrailing ()
                leading.Add trivia
    finishTrailing ()
    tokens.Add { Kind = EndOfFile; Start = source.Length; End = source.Length; Leading = List.ofSeq leading; Trailing = [] }
    { Tokens = tokens.ToArray(); Errors = errors }

/// The text `tokens` were lexed from, written back from `source`: each token's leading trivia, its text and its
/// trailing trivia, in order. For a whole lexed text it is the text itself.
let write (source: string) (tokens: Token seq) : string =
    let out = Text.StringBuilder()
    let append (start: int) (finish: int) = out.Append(source, start, finish - start) |> ignore
    for token in tokens do
        for trivia in token.Leading do
            append trivia.Start trivia.End
        append token.Start token.End
        for trivia in token.Trailing do
            append trivia.Start trivia.End
    out.ToString()
