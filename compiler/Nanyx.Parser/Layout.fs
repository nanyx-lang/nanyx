/// The layout pass: the lexer's tokens in, the same tokens out with the block structure that indentation gives them
/// made explicit, as `Indent`, `Newline` and `Dedent`, which the recursive-descent parser reads. Its rules are the
/// numbered list on the Deep dives page "The parser"; each is marked here where it is applied.
module Parser.Program.Layout

open Parser.Program.Lexer

/// A token of the laid-out stream: one of the lexer's, or a layout token at the offset of the code token after it.
type Laid =
    | Code of Token
    /// A block starts: the code token after it starts the block's first item.
    | Indent of at: int
    /// The next item of the innermost block starts at the code token after it.
    | Newline of at: int
    /// The innermost block ends before the token at `at`.
    | Dedent of at: int

/// What the pass is inside, innermost first.
type private Context =
    /// A block whose items start at this column (counted from 0), and whether they are a match's arms.
    | Block of column: int * arms: bool
    /// Brackets: `(`, `[`, or a string's holes, which close with the token of this kind.
    | Bracket of closer: TokenKind
    /// Braces: a lambda or a block in `{` and `}`.
    | Brace

let private isKeyword (source: string) (words: string list) (token: Token) =
    token.Kind = Keyword && List.contains (source.Substring(token.Start, token.End - token.Start)) words

/// Whether `token`, at the end of its line, opens a block on the lines after it (rule 2). A keyword written straight
/// after `#` or `.` is a name, a tag's or a field's (`#match`), and opens nothing.
let private opensBlock (source: string) (token: Token) =
    match token.Kind with
    | Equals
    | Arrow
    | LeftBrace -> true
    | _ ->
        let isName = token.Start > 0 && (source[token.Start - 1] = '#' || source[token.Start - 1] = '.')
        not isName && isKeyword source [ "then"; "else"; "do"; "in"; "match" ] token

/// Whether a line that starts with `token` at a block's column, after a line that ends with `previous`, continues the
/// item before it (rule 5).
let private continuesItem (source: string) (previous: Token) (token: Token) =
    isKeyword source [ "then"; "else" ] token || isKeyword source [ "in" ] previous

let private isCloser (token: Token) =
    match token.Kind with
    | RightParen
    | RightBracket
    | RightBrace
    | StringMiddle
    | FormatSpec
    | StringEnd -> true
    | _ -> false

/// The column `token` starts at, counted from 0, if it is the first token on its line.
let private lineStartColumn (source: string) (token: Token) =
    let mutable i = token.Start
    while i > 0 && (source[i - 1] = ' ' || source[i - 1] = '\t') do
        i <- i - 1
    if i = 0 || source[i - 1] = '\n' || source[i - 1] = '\r' then Some(token.Start - i) else None

/// The column of the innermost block in `stack`, outside any brackets.
let private blockColumn (stack: Context list) =
    stack |> List.pick (function Block(column, _) -> Some column | _ -> None)

/// The layout tokens before `token`, which starts its line at `column`, after `previous`, written to `out`, and what
/// the pass is inside afterwards, from `stack`, what it was inside before.
let private lineStart (source: string) (out: ResizeArray<Laid>) (stack: Context list) (previous: Token) (token: Token) (column: int) =
    let isArm = token.Kind = Bar
    // Rule 4: a line indented less than a block closes it, unless brackets are open inside the block, and so does a
    // line at a block of arms' column that is not another arm. The top level's block is never closed.
    let rec close (stack: Context list) =
        match stack with
        | Block(inner, arms) :: (_ :: _ as rest) when column < inner || (arms && column = inner && not isArm) ->
            out.Add(Dedent token.Start)
            close rest
        | _ -> stack
    let stack = close stack
    let indent (arms: bool) =
        out.Add(Indent token.Start)
        Block(column, arms) :: stack
    match stack with
    // Rule 6: a line that starts with a closing bracket or brace is part of what it closes.
    | _ when isCloser token -> stack
    // Rule 2: after a token that opens a block, a line indented further than the innermost block opens a block at its
    // column. A `{` closes what it opens, so the block after one may be at any column.
    | _ when opensBlock source previous && (column > blockColumn stack || previous.Kind = LeftBrace) -> indent isArm
    // Rule 3: a match's arms are a block of their own, even at the column of the block the match is in.
    | Block(inner, false) :: _ when isArm && column = inner -> indent true
    // Rule 5: a line at a block's column starts its next item, unless it continues the one before.
    | Block(inner, _) :: _ when column = inner && (isArm || not (continuesItem source previous token)) ->
        out.Add(Newline token.Start)
        stack
    // Rule 3 too: arms indented further than the innermost block, except in `[` and `]`, where a table's rows start
    // with `|`.
    | top :: _ when isArm && top <> Bracket RightBracket && column > blockColumn stack -> indent true
    // Rule 5 too: any other line continues the item it is in, and in brackets every line does (rule 6).
    | _ -> stack

