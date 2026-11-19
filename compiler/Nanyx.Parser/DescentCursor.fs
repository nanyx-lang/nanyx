/// What the recursive-descent parser reads with: a cursor over the layout pass's tokens, the spans of what it has read,
/// and the side state the FParsec parser keeps (`ParseState`). It is stage 3 of the move to a lexer, a layout pass and a
/// recursive-descent parser (see the Work in progress page "The parser"). The new parser reads only part of the grammar
/// so far; at anything else, and at any mistake, it gives up, and the FParsec parser reads the file instead, so that
/// what a user sees doesn't change until stage 4.
module Parser.Program.DescentCursor

open System
open Parser.Program.Lexer
open Parser.Program.Layout

/// The new parser can't read what is here, because it doesn't read that part of the grammar yet or because it is a
/// mistake: the FParsec parser reads the file instead. `reason` says which, for the corpus test's report.
exception GiveUp of reason: string

/// One way of reading the text doesn't fit what is there, and the cursor goes back to try the next, as FParsec's
/// `attempt` and `<|>` do. Only the parsers that list the alternatives catch it (see `Cursor.Attempt`).
exception NoMatch

/// A cursor over `laid`, the layout pass's tokens of `source`, the text of the file at `file`.
type Cursor(file: string, source: string, laid: Laid array) =
    let lines = LineTable source
    let mutable index = 0
    // The names the nodes declare and where documentable constructs start, as `ParseState` keeps them.
    let mutable declared: DeclaredNames = Map.empty
    let mutable documentable: Map<int, int> = Map.empty

    member _.Source = source
    member _.Index
        with get () = index
        and set value = index <- value

    /// The item at `offset` from the cursor, a code token or a layout token.
    member _.At(offset: int) = if index + offset < laid.Length then laid[index + offset] else laid[laid.Length - 1]

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
    member this.OnSameLine =
        match this.Token with
        | Some token when index > 0 ->
            match laid[index - 1] with
            | Code previous -> not (source.AsSpan(previous.End, token.Start - previous.End).Contains '\n')
            | _ -> false
        | _ -> false

    /// Takes the code token at the cursor.
    member this.Take() =
        match this.Token with
        | Some token ->
            index <- index + 1
            token
        | None -> raise NoMatch

    /// Takes the code token at the cursor if it is `kind` (with the text `text`), or fails to match.
    member this.Expect(kind: TokenKind, ?text: string) =
        if this.Is(kind, ?text = text) then this.Take() else raise NoMatch

    /// Takes the code token at the cursor if it is `kind` (with the text `text`), and says whether it did.
    member this.Skip(kind: TokenKind, ?text: string) =
        if this.Is(kind, ?text = text) then
            index <- index + 1
            true
        else
            false

    /// Skips the `Newline` and `Indent` tokens at the cursor when a code token `fits` comes after them, as FParsec's `ws`
    /// goes across line breaks to what comes next, and says whether that token is next now.
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

    /// The span from `start` to the end of the last code token taken.
    member this.SpanFrom(start: int) = lines.Span file start (max start this.LastEnd)

    /// The span of `token`.
    member _.SpanOf(token: Token) = lines.Span file token.Start token.End

    /// Where the code token at the cursor starts.
    member this.Start =
        match this.Token with
        | Some token -> token.Start
        | None -> raise NoMatch

    /// Runs `read`, and if it doesn't match, puts the cursor back and gives `None`, as FParsec's `attempt` does.
    member _.Attempt(read: unit -> 'a) : 'a option =
        let start = index
        let declaredBefore, documentableBefore = declared, documentable
        try
            Some(read ())
        with NoMatch ->
            index <- start
            declared <- declaredBefore
            documentable <- documentableBefore
            None

    member _.Declared = declared
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

    /// Gives up on the file (see `GiveUp`).
    member _.GiveUp(reason: string) : 'a = raise (GiveUp reason)

/// Takes `item`s separated by what `separator` takes, at least one, as FParsec's `sepBy1` with an `attempt` around the
/// separator and the item after it.
let sepBy1 (cursor: Cursor) (item: unit -> 'a) (separator: unit -> unit) : 'a list =
    let first = item ()
    let rest = ResizeArray<'a>()
    let mutable going = true
    while going do
        match cursor.Attempt(fun () -> separator (); item ()) with
        | Some next -> rest.Add next
        | None -> going <- false
    first :: List.ofSeq rest

/// Takes as many `item`s as there are, as FParsec's `many` with an `attempt` around each.
let many (cursor: Cursor) (item: unit -> 'a) : 'a list =
    let items = ResizeArray<'a>()
    let mutable going = true
    while going do
        match cursor.Attempt item with
        | Some next -> items.Add next
        | None -> going <- false
    List.ofSeq items

/// A cursor over the layout of `source`, the text of the file at `file`.
let cursorOf (file: string) (source: string) =
    let lexed = Lexer.lex file source
    if not lexed.Errors.IsEmpty then raise (GiveUp "the lexer found a mistake")
    Cursor(file, source, Layout.layout source lexed.Tokens)

/// Puts `cursor` at the code token that starts at `offset`, if there is one.
let seek (cursor: Cursor) (offset: int) =
    cursor.Index <- 0
    let mutable found = false
    while not found && (match cursor.At 0 with Code token -> token.Kind <> EndOfFile | _ -> true) do
        match cursor.At 0 with
        | Code token when token.Start = offset -> found <- true
        | _ -> cursor.Index <- cursor.Index + 1
    found
