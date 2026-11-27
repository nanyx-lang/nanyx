/// The recursive-descent parser's expressions: the operator expressions and what they are made of, read from the layout pass's tokens. It reads literals, strings with holes, names, calls, tags, member access,
/// tuples, records, lists and maps, the prefix `not` and `-`, pipes and the binary operators, with the precedence and
/// grouping of `Operators.binaryOperators`. Lambdas, arms and what starts with a keyword (`if`, `match`, `for`, `use`,
/// ...) come with blocks and statements, in later files, and are read through `Forward`.
module Parser.Program.DescentExpressions

open System
open Parser.Program.Lexer
open Parser.Program.Layout
open Parser.Program.DescentCursor
open Parser.Program.DescentLiterals

/// An operand and the span of all the text it was written as, which is wider than the expression's own span when it is
/// in brackets.
type private Operand = Expression * SourceSpan

/// Where an expression stops.
type Lines =
    /// A def's value, an argument, an item. An operator may end its line, and the operand after it start
    /// the next, and a pipe may start any line after it.
    | Crossing
    /// A statement, or a match arm's body. The operand after an operator is on its line,
    /// a pipe continues it only on a line indented more than its first, and there is no range.
    | Inline

/// The column `offset` is at in `source`, counted from 0, and the indent of the line it is on.
let private columnAndIndent (source: string) (offset: int) =
    let lineStart = source.LastIndexOf('\n', max 0 (offset - 1)) + 1
    let lineStart = if offset = 0 then 0 else lineStart
    let mutable indent = 0
    while lineStart + indent < source.Length && (source[lineStart + indent] = ' ' || source[lineStart + indent] = '\t') do
        indent <- indent + 1
    offset - lineStart, indent

/// Whether the code token at the cursor is the first on its line.
let private startsLine (c: Cursor) =
    let column, indent = columnAndIndent c.Source c.Start
    column = indent

/// Skips the layout tokens at the cursor when what comes after them is a code token `fits` takes, going across lines; a
/// line indented less than the one before (a `Dedent`) is a mistake there.
let internal across (c: Cursor) (fits: Cursor -> bool) =
    let start = c.Index
    let mutable dedented = false
    while (match c.At 0 with Code _ -> false | Dedent _ -> dedented <- true; true | _ -> true) do
        c.Index <- c.Index + 1
    if fits c then
        if dedented && c.Index <> start then c.GiveUp "a line indented less than the one before"
        true
    else
        c.Index <- start
        false

/// The arguments of `f(fields) { ... }`: the trailing lambda is the last positional argument, as in a pipe, so
/// `f(a, b) { ... }` is `f(a, b, { ... })`, and `f(a, y = 1) { ... }` is `f(a, { ... }, y = 1)`
///.
let private withTrailingLambda (fields: RecordField list) (span: SourceSpan) (lambda: Expression) =
    let positional, named = fields |> List.partition (function PositionalField _ -> true | NamedField _ -> false)
    match named with
    | [] -> (positional |> List.choose (function PositionalField e -> Some e | NamedField _ -> None)) @ [ lambda ]
    | _ -> [ Expression.at span (RecordExpr(positional @ [ PositionalField lambda ] @ named)) ]

/// The keywords that start a primary expression of their own, before they would be read as names.
let private constructKeywords =
    set [ "return"; "try"; "yield"; "mut"; "use"; "if"; "do"; "for"; "while"; "match"; "type" ]

/// `{` on the line after spaces, where a lambda would start.
let private lambdaFollows (c: Cursor) = c.Is LeftBrace && c.OnSameLine

/// A name qualified with dots written with no spaces, `m.Email.parse`, as `qualifiedIdentifier` reads it, with its span.
let private qualifiedName (c: Cursor) : Identifier * SourceSpan =
    let start = c.Start
    noDollar c
    let head, _ = name c
    let parts = ResizeArray [ head ]
    while c.Adjacent && c.Is Dot do
        c.Take() |> ignore
        if not c.Adjacent then raise NoMatch
        parts.Add(fst (name c))
    String.concat "." parts, c.SpanFrom start

/// Skips the layout tokens at the cursor: in brackets, which have no layout of their own, the only ones are those of a
/// block opened inside them, a lambda's or arms', which may close after a `,` or before the bracket.
let private skipLayoutTokens (c: Cursor) =
    while (match c.At 0 with Code _ -> false | _ -> true) do
        c.Index <- c.Index + 1

