/// What the recursive-descent parser reads with: a cursor over the layout pass's tokens, the spans of what it has read,
/// and the side state kept beside the tree (`ParseState`).
module Parser.Program.DescentCursor

open System
open Parser.Program.Lexer
open Parser.Program.Layout

/// A mistake no reading of the text gets past: it is reported as what was expected where the furthest reading failed
/// (see `Cursor.Unexpected`). `reason` says what the mistake is, for whoever reads the code.
exception GiveUp of reason: string

/// One way of reading the text doesn't fit what is there, and the cursor goes back to try the next. Only the parsers
/// that list the alternatives catch it (see `Cursor.Attempt`).
exception NoMatch

/// A mistake in the text the parser has a message of its own for, reported where it is: reading stops there.
exception Reported of ParseError

/// How a token reads in a message: its text in backticks, or the end of the file.
let private describeToken (source: string) (token: Token) =
    match token.Kind with
    | EndOfFile -> "the end of the file"
    | StringStart
    | StringLiteral -> "a string"
    | Number -> "a number"
    | _ -> $"`{source.Substring(token.Start, token.End - token.Start)}`"

/// The text of `token` as a syntax error's headline names what the parser found (see `ParseError.Found`): none at the end
/// of the file, or for the start of a string, which runs on past it.
let private foundText (source: string) (token: Token) =
    match token.Kind with
    | EndOfFile
    | StringStart -> None
    | _ -> Some $"`{source.Substring(token.Start, token.End - token.Start)}`"

/// The error at `span` that `expected`, a message saying what was expected, is not `token`, which is found there. A
/// token the headline can name is left out of the message; any other is said there, as "the end of the file".
let private expectedAt (source: string) (span: SourceSpan) (token: Token) (expected: string) : ParseError =
    match foundText source token with
    | Some _ as found -> { Message = expected; Span = span; Found = found }
    | None -> { Message = $"{expected}, found {describeToken source token}"; Span = span; Found = None }

/// How a token kind reads as what was expected.
let private describeKind (kind: TokenKind) =
    match kind with
    | Name -> "a name"
    | Number -> "a number"
    | StringLiteral -> "a string"
    | LeftParen -> "`(`"
    | RightParen -> "`)`"
    | LeftBracket -> "`[`"
    | RightBracket -> "`]`"
    | LeftBrace -> "`{`"
    | RightBrace -> "`}`"
    | Comma -> "`,`"
    | Colon -> "`:`"
    | Equals -> "`=`"
    | Arrow -> "`->`"
    | Bar -> "`|`"
    | Dot -> "`.`"
    | Hash -> "`#`"
    | other -> $"{other}"