/// What the pass is inside after `token`, from `stack`, what it was inside before it, with a `Dedent` written to `out`
/// for each block a closing bracket or brace closes (rule 6).
let private afterToken (out: ResizeArray<Laid>) (stack: Context list) (token: Token) =
    let rec closeTo (opener: Context) (stack: Context list) =
        match stack with
        | [] -> []
        | context :: rest when context = opener -> rest
        | Block _ :: rest ->
            out.Add(Dedent token.Start)
            closeTo opener rest
        | _ :: rest -> closeTo opener rest
    let closing (opener: Context) = if List.contains opener stack then closeTo opener stack else stack
    match token.Kind with
    | LeftParen -> Bracket RightParen :: stack
    | LeftBracket -> Bracket RightBracket :: stack
    | StringStart -> Bracket StringEnd :: stack
    | LeftBrace -> Brace :: stack
    | RightParen
    | RightBracket
    | StringEnd -> closing (Bracket token.Kind)
    | RightBrace -> closing Brace
    | _ -> stack

/// `tokens`, lexed from `source`, with `Indent`, `Newline` and `Dedent` among them where its layout puts them. The
/// tokens are all there, in order, and the last is `EndOfFile`, with a `Dedent` before it for each block still open
/// but the top level's.
let layout (source: string) (tokens: Token array) : Laid array =
    let out = ResizeArray<Laid>()
    let mutable stack: Context list = []
    let mutable previous: Token option = None
    for token in tokens do
        match token.Kind, lineStartColumn source token, previous with
        | EndOfFile, _, _ ->
            // Rule 7: the end of the text closes every block but the top level's.
            for _ in 2 .. (stack |> List.filter (function Block _ -> true | _ -> false) |> List.length) do
                out.Add(Dedent token.Start)
        // Rule 1: the top level is a block at the column of the first token.
        | _, Some column, None -> stack <- [ Block(column, false) ]
        | _, Some column, Some previous -> stack <- lineStart source out stack previous token column
        | _ -> ()
        stack <- afterToken out stack token
        out.Add(Code token)
        previous <- Some token
    out.ToArray()

/// The groups of items the layout of `laid` makes: for each block, the offsets its items start at, in order, the
/// top level's last. An item that starts with `|` (a match's arm, a union's case) is taken to start at the token after
/// it.
let blocks (laid: Laid array) : int list list =
    let finished = ResizeArray<int list>()
    // The open blocks' items so far, innermost first, each in reverse.
    let mutable openBlocks: int list list = [ [] ]
    let mutable expecting = true
    for item in laid do
        match item with
        | Indent _ ->
            openBlocks <- [] :: openBlocks
            expecting <- true
        | Newline _ -> expecting <- true
        | Dedent _ ->
            finished.Add(List.rev openBlocks.Head)
            openBlocks <- openBlocks.Tail
        | Code token when token.Kind = EndOfFile || (expecting && token.Kind = Bar) -> ()
        | Code token when expecting ->
            openBlocks <- (token.Start :: openBlocks.Head) :: openBlocks.Tail
            expecting <- false
        | Code _ -> ()
    for remaining in openBlocks do
        finished.Add(List.rev remaining)
    List.ofSeq finished
