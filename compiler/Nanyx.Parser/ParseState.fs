[<AutoOpen>]
module Parser.Program.ParseState

open System
open FParsec
// After the libraries, so that the parser's names shadow theirs (identifier, Attribute) as they did in one file.
open Parser.Program

/// A comment the parser skipped: where it starts and ends (from `--` to the end of the line, without the line break, or
/// to the end of a block comment's closing line or a doc comment's last line), and whether it is a doc comment.
type NotedComment =
    { Start: int
      End: int
      /// A doc comment: `--|` on a line of its own, and the lines after it that are indented further.
      IsDoc: bool }

/// What the parser keeps beside the input. FParsec restores it when it backtracks, so a declaration parsed on
/// a path that failed is forgotten with it.
type ParseState =
    { Declared: DeclaredNames
      /// Names parsed for a construct that has not been built yet (a lambda's parameters, a pattern's
      /// variables), most recent first. The construct takes them with `collectingNames`.
      PendingNames: (Identifier * SourceSpan) list
      /// How many lambdas the parser is inside.
      LambdaDepth: int
      /// When the next operand parsed is the argument a shorthand lambda's body leaves out (`{ % 2 == 0 }`), the
      /// span it is given: that of the operator, or the `.`, written in its place.
      ImplicitArgumentNext: SourceSpan option
      /// The text of each `module name =` block parsed so far, with the block's name.
      ModuleBlocks: (ModuleName * SourceSpan) list
      /// The furthest mistake the parser recognised (a workflow keyword applied to a value, or `use C(T) = ...`),
      /// where it is and what to say about it. It is a cell shared by every copy of the state, so backtracking does
      /// not forget it (see `reportFocused`).
      FocusedError: (Position * string) option ref
      /// The furthest failure inside brackets, `[...]` or `(...)`, after the opening one: where it is and what FParsec
      /// expected there. A cell shared by every copy of the state, as `FocusedError` is (see `noteFailureInside`).
      FailureInBrackets: (Position * ErrorMessageList) option ref
      /// The first operator written without a space on each side, where it is and what to say about it (see
      /// `noteMisspacedOperator`). A cell shared by every copy of the state, as `FocusedError` is: the operator is
      /// parsed as if it were spaced, and the parse fails with this afterwards, so that no other reading of the text,
      /// tried when this one is backtracked over, hides it.
      MisspacedOperator: (Position * string) option ref
      /// The comments skipped so far, most recent first. For the formatter, which puts them back (see
      /// `parseModuleWithTrivia`), and for doc comments (see `DocComments`).
      Comments: NotedComment list
      /// Where each construct a doc comment can document starts, from its first keyword (`export def name`, `| #tag`),
      /// with where the name or tag it declares starts (see `noteDocumentable`).
      DocumentableStarts: Map<int, int>
      /// The text of each string literal parsed so far (between the quotes, without any interpolation holes), as
      /// start and end offsets, most recent first. A blank line in one is part of the string, not of the layout.
      StringTexts: (int * int) list }

module ParseState =
    let empty () =
        { Declared = Map.empty
          PendingNames = []
          LambdaDepth = 0
          ImplicitArgumentNext = None
          ModuleBlocks = []
          FocusedError = ref None
          FailureInBrackets = ref None
          MisspacedOperator = ref None
          Comments = []
          DocumentableStarts = Map.empty
          StringTexts = [] }

/// Runs `p`, noting what it gave and the offsets of the text it consumed with `note`.
let private noting (note: 'a -> int * int -> ParseState -> ParseState) (p: Parser<'a, ParseState>) : Parser<'a, ParseState> =
    pipe3 getPosition p getPosition (fun start value finish -> value, (int start.Index, int finish.Index))
    >>= fun (value, range) -> updateUserState (note value range) >>% value