/// A cursor over `laid`, the layout pass's tokens of `source`, the text of the file at `file`.
type Cursor(file: string, source: string, laid: Laid array) =
    let lines = LineTable source
    let mutable index = 0
    // The names the nodes declare and where documentable constructs start, as `ParseState` keeps them.
    let mutable declared: DeclaredNames = Map.empty
    let mutable documentable: Map<int, int> = Map.empty
    // The names noted for a construct not built yet, most recent first.
    let mutable pending: (Identifier * SourceSpan) list = []
    // Where the text of each string literal read so far is, most recent first, as `ParseState.StringTexts` keeps it.
    let mutable stringTexts: (int * int) list = []
    // Where the argument a shorthand lambda's body leaves out is made up (see `ImplicitArgumentNext`).
    let mutable implicitArgumentNext: SourceSpan option = None
    // The `module name =` blocks read so far, each with its text, as `ParseState.ModuleBlocks` keeps them.
    let mutable moduleBlocks: (ModuleName * SourceSpan) list = []
    // The furthest item any reading reached or failed at, and what the readings that failed there expected, for the
    // message when no reading of the text matches.
    let mutable furthest = 0
    // How many lambdas the cursor is inside.
    let mutable lambdaDepth = 0
    let mutable expected: string list = []
    // How many code tokens have been taken, a token read again after a reading that didn't match counted again: the
    // parser's work, which grows with the text when each part of it is read a few times at most.
    let mutable tokensRead = 0

    member _.Source = source
    member _.Index
        with get () = index
        and set value = index <- value

    /// The item at `offset` from the cursor, a code token or a layout token. Before the first there is a line break.
    member _.At(offset: int) =
        if index + offset < 0 then Newline 0
        elif index + offset < laid.Length then laid[index + offset]
        else laid[laid.Length - 1]

    /// The next code token, if the cursor is at one, not at a layout token.
    member this.Token =
        match this.At 0 with
        | Code token -> Some token
        | _ -> None

    /// The text of `token`.
    member _.Text(token: Token) = source.Substring(token.Start, token.End - token.Start)

    /// Whether the cursor is at a code token of `kind`, with the text `text` if one is given.
    member this.Is(kind: TokenKind, ?text: string) =
        match this.Token with
        | Some token -> token.Kind = kind && (text.IsNone || this.Text token = text.Value)
        | None -> false

    member this.IsKeyword(word: string) = this.Is(Keyword, word)

    /// Whether the code token at the cursor follows the one before it with nothing between them.
    member this.Adjacent =
        match this.Token with
        | Some token when index > 0 ->
            match laid[index - 1] with
            | Code previous -> previous.End = token.Start
            | _ -> false
        | _ -> false

    /// Whether the code token at the cursor is on the line the one before it ends on.
    member this.OnSameLine = this.OnSameLineAt 0

    /// Whether the code token `offset` tokens on from the cursor is on the line the one before it ends on.
    member this.OnSameLineAt (offset: int) =
        match this.At offset with
        | Code token when index + offset > 0 ->
            match laid[index + offset - 1] with
            | Code previous -> not (source.AsSpan(previous.End, token.Start - previous.End).Contains '\n')
            | _ -> false
        | _ -> false

    /// Takes the code token at the cursor.
    member this.Take() =
        match this.Token with
        | Some token ->
            index <- index + 1
            tokensRead <- tokensRead + 1
            if index > furthest then
                furthest <- index
                expected <- []
            token
        | None -> raise NoMatch

    /// Notes that a reading expected `description` at the cursor, for the message if no reading matches.
    member _.Expecting(description: string) =
        if index > furthest then
            furthest <- index
            expected <- [ description ]
        elif index = furthest && not (List.contains description expected) then
            expected <- expected @ [ description ]

    /// Fails to match, noting that `description` was expected at the cursor.
    member this.Fail(description: string) : 'a =
        this.Expecting description
        raise NoMatch

    /// Takes the code token at the cursor if it is `kind` (with the text `text`), or fails to match.
    member this.Expect(kind: TokenKind, ?text: string) =
        if this.Is(kind, ?text = text) then this.Take()
        else this.Fail(match text with Some text -> $"`{text}`" | None -> describeKind kind)

    /// Reports the mistake `message` at `offset` (see `Reported`).
    member _.Report (offset: int) (message: string) : 'a =
        raise (Reported { Message = message; Span = lines.Span file offset (min source.Length (offset + 1)); Found = None })

    /// Reports the mistake `message` at the text from `start` to `finish` (see `Reported`).
    member _.ReportBetween (start: int) (finish: int) (message: string) : 'a =
        raise (Reported { Message = message; Span = lines.Span file start finish; Found = None })

    /// Reports `expected` at `token`, which is not it: the message says what was expected, and the error names the
    /// token found (see `ParseError.Found`).
    member _.ReportFound (token: Token) (expected: string) : 'a =
        raise (Reported (expectedAt source (lines.Span file token.Start (min source.Length (max (token.Start + 1) token.End))) token expected))

    /// The message for text no reading matches: what was found at the furthest item reached, and what was expected
    /// there, if anything was noted.
    member _.Unexpected() : ParseError =
        let at = min furthest (laid.Length - 1)
        let mutable i = at
        while i < laid.Length - 1 && (match laid[i] with Code _ -> false | _ -> true) do
            i <- i + 1
        let token = match laid[i] with Code token -> token | _ -> { Kind = EndOfFile; Start = source.Length; End = source.Length; Leading = []; Trailing = [] }
        let span = lines.Span file token.Start (min source.Length (max (token.Start + 1) token.End))
        match expected with
        | [] when token.Kind = EndOfFile -> { Message = "Unexpected end of the file"; Span = span; Found = None }
        | [] -> { Message = "It can't come here"; Span = span; Found = foundText source token }
        | [ single ] -> expectedAt source span token $"Expected {single}"
        | several -> expectedAt source span token $"""Expected {String.concat ", " (List.take (several.Length - 1) several)} or {List.last several}"""

    /// Takes the code token at the cursor if it is `kind` (with the text `text`), and says whether it did.
    member this.Skip(kind: TokenKind, ?text: string) =
        if this.Is(kind, ?text = text) then
            index <- index + 1
            true
        else
            false

    /// Skips the `Newline` and `Indent` tokens at the cursor when a code token `fits` comes after them, going across line
    /// breaks to what comes next, and says whether that token is next now.
    member this.Across(fits: Cursor -> bool) =
        let start = index
        while (match this.At 0 with Newline _ | Indent _ -> true | _ -> false) do
            index <- index + 1
        if fits this then true
        else
            index <- start
            false

    /// The end of the last code token taken.
    member _.LastEnd =
        let mutable i = index - 1
        while i >= 0 && (match laid[i] with Code _ -> false | _ -> true) do
            i <- i - 1
        match if i >= 0 then laid[i] else Code { Kind = EndOfFile; Start = 0; End = 0; Leading = []; Trailing = [] } with
        | Code token -> token.End
        | _ -> 0

    /// Where the `--` comment after the last code token taken, on its line, ends, if there is one.
    member _.TrailingCommentEnd =
        let mutable i = index - 1
        while i >= 0 && (match laid[i] with Code _ -> false | _ -> true) do
            i <- i - 1
        match if i >= 0 then Some laid[i] else None with
        | Some(Code token) -> token.Trailing |> List.tryPick (fun trivia -> if trivia.Kind = LineComment then Some trivia.End else None)
        | _ -> None

    /// The span from `start` to the end of the last code token taken.
    member this.SpanFrom(start: int) = lines.Span file start (max start this.LastEnd)

    /// The span of `token`.
    member _.SpanOf(token: Token) = lines.Span file token.Start token.End

    /// Where the code token at the cursor starts.
    member this.Start =
        match this.Token with
        | Some token -> token.Start
        | None -> raise NoMatch

    /// Runs `read`, and if it doesn't match, puts the cursor back and gives `None`.
    member _.Attempt(read: unit -> 'a) : 'a option =
        let start = index
        let declaredBefore, documentableBefore, pendingBefore, stringTextsBefore = declared, documentable, pending, stringTexts
        let implicitBefore, moduleBlocksBefore, lambdaDepthBefore = implicitArgumentNext, moduleBlocks, lambdaDepth
        try
            Some(read ())
        with NoMatch ->
            index <- start
            declared <- declaredBefore
            documentable <- documentableBefore
            pending <- pendingBefore
            stringTexts <- stringTextsBefore
            implicitArgumentNext <- implicitBefore
            moduleBlocks <- moduleBlocksBefore
            lambdaDepth <- lambdaDepthBefore
            None

    member _.Declared = declared

    member _.ModuleBlocks = moduleBlocks

    member _.LambdaDepth
        with get () = lambdaDepth
        and set value = lambdaDepth <- value

    /// Notes the `module name =` block `name`, whose text is at `span`.
    member _.NoteModuleBlock (name: ModuleName) (span: SourceSpan) = moduleBlocks <- moduleBlocks @ [ name, span ]

    /// The comments of the text, most recent first, as `ParseState.Comments` keeps them: each `--` comment, block comment
    /// and doc comment, from the trivia of the tokens.
    member _.Comments : NotedComment list =
        laid
        |> Seq.collect (function Code token -> Seq.append token.Leading token.Trailing | _ -> Seq.empty)
        |> Seq.choose (fun trivia ->
            match trivia.Kind with
            | LineComment
            | BlockComment -> Some { NotedComment.Start = trivia.Start; End = trivia.End; IsDoc = false }
            | DocComment -> Some { NotedComment.Start = trivia.Start; End = trivia.End; IsDoc = true }
            | Whitespace
            | LineBreak
            | Directive -> None)
        |> Seq.distinct
        |> Seq.rev
        |> List.ofSeq

    /// Where the argument a shorthand lambda's body leaves out is made up, until its first operand takes it: the operator,
    /// `.` or `\` written in its place.
    member _.ImplicitArgumentNext
        with get () = implicitArgumentNext
        and set value = implicitArgumentNext <- value
    member _.StringTexts = stringTexts

    /// How many code tokens have been taken (see `ParseState.TokensRead`).
    member _.TokensRead = tokensRead

    /// Notes `name`, written at `span`, as declared by the construct being read (see `Collecting`).
    member _.NoteName (name: Identifier) (span: SourceSpan) = pending <- (name, span) :: pending

    /// Notes that the text of a string literal is from `start` to `finish` (see `ParseState.StringTexts`).
    member _.NoteStringText (start: int) (finish: int) = stringTexts <- (start, finish) :: stringTexts

    /// Runs `read`, and gives with what it read the names noted while it ran, in the order they are written, which are
    /// no longer pending.
    member _.Collecting(read: unit -> 'a) : 'a * (Identifier * SourceSpan) list =
        let before = pending
        let value = read ()
        let noted = pending |> List.take (pending.Length - before.Length) |> List.rev
        pending <- before
        value, noted
    member _.DocumentableStarts = documentable

    /// Records that `holder` declares `names` as `declarer` (see `ParseState.declare`).
    member _.Declare (declarer: Declarer) (holder: NodeId) (names: (Identifier * SourceSpan) list) =
        if not names.IsEmpty then
            let key = holder, declarer
            let existing = declared |> Map.tryFind key |> Option.defaultValue []
            declared <- declared |> Map.add key (existing @ names |> List.distinctBy fst)

    /// Notes that a documentable construct starts at `start` and declares what starts at `target` (see
    /// `ParseState.noteDocumentable`).
    member _.NoteDocumentable (start: int) (target: int) = documentable <- documentable |> Map.add start target

    /// Stops at a mistake (see `GiveUp`).
    member _.GiveUp(reason: string) : 'a = raise (GiveUp reason)

/// An identifier, with its span: a name, or a keyword, since `#match` is a tag and a field may be called `type`.
let identifierWithSpan (c: Cursor) : Identifier * SourceSpan =
    match c.Token with
    | Some token when token.Kind = Name || token.Kind = Keyword ->
        c.Take() |> ignore
        c.Text token, c.SpanOf token
    | _ -> raise NoMatch

/// A name, with the span of what is written: an identifier, or a name in backticks, which is the text inside them.
let name (c: Cursor) : Identifier * SourceSpan =
    match c.Token with
    | Some token when token.Kind = BacktickedName ->
        let written = c.Text token
        let inner = written.Substring(1, written.Length - 2)
        if Names.isReservedOperatorSpelling inner then c.Report token.Start $"'{inner}' is syntax, not an operator, so it cannot be a name"
        if String.IsNullOrWhiteSpace inner then c.Report token.Start "A name in backticks cannot be blank"
        c.Take() |> ignore
        inner, c.SpanOf token
    | _ -> identifierWithSpan c

/// The parts of the grammar a file reads before the file that reads them, filled by `DescentControl.fill` before anything
/// is read. Each gives up until it is filled.
module Forward =
    let private unfilled (what: string) (c: Cursor) : 'a = c.GiveUp $"{what}, before the descent parser is filled"
    /// `{ ... }` (`DescentBlocks.lambda`).
    let lambda: (Cursor -> Expression) ref = ref (unfilled "a lambda")
    /// An expression (`DescentExpressions.expression Crossing`).
    let expression: (Cursor -> Expression) ref = ref (unfilled "an expression")
    /// A match's arms, from the first `|` (`DescentBlocks.arms`).
    let arms: (Cursor -> MatchArm list) ref = ref (unfilled "match arms")
    /// What follows `except`: arms, or one arm without its `|` (`DescentBlocks.exceptArms`).
    let exceptArms: (Cursor -> MatchArm list) ref = ref (unfilled "`except` arms")
    /// An attribute's argument, written straight after its name (`DescentExpressions.attributeArgument`).
    let attributeArgument: (Cursor -> Expression) ref = ref (unfilled "an attribute's argument")
    /// A type's or a context's definition (`DescentTopLevel.typeDefinition`).
    let typeDefinition: (Cursor -> Definition) ref = ref (unfilled "a type definition")
    /// What follows `import`: one item, or several in brackets (`DescentTopLevel.importItems`).
    let importItems: (Cursor -> ImportItem list) ref = ref (unfilled "an import")
    /// An expression that starts with a keyword: `if`, `match`, `for`, `while`, `do`, `use ... in`, `return`, `try`,
    /// `yield`, `mut x` and `type T` (`DescentControl.keywordExpression`).
    let keywordExpression: (Cursor -> Expression) ref = ref (unfilled "a keyword's expression")

/// Reports the first backslash in `text`, a double-quoted string's text that starts at `offset`, that isn't an escape,
/// with what the escapes are.
let reportBadEscape (c: Cursor) (offset: int) (text: string) : 'a =
    let mutable i = 0
    let mutable found = None
    while found.IsNone && i < text.Length do
        if text[i] = '\\' then
            let next = if i + 1 < text.Length then Some text[i + 1] else None
            let codePoint () =
                if next = Some 'u' && i + 2 < text.Length && text[i + 2] = '{' then
                    let close = text.IndexOf('}', i + 3)
                    if close > 0 then StringEscapes.tryCodePoint (text.Substring(i + 3, close - i - 3)) |> Option.map (fun _ -> close + 1) else None
                else None
            match next with
            | Some n when StringEscapes.simple |> List.exists (fst >> (=) n) -> i <- i + 2
            | _ ->
                match codePoint () with
                | Some after -> i <- after
                | None ->
                    let message =
                        match next with
                        | Some 'u' -> "`\\u{...}` is a code point in hex, from 0 to 10FFFF, and not a surrogate"
                        | Some n when not (Char.IsControl n) -> $"`\\{n}` is not an escape"
                        | _ -> "A backslash on its own is not an escape"
                    found <- Some(offset + i, message)
        else i <- i + 1
    match found with
    | Some(at, message) ->
        c.Report at
            $"{message}: the escapes in a double-quoted string are {StringEscapes.written}, so write a backslash as `\\\\`, or use a single-quoted string, which is raw"
    | None -> c.GiveUp "a string whose escapes don't decode"

/// Reports a `$` before a type's name at the cursor, the way a nominal type's name was written before `protected`
/// declared one (`ParseState.typeNameIdentifier`).
let noDollar (c: Cursor) =
    if c.Is Dollar then
        match c.At 1 with
        | Code next when next.Kind = Name && next.Start = c.Start + 1 ->
            let name = c.Source.Substring(next.Start, next.End - next.Start)
            c.Report c.Start
                $"A type's name is written without a `$`: write `{name}`, and declare a nominal type with `protected` after its `=`, as in `type {name} = protected string`"
        | _ -> ()

/// Takes `item`s separated by what `separator` takes, at least one, going back to before the separator when the item
/// after it doesn't match.
let sepBy1 (cursor: Cursor) (item: unit -> 'a) (separator: unit -> unit) : 'a list =
    let first = item ()
    let rest = ResizeArray<'a>()
    let mutable going = true
    while going do
        match cursor.Attempt(fun () -> separator (); item ()) with
        | Some next -> rest.Add next
        | None -> going <- false
    first :: List.ofSeq rest

/// Takes as many `item`s as there are, going back to before each one that doesn't match.
let many (cursor: Cursor) (item: unit -> 'a) : 'a list =
    let items = ResizeArray<'a>()
    let mutable going = true
    while going do
        match cursor.Attempt item with
        | Some next -> items.Add next
        | None -> going <- false
    List.ofSeq items

/// The offset from the cursor of the `)` that closes the `(` at `offset`, if the text closes it. Lets a reading look past
/// brackets before it commits to reading what is in them twice.
let closingParen (c: Cursor) (offset: int) : int option =
    let mutable i = offset + 1
    let mutable depth = 1
    let mutable found = None
    while found.IsNone && depth > 0 do
        match c.At i with
        | Code token when token.Kind = EndOfFile -> depth <- 0
        | Code token when token.Kind = LeftParen -> depth <- depth + 1
        | Code token when token.Kind = RightParen ->
            depth <- depth - 1
            if depth = 0 then found <- Some i
        | _ -> ()
        i <- i + 1
    found

/// A cursor over the layout of `source`, the text of the file at `file`.
let cursorOf (file: string) (source: string) =
    let lexed = Lexer.lex file source
    if not lexed.Errors.IsEmpty then raise (Reported lexed.Errors.Head)
    Cursor(file, source, Layout.layout source lexed.Tokens)

/// `span`, ending no later than `last` does when `last` has a span of its own.
let internal endingAt (last: SourceSpan) (span: SourceSpan) =
    if not (SourceSpan.isNone last) && last.End < span.End then { span with End = last.End } else span