/// One expression, or several separated by commas written one per line when the first starts its line.
let private byCommaOrLine (c: Cursor) (item: unit -> 'a) : 'a list =
    let firstStart = c.Start
    let firstColumn, _ = columnAndIndent c.Source firstStart
    let firstStartsLine = startsLine c
    let items = ResizeArray [ item () ]
    let mutable going = true
    while going do
        let index = c.Index
        skipLayoutTokens c
        if c.Is Comma && (c.OnSameLine || index <> c.Index) then
            c.Take() |> ignore
            skipLayoutTokens c
            items.Add(item ())
        elif firstStartsLine && c.Token.IsSome && not c.OnSameLine && startsLine c && fst (columnAndIndent c.Source c.Start) = firstColumn then
            match c.Attempt item with
            | Some next -> items.Add next
            | None -> going <- false
        else
            c.Index <- index
            going <- false
    skipLayoutTokens c
    List.ofSeq items

/// A lone positional field is its expression, positional fields are a tuple, and a named one makes a record.
let private fieldsToExpr (fields: RecordField list) (span: SourceSpan) =
    match fields with
    | [ PositionalField single ] -> single
    | _ when fields |> List.forall (function PositionalField _ -> true | NamedField _ -> false) ->
        Expression.at span (TupleExpr(fields |> List.choose (function PositionalField e -> Some e | NamedField _ -> None)))
    | _ -> Expression.at span (RecordExpr fields)

/// A double-quoted string: its text decoded, with each hole's expression, or a literal when it has none.
let rec private doubleQuoted (c: Cursor) : Expression =
    let start = c.Start
    let decoded (offset: int) (text: string) =
        match StringEscapes.decode text with
        | Some value -> value
        | None -> reportBadEscape c offset text
    let parts = ResizeArray<StringPart>()
    // Each run of text that isn't empty is noted.
    let addText (token: Token) =
        let written = c.Text token
        let text = written.Substring(1, written.Length - 2)
        if text <> "" then
            c.NoteStringText (token.Start + 1) (token.End - 1)
            parts.Add(StringText(decoded (token.Start + 1) text))
    // A string with no holes is read the same way.
    if c.Is StringLiteral then
        addText (c.Take())
        let text = parts |> Seq.choose (function StringText text -> Some text | StringExpr _ -> None) |> String.concat ""
        Expression.at (c.SpanFrom start) (LiteralExpr(StringLit text))
    else
    addText (c.Expect StringStart)
    let mutable going = true
    while going do
        let hole = try expression Crossing c with NoMatch -> c.GiveUp "a hole that doesn't read, which is a mistake"
        parts.Add(StringExpr hole)
        if not (c.Is StringMiddle || c.Is StringEnd) then c.GiveUp "a hole that doesn't read, which is a mistake"
        if c.Is StringMiddle then addText (c.Take())
        else
            addText (c.Expect StringEnd)
            going <- false
    let span = c.SpanFrom start
    if parts |> Seq.exists (function StringExpr _ -> true | StringText _ -> false) then Expression.at span (InterpolatedString(List.ofSeq parts))
    else Expression.at span (LiteralExpr(StringLit(parts |> Seq.choose (function StringText text -> Some text | StringExpr _ -> None) |> String.concat "")))

/// `name = value` or `value`, a field of a record or tuple, or an argument.
and private recordField (c: Cursor) : RecordField =
    let named () =
        let fieldName, nameSpan = name c
        c.Expect Equals |> ignore
        let value = expression Crossing c
        c.Declare NamedFieldValue value.Id [ fieldName, nameSpan ]
        NamedField(fieldName, value)
    // `named` takes a name and then expects `=`: where a name has no `=` after it, it would fail there, and is not tried.
    let namedNext =
        if c.Is BacktickedName then true
        elif not (c.Is Name || c.Is Keyword) then false
        elif c.IsAt(1, Equals) then true
        else
            c.NoteAt(1, "`=`")
            false
    match c.AttemptIf(namedNext, named) with
    | Some field -> field
    | None -> PositionalField(expression Crossing c)

/// The fields in brackets after a name, at least one, and the span from the first to the last.
and internal parenFields (c: Cursor) : RecordField list * SourceSpan =
    c.Expect LeftParen |> ignore
    let start = c.Start
    let fields = byCommaOrLine c (fun () -> recordField c)
    let span = c.SpanFrom start
    c.Expect RightParen |> ignore
    fields, span

/// `(args)`, as a call's arguments: none, one, a tuple of them, or a record, and the lambda after them on the line,
/// a trailing one, if there is one.
and private callArguments (c: Cursor) : Expression list =
    let fields =
        if c.Is LeftParen && (match c.At 1 with Code next -> next.Kind = RightParen | _ -> false) then
            c.Take() |> ignore
            c.Take() |> ignore
            None
        else Some(parenFields c)
    let trailing = if lambdaFollows c then Some(Forward.lambda.Value c) else None
    match fields, trailing with
    | Some(fields, span), Some lambda -> withTrailingLambda fields span lambda
    | Some(fields, span), None -> [ fieldsToExpr fields span ]
    | None, Some lambda -> [ lambda ]
    | None, None -> []

/// A call, `f(args)`, and then each call on the result before it, `f()(args)` (see `CallResultSyntax`).
and private call (c: Cursor) : Expression =
    let start = c.Start
    let callee, calleeSpan = qualifiedName c
    if c.Adjacent && c.Is LeftBracket then collectionConstruct c start callee calleeSpan
    // `f { ... }`: a lambda alone after the name is the only argument.
    elif lambdaFollows c then
        let lambda = Forward.lambda.Value c
        Expression.at (c.SpanFrom start) (FunctionCall(callee, Some calleeSpan, [ lambda ]))
    else
    if not (c.Adjacent && c.Is LeftParen) then raise NoMatch
    let args = callArguments c
    let mutable result = Expression.at (c.SpanFrom start) (FunctionCall(callee, Some calleeSpan, args))
    while c.Adjacent && c.Is LeftParen do
        let argsStart = c.Start
        let args = callArguments c
        let span = SourceSpan.between result.Span (c.SpanFrom argsStart)
        let definition = Statement.at result.Span (DefStatement(false, CallResultSyntax.name, None, result))
        let calling = Statement.at span (ExprStatement(Expression.at span (FunctionCall(CallResultSyntax.name, None, args))))
        result <- Expression.at span (Block [ definition; calling ])
    result

/// Whether `contextMember` can read past the name at the cursor: a capitalised name with `(` straight after it. Where a
/// name is there without them, it would fail having taken the name, and this notes that (see `Cursor.AttemptIf`).
and private contextMemberNext (c: Cursor) =
    match c.Token with
    | Some token when token.Kind = Name || token.Kind = Keyword ->
        if Char.IsUpper((c.Text token)[0]) && c.AdjacentAt 1 && c.IsAt(1, LeftParen) then true
        else
            c.NoteAt 1
            false
    | _ -> false

/// Whether `call` can read past the name at the cursor: a name with `[`, `(` or a lambda after it. Where a name that no
/// `.` follows is there without one, `call` would fail having taken it, and this notes that. A qualified name, a name in
/// backticks and a `$` are left to `call`.
and private callNext (c: Cursor) =
    if not (c.Is Name || c.Is Keyword) || (c.AdjacentAt 1 && c.IsAt(1, Dot)) then true
    elif c.AdjacentAt 1 && (c.IsAt(1, LeftBracket) || c.IsAt(1, LeftParen)) then true
    elif c.IsAt(1, LeftBrace) && c.OnSameLineAt 1 then true
    else
        c.NoteAt 1
        false

/// `C(T).member` or `C(T).member(args)`: a member of a context's instance for a type, named by a capitalised context.
and private contextMember (c: Cursor) : Expression =
    let start = c.Start
    let contextName, _ = identifierWithSpan c
    if not (Char.IsUpper contextName[0]) || not (c.Adjacent && c.Is LeftParen) then raise NoMatch
    // A call of a capitalised name, `Wrap(x)`, has no `.` after its `)`: its arguments are read once, as a call's (#2218).
    match closingParen c 0 with
    | Some close ->
        match c.At(close + 1), c.At close with
        | Code dot, Code paren when dot.Kind = Dot && dot.Start = paren.End -> ()
        | _ -> raise NoMatch
    | None -> ()
    c.Take() |> ignore
    let types = sepBy1If c (fun () -> DescentTypes.typeExpr c) (fun () -> c.Expects Comma) (fun () -> c.Expect Comma |> ignore)
    c.Expect RightParen |> ignore
    let contextType = TypeExpr.at (c.SpanFrom start) (TypeApply(contextName, types))
    if not (c.Adjacent && c.Is Dot) then raise NoMatch
    c.Take() |> ignore
    if not c.Adjacent then raise NoMatch
    let memberName, _ = name c
    let kind =
        if c.Adjacent && c.Is LeftParen then
            c.Take() |> ignore
            let args = if c.Is RightParen then [] else sepBy1If c (fun () -> expression Crossing c) (fun () -> c.Expects Comma) (fun () -> c.Expect Comma |> ignore)
            c.Expect RightParen |> ignore
            ContextMemberCall(contextType, memberName, args)
        else ContextMemberAccess(contextType, memberName)
    Expression.at (c.SpanFrom start) kind

/// `#tag`, with its payload in brackets straight after it: `#some(x)`, `#pair(a, b)`, `#some()`.
and private tag (c: Cursor) : Expression =
    let start = c.Start
    c.Expect Hash |> ignore
    if not c.Adjacent then raise NoMatch
    let tagName, _ = identifierWithSpan c
    let payload =
        if c.Adjacent && c.Is LeftParen then
            let open' = c.Start
            if (match c.At 1 with Code next -> next.Kind = RightParen | _ -> false) then
                c.Take() |> ignore
                c.Take() |> ignore
                Some(Expression.at (c.SpanFrom open') UnitExpr)
            else
                let fields, span = parenFields c
                Some(fieldsToExpr fields span)
        else None
    Expression.at (c.SpanFrom start) (TagExpr(tagName, payload))

/// The items in `[` and `]`, with the span of the brackets: runs of items and spreads, or a map's entries as tuples,
/// when every item is `key => value`.
and private bracketItems (c: Cursor) : Choice<Choice<Expression list, Expression> list, Expression list> * SourceSpan =
    let start = c.Start
    c.Expect LeftBracket |> ignore
    let item () =
        if c.Is Ellipsis then
            c.Take() |> ignore
            Choice2Of2(expression Crossing c)
        else
            let key = expression Crossing c
            if c.Is FatArrow && c.OnSameLine then
                c.Take() |> ignore
                Choice1Of2(key, Some(expression Crossing c))
            else Choice1Of2(key, None)
    let items = if c.Is RightBracket then [] else byCommaOrLine c item
    c.Expect RightBracket |> ignore
    let span = c.SpanFrom start
    let entries = items |> List.choose (function Choice1Of2(key, Some value) -> Some(key, value) | _ -> None)
    if entries.IsEmpty then
        // Each run of elements is built backwards, by consing, and turned round once, so a literal of n elements is
        // built in time that grows with n (#2220).
        let segments =
            items
            |> List.fold (fun segments item ->
                match item, segments with
                | Choice1Of2(element, _), Choice1Of2 elements :: rest -> Choice1Of2(element :: elements) :: rest
                | Choice1Of2(element, _), _ -> Choice1Of2 [ element ] :: segments
                | Choice2Of2 spread, _ -> Choice2Of2 spread :: segments) []
            |> List.rev
            |> List.map (function Choice1Of2 elements -> Choice1Of2(List.rev elements) | spread -> spread)
        Choice1Of2 segments, span
    elif entries.Length = items.Length then
        Choice2Of2(entries |> List.map (fun (key, value) -> Expression.from key (TupleExpr [ key; value ]))), span
    else c.Report start "A map literal has an item that is not an entry: write each item as `key => value`"

/// `[| a | b |` and then rows of cells, a table: the list of a record for each row, with the header's fields
///. Each row is on a line of its own, and spans its `|`s.
and private table (c: Cursor) : Expression =
    let start = c.Start
    c.Expect LeftBracket |> ignore
    c.Expect Bar |> ignore
    let header = ResizeArray()
    while c.OnSameLine && (c.Is Name || c.Is Keyword || c.Is BacktickedName) do
        header.Add(name c)
        c.Expect Bar |> ignore
    if header.Count = 0 then raise NoMatch
    let rows = ResizeArray()
    while c.Is Bar && not c.OnSameLine do
        let rowStart = c.Start
        c.Take() |> ignore
        let cells = ResizeArray()
        while c.OnSameLine && not (c.Is RightBracket) do
            cells.Add(expression Crossing c)
            if not c.OnSameLine then raise NoMatch
            c.Expect Bar |> ignore
        if cells.Count <> header.Count then
            c.Report rowStart $"This row has {cells.Count} cells, but the table's header names {header.Count} fields: each row has a value for each field"
        let fields =
            Seq.zip header cells
            |> Seq.map (fun ((field, fieldSpan), value) ->
                c.Declare NamedFieldValue value.Id [ field, fieldSpan ]
                NamedField(field, value))
            |> List.ofSeq
        rows.Add(Expression.at (c.SpanFrom rowStart) (RecordExpr fields))
    if rows.Count = 0 then c.Report c.LastEnd "A table literal has a row of values under its header"
    c.Expect RightBracket |> ignore
    Expression.at (c.SpanFrom start) (ListExpr(List.ofSeq rows))

/// `[a, b]`, `[a, ...xs]` or `[k => v]`: a list, a concatenation of its runs and spreads, or a map. A literal that is
/// one spread, `[...xs]`, is `[] ++ xs`, so that it is still a literal, whose spread is checked as any other is.
and private list (c: Cursor) : Expression =
    if (match c.At 1 with Code next -> next.Kind = Bar && next.Start = c.Start + 1 | _ -> false) then table c else
    match bracketItems c with
    | Choice1Of2 segments, span ->
        let segments =
            match segments with
            | [ Choice2Of2 _ ] -> Choice1Of2 [] :: segments
            | _ -> segments
        let segments = segments |> List.map (function Choice1Of2 elements -> Expression.at span (ListExpr elements) | Choice2Of2 spread -> spread)
        match segments with
        | [] -> Expression.at span (ListExpr [])
        | _ -> segments |> List.reduceBack (fun left right -> Expression.at span (BinaryOp("++", None, left, right)))
    | Choice2Of2 tuples, span -> Expression.at span (FunctionCall(MapSyntax.builtIn, None, [ Expression.at span (ListExpr tuples) ]))

/// `T[...]`, a literal written straight after a type's name, which builds a `T` (see `CollectionSyntax`).
and private collectionConstruct (c: Cursor) (start: int) (typeName: Identifier) (nameSpan: SourceSpan) : Expression =
    let segments, span =
        match bracketItems c with
        | Choice1Of2 segments, span -> segments, span
        | Choice2Of2 tuples, span -> [ Choice1Of2 tuples ], span
    Expression.at (c.SpanFrom start) (CollectionSyntax.build typeName (Some nameSpan) span segments)

/// `(...)`: an expression in brackets, a tuple, a record, or a record with spreads (see `SpreadSyntax`).
and private bracketed (c: Cursor) : Expression =
    let start = c.Start
    c.Expect LeftParen |> ignore
    let part () =
        if c.Is Ellipsis then
            c.Take() |> ignore
            Choice2Of2(expression Crossing c)
        else Choice1Of2(recordField c)
    let parts = byCommaOrLine c part
    c.Expect RightParen |> ignore
    let span = c.SpanFrom start
    let fields = parts |> List.choose (function Choice1Of2 field -> Some field | Choice2Of2 _ -> None)
    if fields.Length = parts.Length then fieldsToExpr fields span
    elif fields |> List.exists (function PositionalField _ -> true | NamedField _ -> false) then
        c.Report start "A record with a spread has a field without a name: name each of its other fields, as `name = value`"
    else
        let parts = parts |> List.map (function Choice1Of2 field -> field | Choice2Of2 spread -> PositionalField spread)
        Expression.at span (FunctionCall(SpreadSyntax.builtIn, None, [ Expression.at span (RecordExpr parts) ]))

/// A primary expression, with its alternatives in order. A keyword that doesn't start one of them is read as a name:
/// `pattern = pattern`.
and private primary (c: Cursor) : Expression =
    let start = c.Start
    let token = c.Token.Value
    let next = match c.At 1 with Code next -> Some next | _ -> None
    let nextAdjacent = next |> Option.exists (fun next -> next.Start = token.End)
    match token.Kind with
    // `name! x`, a workflow keyword applied the older way, before any keyword's own reading (`workflowBindExpr`).
    | Keyword when c.Text token <> WorkflowSyntax.returnKeyword && nextAdjacent && next |> Option.exists (fun next -> next.Kind = Operator && c.Text next = "!") ->
        Forward.keywordExpression.Value c
    | Keyword when constructKeywords.Contains(c.Text token) -> Forward.keywordExpression.Value c
    | LeftBrace -> Forward.lambda.Value c
    | StringStart -> doubleQuoted c
    | StringLiteral when (c.Text token).StartsWith "\"" -> doubleQuoted c
    | Name when nextAdjacent && next |> Option.exists (fun next -> next.Kind = Operator && c.Text next = "!") ->
        Forward.keywordExpression.Value c
    | LeftParen when next |> Option.exists (fun next -> next.Kind = RightParen) ->
        c.Take() |> ignore
        c.Take() |> ignore
        Expression.at (c.SpanFrom start) UnitExpr
    | Name
    | Keyword
    | BacktickedName
    | Dollar ->
        match c.AttemptIf(contextMemberNext c, fun () -> contextMember c) with
        | Some expr -> expr
        | None ->
            match c.AttemptIf(callNext c, fun () -> call c) with
            | Some expr -> expr
            | None when token.Kind = Name && (c.Text token = "true" || c.Text token = "false") ->
                let literal, span = literal c
                Expression.at span (LiteralExpr literal)
            | None ->
                // A name applied to a value without brackets, in a lambda, as only a workflow keyword is
                //.
                match c.At 1 with
                | Code next when c.LambdaDepth > 0 && token.Kind = Name && Char.IsLower((c.Text token)[0]) && not (Keywords.reserved.Contains(c.Text token))
                                 && next.Start > token.End && c.Source.Substring(token.End, next.Start - token.End) |> Seq.forall (fun ch -> ch = ' ' || ch = '\t')
                                 && (List.contains next.Kind [ Name; Number; StringLiteral; StringStart; Hash; LeftParen; LeftBracket ]) ->
                    let applied = c.Text token
                    c.Report token.Start
                        ($"`{applied}` is applied to a value without parentheses, as only a workflow keyword (`try`, `yield` or `return`) is: "
                         + $"write `{applied}(...)` to call a function")
                | _ -> ()
                let identifier, span = name c
                Expression.at span (IdentifierExpr identifier)
    | Hash -> tag c
    | Number
    | StringLiteral ->
        let literal, span = literal c
        Expression.at span (LiteralExpr literal)
    | Operator when (c.Text token = "-" || c.Text token = "+") && nextAdjacent && next |> Option.exists (fun next -> next.Kind = Number) ->
        let literal, span = literal c
        Expression.at span (LiteralExpr literal)
    | Operator when c.Text token = coalesceOperator && nextAdjacent && next |> Option.exists (fun next -> c.Text next = "?") ->
        c.Take() |> ignore
        c.Take() |> ignore
        Expression.at (c.SpanFrom start) (IdentifierExpr notImplemented)
    | LeftBracket -> list c
    | LeftParen -> bracketed c
    | _ -> c.Fail "an expression"

/// A primary expression and the `.field`s after it, with the span of its text.
and private accessed (c: Cursor) : Operand =
    // In a shorthand lambda's body, the first operand is the argument it leaves out, made up at the operator, `.` or `\`
    // written in its place, which takes up no text.
    let madeUp = c.ImplicitArgumentNext
    let mutable expr, text =
        match madeUp with
        | Some span ->
            c.ImplicitArgumentNext <- None
            Expression.at span (IdentifierExpr implicitArgument), span
        | None ->
            let start = c.Start
            let expr = primary c
            expr, c.SpanFrom start
    // The made-up argument's first `.field` is written where the argument would be, not straight after it.
    let mutable first = madeUp.IsSome
    let field () =
        if not ((c.Adjacent || first) && c.Is Dot) then raise NoMatch
        first <- false
        c.Take() |> ignore
        if not c.Adjacent then raise NoMatch
        match c.Token with
        | Some token when token.Kind = Number ->
            let digits = c.Text token
            if not (digits |> Seq.forall Char.IsAsciiDigit) then c.GiveUp "a tuple's index with a `.` after it, which the lexer reads as one number"
            c.Take() |> ignore
            digits, c.SpanOf token
        | _ -> name c
    let mutable going = true
    // Not `c.AttemptIf(..., field)`: in the browser, .NET's WebAssembly runtime read the primary again after that
    // `AttemptIf` that didn't run `field`, taking the name on the next line, where every other runtime didn't (#3152).
    while going do
        if (c.Adjacent || first) && c.Is Dot then
            match c.Attempt field with
            | Some(fieldName, fieldSpan) ->
                text <- SourceSpan.between text fieldSpan
                expr <- Expression.at text (MemberAccess(expr, fieldName, Some fieldSpan))
            | None -> going <- false
        else going <- false
    if c.IsKeyword "with" && c.OnSameLine then withUpdates c (expr, text) else expr, text

/// `record with x = 1, y = 2`, or with the updates in brackets, `record with (x = 1)`: a call to the built-in "__with"
/// with the record and a record of the updates (see `WithSyntax`).
and private withUpdates (c: Cursor) ((expr: Expression), (text: SourceSpan)) : Operand =
    c.Take() |> ignore
    across c (fun c -> c.Token.IsSome) |> ignore
    let updatesStart = c.Start
    let updates =
        if c.Is LeftParen then
            c.Take() |> ignore
            let fields = byCommaOrLine c (fun () -> recordField c)
            c.Expect RightParen |> ignore
            fields
        else
            let update () =
                let field, fieldSpan = name c
                if not c.OnSameLine then raise NoMatch
                c.Expect Equals |> ignore
                across c (fun c -> c.Token.IsSome) |> ignore
                let value = expression Crossing c
                c.Declare NamedFieldValue value.Id [ field, fieldSpan ]
                NamedField(field, value)
            sepBy1If c update (fun () -> c.OnSameLine && c.Expects Comma) (fun () -> (if not c.OnSameLine then raise NoMatch); c.Expect Comma |> ignore; across c (fun c -> c.Token.IsSome) |> ignore)
    let updatesSpan = c.SpanFrom updatesStart
    let updated = Expression.at updatesSpan (RecordExpr updates)
    let span = SourceSpan.between text updatesSpan
    Expression.at span (FunctionCall(WithSyntax.builtIn, None, [ expr; updated ])), span

/// What a `\` applies the value before it to, with the span from the function's name: a call of a function with the
/// value as its first argument.
and private pipeTarget (c: Cursor) : Choice<Identifier * SourceSpan * Expression list, MatchArm list> * SourceSpan =
    let start = c.Start
    // `\match` and arms: a match on the value.
    if c.IsKeyword "match" then
        c.Take() |> ignore
        let arms = Forward.arms.Value c
        Choice2Of2 arms, c.SpanFrom start
    else
    let callee, calleeSpan = qualifiedName c
    let args =
        if c.Adjacent && c.Is LeftParen then
            if (match c.At 1 with Code next -> next.Kind = RightParen | _ -> false) then c.GiveUp "a pipe's empty brackets, which are a mistake"
            let fields, span = parenFields c
            match fields with
            | PositionalField(Kind(RecordExpr _)) :: _ -> [ Expression.at span (RecordExpr fields) ]
            | _ when fields |> List.forall (function PositionalField _ -> true | NamedField _ -> false) ->
                fields |> List.choose (function PositionalField e -> Some e | NamedField _ -> None)
            | _ -> [ Expression.at span (RecordExpr fields) ]
        else []
    let args = if lambdaFollows c then args @ [ Forward.lambda.Value c ] else args
    Choice1Of2(callee, calleeSpan, args), c.SpanFrom start

/// `value` and then each `\` step after it: on the line, or for `Crossing` on any line after
/// it, or for `Inline` on a line indented more than its first, which has no layout token before it.
and private withPipes (lines: Lines) (c: Cursor) (value: Operand) : Operand =
    let pipeNext () =
        match lines with
        | Crossing -> across c (fun c -> c.Is Backslash)
        | Inline -> c.Is Backslash
    let mutable result = value
    while pipeNext () do
        c.Take() |> ignore
        if not c.OnSameLine then raise NoMatch
        let step, targetSpan = pipeTarget c
        let acc, accText = result
        let span = SourceSpan.between accText targetSpan
        let kind =
            match step with
            | Choice1Of2(callee, calleeSpan, args) -> Pipe(acc, callee, Some calleeSpan, args)
            | Choice2Of2 arms -> Match([ acc ], arms)
        result <- Expression.at span kind, span
    result

/// An operand: a primary expression after any `not` and `-` before it, and the pipes after it.
and private operand (lines: Lines) (c: Cursor) : Operand =
    let prefixes = ResizeArray()
    let mutable going = true
    while going do
        match c.Token with
        | Some token when token.Kind = Keyword && c.Text token = "not" ->
            c.Take() |> ignore
            prefixes.Add((NotSyntax.builtIn, c.SpanOf token))
            match lines with
            | Crossing -> across c (fun c -> c.Token.IsSome) |> ignore
            | Inline -> if not c.OnSameLine then raise NoMatch
        | Some token when token.Kind = Operator && c.Text token = "-" && c.ImplicitArgumentNext.IsNone ->
            match c.At 1 with
            | Code next when next.Start = token.End && next.Kind <> Number ->
                c.Take() |> ignore
                prefixes.Add((NegateSyntax.builtIn, c.SpanOf token))
            | _ -> going <- false
        | Some token when token.Kind = Operator && c.Text token = "!" && c.ImplicitArgumentNext.IsNone -> c.Report token.Start "There is no prefix `!` in Nanyx: write the keyword `not`, as in `not x`"
        | _ -> going <- false
    let term =
        prefixes
        |> Seq.rev
        |> Seq.fold (fun ((inner: Expression), innerText) (builtIn, prefixSpan) ->
            let span = SourceSpan.between prefixSpan innerText
            Expression.at span (FunctionCall(builtIn, None, [ inner ])), span) (accessed c)
    let piped = withPipes lines c term
    // `&&` and `||` are not operators: Boolean operations are the keywords `and` and `or`.
    match c.Token with
    | Some token when c.OnSameLine && (token.Kind = Ampersand || token.Kind = Bar) ->
        match c.At 1 with
        | Code next when next.Kind = token.Kind && next.Start = token.End ->
            let written, keyword = if token.Kind = Ampersand then "&&", "and" else "||", "or"
            c.Report token.Start $"`{written}` is not an operator in Nanyx: write `{keyword}`"
        | _ -> piped
    | _ -> piped

/// The binary operator at the cursor, if one is written after an operand on its line, as the operator table for
/// `lines` takes it: `None` when what is there ends the expression.
and private operatorAt (lines: Lines) (c: Cursor) : BinaryOperator option =
    if not c.OnSameLine then None
    else
        match c.Token with
        | Some token when token.Kind = Operator || (token.Kind = Keyword && (c.Text token = "and" || c.Text token = "or")) ->
            binaryOperatorNamed (c.Text token)
        | Some token when token.Kind = DotDot ->
            match lines with
            | Inline -> c.GiveUp "a range in a statement, which only the full operator table reads"
            | Crossing ->
                // `a..` with nothing after it but the end of an enclosing construct is an open range (`withOpenRange`).
                let hasEnd =
                    match c.At 1 with
                    | Code next ->
                        let written = c.Text next
                        not (c.Source.AsSpan(token.End, next.Start - token.End).Contains '\n')
                        && next.Kind <> EndOfFile
                        && not (written.StartsWith "|")
                        && not (List.contains next.Kind [ RightParen; RightBracket; RightBrace; Comma; Backslash; Arrow ])
                    | _ -> false
                if hasEnd then
                    Some { Text = ".."; Precedence = rangePrecedence; Associativity = NonAssociative; IsSpare = false; ShortCircuits = false }
                else None
        | _ -> None

/// Operands joined by operators that bind at least as tightly as `minimum`, after `parent`, the operator before them,
/// by precedence climbing. Two operators of one precedence that don't group the same way, or don't
/// group, can't be written side by side.
and private climb (lines: Lines) (c: Cursor) (minimum: int) (parent: BinaryOperator option) : Operand =
    let mutable left = operand lines c
    let mutable previous = parent
    let mutable going = true
    let sideBySide (op: BinaryOperator) =
        match previous with
        | Some p when p.Precedence = op.Precedence && (p.Associativity <> op.Associativity || op.Associativity = NonAssociative) ->
            let message =
                if isComparison p.Text && isComparison op.Text then
                    $"Comparisons do not chain: write `a {p.Text} b and b {op.Text} c`, or put one comparison in parentheses"
                else $"`{p.Text}` and `{op.Text}` cannot be written side by side: put one of them in parentheses"
            c.Report c.Start message
        | Some p when p = isOperator && op.Precedence > p.Precedence ->
            c.Report c.Start $"`{op.Text}` binds tighter than `is`, so it can't follow a pattern: put the `is` test in parentheses"
        | _ -> ()
    while going do
        match operatorAt lines c with
        | _ when c.OnSameLine && c.IsKeyword IsSyntax.keyword && isOperator.Precedence >= minimum ->
            sideBySide isOperator
            let value, valueText = left
            // `not x is p` is `(not x) is p`, as `not a == b` is `(not a) == b`.
            match value.Kind with
            | FunctionCall(name, _, [ _ ]) when name = NotSyntax.builtIn && c.Source.AsSpan(valueText.Start).StartsWith(NotSyntax.builtIn) ->
                c.Report valueText.Start "`not` binds tighter than `is`: write `not (x is p)` to negate the test"
            | _ -> ()
            let isSpan = c.SpanOf(c.Take())
            if not c.OnSameLine then raise NoMatch
            let pattern, _ = c.Collecting(fun () -> DescentPatterns.pattern c)
            let span = SourceSpan.between valueText pattern.Span
            left <- IsSyntax.test span isSpan value pattern None, span
            previous <- Some isOperator
        | Some op when op.Precedence >= minimum ->
            sideBySide op
            let token = c.Take()
            let opSpan = c.SpanOf token
            if op.Text <> ".." then
                let isSpace (ch: char) = ch = ' ' || ch = '\t' || ch = '\r' || ch = '\n'
                let before = if token.Start > 0 then c.Source[token.Start - 1] else ' '
                let after = if token.End < c.Source.Length then c.Source[token.End] else ' '
                if not ((isSpace before || before = '{') && isSpace after) then
                    c.Report token.Start $"`{op.Text}` is not spaced: write an operator with a space on each side, as `x {op.Text} y`, not `x{op.Text}y`"
            match lines with
            | Crossing -> across c (fun c -> c.Token.IsSome) |> ignore
            | Inline -> if not c.OnSameLine then raise NoMatch
            let next = if op.Associativity = RightAssociative then op.Precedence else op.Precedence + 1
            let right, rightText = climb lines c next (Some op)
            let leftExpr, leftText = left
            let span = SourceSpan.between leftText rightText
            let combined =
                if op.Text = ".." then Expression.at span (FunctionCall(RangeSyntax.builtIn, None, [ leftExpr; right ]))
                // The names an `is` test in `leftExpr` binds are in scope in `right` (see `IsSyntax.andThen`).
                elif op.Text = "and" then IsSyntax.andThen (fun body names -> c.Declare ArmBody body.Id names) span opSpan leftExpr right
                else Expression.at span (BinaryOp(op.Text, Some opSpan, leftExpr, right))
            left <- combined, span
            previous <- Some op
        | _ -> going <- false
    left

/// An expression, which may go on across lines (`Crossing`) or ends at the end of its line (`Inline`).
and expression (lines: Lines) (c: Cursor) : Expression =
    if c.IsKeyword "match" then Forward.keywordExpression.Value c else
    let start = c.Start
    let operators = climb lines c 0 None
    let value =
        match lines with
        | Crossing ->
            let ranged =
                if c.Is DotDot && c.OnSameLine then
                    let dots = c.Take()
                    let first, firstText = operators
                    let span = SourceSpan.between firstText (c.SpanOf dots)
                    Expression.at span (FunctionCall(RangeSyntax.builtIn, None, [ first ])), span
                else operators
            fst (withPipes Crossing c ranged)
        | Inline -> fst operators
    let exceptNext =
        match lines with
        | Crossing -> across c (fun c -> c.IsKeyword "except")
        | Inline -> c.IsKeyword "except"
    // `value except arms`: the cases of the value the arms match replaced, the others passed through.
    let value =
        if exceptNext then
            let valueText = c.SpanFrom start
            let exceptStart = c.Start
            c.Take() |> ignore
            let arms = Forward.exceptArms.Value c
            Expression.at (SourceSpan.between valueText (c.SpanFrom exceptStart)) (Except(value, arms))
        else value
    // `target := value`, a call to the built-in "__assign" whose name span is the `:=` (see `AssignSyntax`).
    if c.Is ColonEquals && c.OnSameLine then
        let operatorSpan = c.SpanOf(c.Take())
        if not c.OnSameLine then raise NoMatch
        let assigned = expression lines c
        Expression.at (SourceSpan.between value.Span assigned.Span) (FunctionCall(AssignSyntax.builtIn, Some operatorSpan, [ value; assigned ]))
    else value

/// A def's value written on its line: an expression, or several separated by commas, a tuple of them
/// (`ParseState.singleOrTuple`).
let defValue (c: Cursor) : Expression =
    let start = c.Start
    let items = sepBy1If c (fun () -> expression Crossing c) (fun () -> c.OnSameLine && c.Expects Comma) (fun () -> (if not c.OnSameLine then raise NoMatch); c.Expect Comma |> ignore)
    match items with
    | [ single ] -> single
    | several -> Expression.at (c.SpanFrom start) (TupleExpr several)

/// An attribute's argument, written straight after its name as a tag's payload is: linter rules, `(default/unused)`,
/// whose `/` has no spaces round it, `()`, or arguments as a call's.
let attributeArgument (c: Cursor) : Expression =
    let linterRules () =
        let start = c.Start
        c.Expect LeftParen |> ignore
        let rule () =
            let ruleStart = c.Start
            let linter, linterSpan = identifierWithSpan c
            let slash = c.Expect(Operator, LinterRuleSyntax.separator)
            if slash.Start <> linterSpan.End then raise NoMatch
            if not c.Adjacent then raise NoMatch
            let ruleName, ruleSpan = identifierWithSpan c
            Expression.at (c.SpanFrom ruleStart)
                (BinaryOp(LinterRuleSyntax.separator, Some(c.SpanOf slash), Expression.at linterSpan (IdentifierExpr linter), Expression.at ruleSpan (IdentifierExpr ruleName)))
        let rules = sepBy1If c rule (fun () -> c.Expects Comma) (fun () -> c.Expect Comma |> ignore)
        c.Expect RightParen |> ignore
        match rules with
        | [ single ] -> single
        | several -> Expression.at (c.SpanFrom start) (TupleExpr several)
    match c.Attempt linterRules with
    | Some rules -> rules
    | None ->
        if c.Is LeftParen && (match c.At 1 with Code next -> next.Kind = RightParen | _ -> false) then
            let start = c.Start
            c.Take() |> ignore
            c.Take() |> ignore
            Expression.at (c.SpanFrom start) UnitExpr
        else
            let fields, span = parenFields c
            fieldsToExpr fields span