// Fails fatally at `position` with `message`, and notes the failure in the state, which `parseModuleFileWithNames`
// reports in place of FParsec's error when the parse fails: the failure is backtracked over, and FParsec would report
// some enclosing construct instead (often the whole def). Of two such failures, the furthest is reported.
let internal reportFocused (position: Position) (message: string) : Parser<'a, ParseState> =
    getUserState >>= fun state ->
        match state.FocusedError.Value with
        | Some (furthest, _) when furthest.Index >= position.Index -> ()
        | _ -> state.FocusedError.Value <- Some (position, message)
        failFatally message

// Runs `p`, which parses something in brackets, and notes in the state where it failed if it failed after taking the
// opening one, as `[1, 2` does. A block's statements are tried under `attempt`, so FParsec reports such a failure at
// the block's start, not in the brackets; `parseModuleFileWithNames` reports the noted failure instead when it is
// further on. Of two such failures, the furthest is noted, and of two at one place, the innermost.
let internal noteFailureInside (p: Parser<'a, ParseState>) : Parser<'a, ParseState> =
    fun stream ->
        let start = stream.Index
        let reply = p stream
        if reply.Status <> Ok && stream.Index > start then
            let failures = stream.UserState.FailureInBrackets
            match failures.Value with
            | Some (furthest, _) when furthest.Index >= stream.Index -> ()
            | _ -> failures.Value <- Some (stream.Position, reply.Error)
        reply

/// Whether only spaces and tabs come before the current position on its line.
let private atLineStart: Parser<unit, ParseState> =
    fun stream ->
        let mutable back = 1
        while stream.Peek(-back) = ' ' || stream.Peek(-back) = '\t' do
            back <- back + 1
        let previous = stream.Peek(-back)
        if previous = '\n' || previous = CharStream.EndOfStreamChar then Reply(()) else Reply(Error, NoErrorMessages)

/// A block comment's fence: a line holding only three or more dashes.
let private isFence (line: string) = line.Length >= 3 && line |> Seq.forall ((=) '-')

/// The rest of a doc comment after its first line: each line after it indented further than `indent` (the indent of
/// the line the `--|` is on, from the start of the line), and the blank lines between them. It stops before the line
/// break that ends the last such line, so blank lines after the comment are not part of it.
let private docCommentRest (indent: int) : Parser<unit, ParseState> =
    fun stream ->
        let isSpace (offset: int) = stream.Peek(offset) = ' ' || stream.Peek(offset) = '\t'
        let isLineEnd (offset: int) = stream.Peek(offset) = '\n' || stream.Peek(offset) = CharStream.EndOfStreamChar
        // `offset` is at the line break before the line being looked at, and `taken` is where the comment ends so far.
        let mutable offset = 0
        let mutable taken = 0
        while stream.Peek(offset) = '\n' do
            let lineStart = offset + 1
            let mutable text = lineStart
            while isSpace text do
                text <- text + 1
            let mutable lineEnd = text
            while not (isLineEnd lineEnd) do
                lineEnd <- lineEnd + 1
            if isLineEnd text then offset <- lineEnd
            elif text - lineStart > indent then
                taken <- lineEnd
                offset <- lineEnd
            else offset <- -1
        stream.SkipCharsOrNewlines taken |> ignore
        Reply(())

// Helper: whitespace and comment handling
/// A comment: `--` to the end of the line, a block comment or a doc comment. A line holding only three or more dashes
/// opens a block comment, which runs to the next line holding exactly the same dashes and ends with it. `--- text` is a
/// `--` comment. A line that starts with `--|` opens a doc comment, which goes on for the lines after it that are
/// indented further (see `docCommentRest`); a `--|` after code on its line is a `--` comment.
let private lineComment () =
    let blockRest (start: Position) (fence: string) =
        let closing = attempt (skipNewline >>. restOfLine false >>= fun line -> if line.Trim() = fence then preturn () else pzero)
        attempt (skipManyTill (skipNewline >>. skipRestOfLine false) closing)
        <|> reportFocused start $"This block comment is never closed: end it with a line of exactly the same dashes, {fence}"
    let comment =
        pipe3 getPosition (opt atLineStart) (pstring "--" >>. restOfLine false) (fun start ownLine rest -> start, ownLine.IsSome, "--" + rest.TrimEnd())
        >>= fun (start, ownLine, line) ->
            if ownLine && isFence line then blockRest start line >>% false
            elif ownLine && line.StartsWith "--|" then docCommentRest (int start.Column - 1) >>% true
            else preturn false
    noting (fun isDoc (start, finish) state -> { state with Comments = { NotedComment.Start = start; End = finish; IsDoc = isDoc } :: state.Comments }) comment
    >>% ()

let comment () = lineComment () >>. optional skipNewline
let inlineCommentNoNl () = lineComment ()
let ws: Parser<unit, ParseState> = skipMany (skipMany1 spaces1 <|> comment ())
/// Spaces and tabs, but no newline.
let wsInline = skipMany (skipAnyOf " \t")

/// After a keyword: the word ends there, so `typeName` is a name and not `type` then `Name`.
let keywordEnd: Parser<unit, ParseState> = notFollowedBy (satisfy (fun c -> System.Char.IsLetterOrDigit c || c = '_'))

/// The end of a line in an indentation-based block: an optional `-- comment`, then the newline.
let blockLineEnd () : Parser<unit, ParseState> =
    wsInline >>. optional (inlineCommentNoNl ()) >>. skipNewline

/// The end of a line in an indentation-based block, then any lines that are empty, whitespace only or a comment, so
/// the next parser starts at the beginning of the next line with code. Used between a block's statements, inside an
/// `attempt`: if what follows is not at the block's indent, the block ends at its last statement and the comments
/// skipped here are forgotten, to be noted again by whatever parses them next.
let blockLinesEnd () : Parser<unit, ParseState> =
    blockLineEnd () >>. skipMany (attempt (blockLineEnd ()))

let newlineIndentedContinuation (refCol: int64) : Parser<unit, ParseState> =
    attempt (
        skipMany (skipAnyOf " \t") >>. newline >>.
        many (pchar ' ' <|> pchar '\t') >>.
        getPosition >>= fun pos ->
            if pos.Column > refCol then preturn () else pzero
    )

let defEqualsContinuation (refCol: int64) : Parser<unit, ParseState> =
    attempt (wsInline >>. followedBy (pstring "=")) <|> newlineIndentedContinuation refCol

/// A character that can continue an identifier. A word such as `true` is only that word when none follows it.
let isIdentifierChar c = isLetter c || isDigit c || c = '_'

// Parser for identifiers
let identifierNoWs: Parser<Identifier, ParseState> =
    let isIdentStart c = isLetter c || c = '_'
    many1Satisfy2 isIdentStart isIdentifierChar
    <?> "identifier"

let identifier: Parser<Identifier, ParseState> = identifierNoWs .>> wsInline

/// A name in backticks, for a name that is not an identifier: an operator, `` def Point.`==` = ... ``, or a sentence,
/// `` def `adds points` = ... ``. The name is the text without its backticks, and is anything but a backtick, a line
/// break or a dot (see `Names.isBacktickedChar`). Spellings made of operator characters that are syntax (`=`, `->`,
/// `=>`, and `--`, which starts a comment) cannot be names, and nor can blank text.
let backtickedNameNoWs: Parser<Identifier, ParseState> =
    let quoted = between (pchar '`') (pchar '`') (many1Satisfy Names.isBacktickedChar) <?> "name in backticks"
    quoted >>= fun name ->
        // Fatal, so the error is this one and not whatever an enclosing alternative fails with after backtracking.
        if Names.isReservedOperatorSpelling name then failFatally $"'{name}' is syntax, not an operator, so it cannot be a name"
        elif String.IsNullOrWhiteSpace name then failFatally "A name in backticks cannot be blank"
        else preturn name

/// A name that may be an identifier or a name in backticks.
let nameNoWs: Parser<Identifier, ParseState> = identifierNoWs <|> backtickedNameNoWs

/// A type's name as it is declared, and whether it is written with the `$` that declared a protected type before
/// `protected` did (`type $Email = string`). The `$` is not part of the name.
let declaredTypeName: Parser<Identifier * bool, ParseState> =
    opt (pchar '$') .>>. identifierNoWs |>> fun (dollar, name) -> name, dollar.IsSome

/// A type's name. A `$` before it, which a protected type's name was written with before `protected` declared one, is
/// read and is not part of the name: `$Email` is `Email`.
let typeNameIdentifier: Parser<Identifier, ParseState> =
    declaredTypeName |>> fst

/// A name qualified with dots, any part of which may be a type's, and whose parts may be names in backticks:
/// `m.Email.parse`, `` Point.`+` ``.
let qualifiedIdentifier: Parser<Identifier, ParseState> =
    pipe2
        (typeNameIdentifier <|> backtickedNameNoWs)
        (many (pchar '.' >>. (typeNameIdentifier <|> backtickedNameNoWs)))
        (fun head tail -> String.concat "." (head :: tail))

let internal spanOf (start: Position) (endIndex: int) : SourceSpan =
    { File = start.StreamName
      Start = int start.Index
      End = max endIndex (int start.Index)
      StartLine = int start.Line - 1
      StartColumn = int start.Column - 1 }

/// The index just past the last character consumed that is not whitespace. Many parsers consume the whitespace
/// after what they parse; it is not part of a node's span.
let private trimmedEnd: Parser<int, ParseState> =
    fun stream ->
        let mutable back = 0
        let isTrailingSpace (offset: int) =
            let c = stream.Peek(offset)
            c <> CharStream.EndOfStreamChar && Char.IsWhiteSpace c
        while isTrailingSpace (-(back + 1)) do
            back <- back + 1
        Reply(int stream.Index - back)

/// Runs `p` and pairs its result with the span of the text it consumed.
let withSpan (p: Parser<'a, ParseState>) : Parser<'a * SourceSpan, ParseState> =
    pipe3 getPosition p trimmedEnd (fun start value endIndex -> value, spanOf start endIndex)

/// The span of `name`, written from `start` on one line, with its backticks if it has them.
let internal nameSpan (start: Position) (name: Identifier) = spanOf start (int start.Index + (Names.written name).Length)

/// The span of `name`, written at `start` and already read: as `nameSpan`, but counting a `$` written before any of its
/// parts (`$Email.parse`), which is not part of the name (see `typeNameIdentifier`).
let internal writtenNameSpan (start: Position) (name: Identifier) : Parser<SourceSpan, ParseState> =
    fun stream ->
        let back = int (stream.Index - start.Index)
        let length = (Names.written name).Length
        let mutable offset = 0
        let mutable read = 0
        while read < length do
            let partStart = offset = 0 || stream.Peek(offset - 1 - back) = '.'
            if not (partStart && stream.Peek(offset - back) = '$') then read <- read + 1
            offset <- offset + 1
        Reply(spanOf start (int start.Index + offset))

/// Notes `name`, written at `start`, as declared by the construct being parsed: see `collectingNames`.
let internal noteName (start: Position) (name: Identifier) : Parser<unit, ParseState> =
    updateUserState (fun state -> { state with PendingNames = (name, nameSpan start name) :: state.PendingNames })

/// Runs `p`, and returns with its result the names noted while it ran, in the order they are written. They are
/// no longer pending afterwards.
let internal collectingNames (p: Parser<'a, ParseState>) : Parser<'a * (Identifier * SourceSpan) list, ParseState> =
    getUserState >>= fun before ->
        p >>= fun value ->
            getUserState >>= fun after ->
                let noted =
                    after.PendingNames
                    |> List.take (after.PendingNames.Length - before.PendingNames.Length)
                    |> List.rev
                setUserState { after with PendingNames = before.PendingNames } >>% (value, noted)

/// Records that `holder` declares `names` as `declarer`. A name declared twice, by the alternatives of an
/// or-pattern, keeps the span of the first.
let internal declare (declarer: Declarer) (holder: NodeId) (names: (Identifier * SourceSpan) list) : Parser<unit, ParseState> =
    if List.isEmpty names then
        preturn ()
    else
        updateUserState (fun state ->
            let key = holder, declarer
            let existing = state.Declared |> Map.tryFind key |> Option.defaultValue []
            { state with Declared = state.Declared |> Map.add key (existing @ names |> List.distinctBy fst) })

/// Notes that a construct a doc comment can document starts at `start` and declares the name or tag that starts at
/// the offset `target`: a doc comment just before `start` documents that (see `DocComments.attach`). A construct that
/// starts at what it declares, as a record field does, needs no note.
let internal noteDocumentable (start: Position) (target: int) : Parser<unit, ParseState> =
    updateUserState (fun state -> { state with DocumentableStarts = state.DocumentableStarts |> Map.add (int start.Index) target })

/// Runs `p` and makes what it built an expression spanning the text it consumed.
let spanned (p: Parser<ExpressionKind, ParseState>) : Parser<Expression, ParseState> =
    withSpan p |>> fun (kind, span) -> Expression.at span kind

/// Runs `p` and makes what it built a pattern spanning the text it consumed.
let internal spannedPattern (p: Parser<PatternKind, ParseState>) : Parser<Pattern, ParseState> =
    withSpan p |>> fun (kind, span) -> Pattern.at span kind

/// Runs `p` and makes what it built a type expression spanning the text it consumed.
let internal spannedType (p: Parser<TypeExprKind, ParseState>) : Parser<TypeExpr, ParseState> =
    withSpan p |>> fun (kind, span) -> TypeExpr.at span kind

/// Runs `p`, which parses one type or several; one stays as it is, and several are made into the type `combine`
/// builds from them, spanning the text `p` consumed.
let internal singleOrCombined (combine: TypeExpr list -> TypeExprKind) (p: Parser<TypeExpr list, ParseState>) : Parser<TypeExpr, ParseState> =
    withSpan p |>> fun (items, span) ->
        match items with
        | [ single ] -> single
        | multiple -> TypeExpr.at span (combine multiple)

let identifierWithSpan: Parser<Identifier * SourceSpan option, ParseState> =
    withSpan identifierNoWs |>> fun (name, span) -> name, Some span

let qualifiedIdentifierWithSpan: Parser<Identifier * SourceSpan option, ParseState> =
    withSpan qualifiedIdentifier |>> fun (name, span) -> name, Some span

/// One expression, or several separated by commas as a tuple of them.
let internal singleOrTuple (items: Parser<Expression list, ParseState>) : Parser<Expression, ParseState> =
    withSpan items |>> fun (exprs, span) ->
        match exprs with
        | [single] -> single
        | multiple -> Expression.at span (TupleExpr multiple)

/// `span`, ending no later than `last` does when `last` has a span of its own.
let internal endingAt (last: SourceSpan) (span: SourceSpan) =
    if not (SourceSpan.isNone last) && last.End < span.End then { span with End = last.End } else span

/// Runs `p` and makes what it built a statement spanning the text it consumed. A statement that ends with an
/// expression ends where that expression does: it may have consumed a trailing `-- comment`, which is not part of
/// the statement.
let internal spannedStatement (p: Parser<StatementKind, ParseState>) : Parser<Statement, ParseState> =
    withSpan p |>> fun (kind, span) ->
        let span =
            match Statement.finalExpression kind with
            | Some expr -> endingAt expr.Span span
            | None -> span
        Statement.at span kind

/// A block of statements. It ends where its last statement does.
let internal blockSpanned (statements: Parser<Statement list, ParseState>) : Parser<Expression, ParseState> =
    withSpan statements |>> fun (stmts, span) ->
        let span =
            match List.tryLast stmts with
            | Some last -> endingAt last.Span span
            | None -> span
        Expression.at span (Block stmts)


/// Runs `p`, which parses text inside a string literal, and notes where that text is.
let internal stringText (p: Parser<string, ParseState>) =
    noting (fun _ range state -> { state with StringTexts = range :: state.StringTexts }) p
