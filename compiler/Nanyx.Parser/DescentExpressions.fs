/// The recursive-descent parser's expressions: the operator expressions of `Expressions.fs` and what they are made of,
/// read from the layout pass's tokens. It reads literals, strings with holes, names, calls, tags, member access,
/// tuples, records, lists and maps, the prefix `not` and `-`, pipes and the binary operators, with the precedence and
/// grouping of `Operators.binaryOperators`. Lambdas are `DescentBlocks`', through `lambdaRef`. It gives up at what starts
/// with a keyword (`if`, `match`, `for`, `use`, ...), which comes with blocks and statements (see the Work in progress
/// page "The parser").
module Parser.Program.DescentExpressions

open System
open Parser.Program.Lexer
open Parser.Program.Layout
open Parser.Program.DescentCursor
open Parser.Program.DescentLiterals

/// An operand and the span of all the text it was written as, which is wider than the expression's own span when it is
/// in brackets (see `Expressions.Operand`).
type private Operand = Expression * SourceSpan

/// Where an expression stops. The FParsec parser has two operator tables (see `Expressions.opp` and `oppInline`).
type Lines =
    /// `expression`: a def's value, an argument, an item. An operator may end its line, and the operand after it start
    /// the next, and a pipe may start any line after it.
    | Crossing
    /// `exprWithoutCrossingNewlines`: a statement, or a match arm's body. The operand after an operator is on its line,
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

/// Skips the layout tokens at the cursor when what comes after them is a code token `fits` takes, as FParsec's `ws`
/// goes across lines; a line indented less than the one before (a `Dedent`) is somewhere the descent parser doesn't
/// follow it yet.
let private across (c: Cursor) (fits: Cursor -> bool) =
    let start = c.Index
    let mutable dedented = false
    while (match c.At 0 with Code _ -> false | Dedent _ -> dedented <- true; true | _ -> true) do
        c.Index <- c.Index + 1
    if fits c then
        if dedented && c.Index <> start then c.GiveUp "a line indented less than the one before, which FParsec's `ws` crosses"
        true
    else
        c.Index <- start
        false

/// Reads a lambda, `{ ... }`, at the cursor: `DescentBlocks.lambda`, which reads statements and so comes after this file.
/// It is filled before anything is read (see `DescentBlocks.fill`), as the FParsec parser's forward refs are.
let internal lambdaRef: (Cursor -> Expression) ref = ref (fun c -> c.GiveUp "a lambda, before the lambda parser is filled")

/// The arguments of `f(fields) { ... }`: the trailing lambda is the last positional argument, as in a pipe, so
/// `f(a, b) { ... }` is `f(a, b, { ... })`, and `f(a, y = 1) { ... }` is `f(a, { ... }, y = 1)`
/// (`Expressions.withTrailingLambda`).
let private withTrailingLambda (fields: RecordField list) (span: SourceSpan) (lambda: Expression) =
    let positional, named = fields |> List.partition (function PositionalField _ -> true | NamedField _ -> false)
    match named with
    | [] -> (positional |> List.choose (function PositionalField e -> Some e | NamedField _ -> None)) @ [ lambda ]
    | _ -> [ Expression.at span (RecordExpr(positional @ [ PositionalField lambda ] @ named)) ]

/// The keywords that start a primary expression of their own, before the FParsec parser would read them as names.
let private constructKeywords =
    set [ "return"; "try"; "yield"; "mut"; "use"; "if"; "do"; "for"; "while"; "match"; "type" ]

/// `{` on the line after spaces, where a lambda would start.
let private lambdaFollows (c: Cursor) = c.Is LeftBrace && c.OnSameLine

/// A name qualified with dots written with no spaces, `m.Email.parse`, as `qualifiedIdentifier` reads it, with its span.
let private qualifiedName (c: Cursor) : Identifier * SourceSpan =
    let start = c.Start
    if c.Is Dollar then c.GiveUp "a `$` before a type's name, which is a mistake"
    let head, _ = name c
    let parts = ResizeArray [ head ]
    while c.Adjacent && c.Is Dot do
        c.Take() |> ignore
        if not c.Adjacent then raise NoMatch
        parts.Add(fst (name c))
    String.concat "." parts, c.SpanFrom start

/// One expression, or several separated by commas written one per line when the first starts its line (see
/// `Expressions.byCommaOrLine`).
let private byCommaOrLine (c: Cursor) (item: unit -> 'a) : 'a list =
    let firstStart = c.Start
    let firstColumn, _ = columnAndIndent c.Source firstStart
    let firstStartsLine = startsLine c
    let items = ResizeArray [ item () ]
    let mutable going = true
    while going do
        if c.Is Comma && c.OnSameLine then
            c.Take() |> ignore
            items.Add(item ())
        elif firstStartsLine && c.Token.IsSome && not c.OnSameLine && startsLine c && fst (columnAndIndent c.Source c.Start) = firstColumn then
            match c.Attempt item with
            | Some next -> items.Add next
            | None -> going <- false
        else going <- false
    List.ofSeq items

/// A lone positional field is its expression, positional fields are a tuple, and a named one makes a record.
let private fieldsToExpr (fields: RecordField list) (span: SourceSpan) =
    match fields with
    | [ PositionalField single ] -> single
    | _ when fields |> List.forall (function PositionalField _ -> true | NamedField _ -> false) ->
        Expression.at span (TupleExpr(fields |> List.choose (function PositionalField e -> Some e | NamedField _ -> None)))
    | _ -> Expression.at span (RecordExpr fields)

/// A string with holes: its text decoded, with each hole's expression. One with none is a literal (`literal`).
let rec private doubleQuoted (c: Cursor) : Expression =
    let start = c.Start
    let decoded (text: string) =
        match StringEscapes.decode text with
        | Some value -> value
        | None -> c.GiveUp "a backslash in a string that isn't an escape"
    let parts = ResizeArray<StringPart>()
    let addText (token: Token) =
        let written = c.Text token
        let text = written.Substring(1, written.Length - 2)
        c.NoteStringText (token.Start + 1) (token.End - 1)
        if text <> "" then parts.Add(StringText(decoded text))
    addText (c.Expect StringStart)
    let mutable going = true
    while going do
        // A hole that doesn't read makes the FParsec parser read the string as text, `{` and all.
        let hole = try expression Crossing c with NoMatch -> c.GiveUp "a hole that doesn't read, which the FParsec parser reads as text"
        parts.Add(StringExpr hole)
        if not (c.Is StringMiddle || c.Is StringEnd) then c.GiveUp "a hole that doesn't read, which the FParsec parser reads as text"
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
    match c.Attempt named with
    | Some field -> field
    | None -> PositionalField(expression Crossing c)

/// The fields in brackets after a name, at least one, and the span from the first to the last.
and private parenFields (c: Cursor) : RecordField list * SourceSpan =
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
    let trailing = if lambdaFollows c then Some(lambdaRef.Value c) else None
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
        let lambda = lambdaRef.Value c
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

/// `C(T).member` or `C(T).member(args)`: a member of a context's instance for a type, named by a capitalised context.
and private contextMember (c: Cursor) : Expression =
    let start = c.Start
    let contextName, _ = identifierWithSpan c
    if not (Char.IsUpper contextName[0]) || not (c.Adjacent && c.Is LeftParen) then raise NoMatch
    c.Take() |> ignore
    let types = sepBy1 c (fun () -> DescentTypes.typeExpr c) (fun () -> c.Expect Comma |> ignore)
    c.Expect RightParen |> ignore
    let contextType = TypeExpr.at (c.SpanFrom start) (TypeApply(contextName, types))
    if not (c.Adjacent && c.Is Dot) then raise NoMatch
    c.Take() |> ignore
    if not c.Adjacent then raise NoMatch
    let memberName, _ = name c
    let kind =
        if c.Adjacent && c.Is LeftParen then
            c.Take() |> ignore
            let args = if c.Is RightParen then [] else sepBy1 c (fun () -> expression Crossing c) (fun () -> c.Expect Comma |> ignore)
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
/// when every item is `key => value` (`Expressions.bracketItems`).
and private bracketItems (c: Cursor) : Choice<Choice<Expression list, Expression> list, Expression list> * SourceSpan =
    let start = c.Start
    c.Expect LeftBracket |> ignore
    if c.Adjacent && c.Is Bar then c.GiveUp "a table literal"
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
        let segments =
            items
            |> List.fold (fun segments item ->
                match item, segments with
                | Choice1Of2(element, _), Choice1Of2 elements :: rest -> Choice1Of2(elements @ [ element ]) :: rest
                | Choice1Of2(element, _), _ -> Choice1Of2 [ element ] :: segments
                | Choice2Of2 spread, _ -> Choice2Of2 spread :: segments) []
            |> List.rev
        Choice1Of2 segments, span
    elif entries.Length = items.Length then
        Choice2Of2(entries |> List.map (fun (key, value) -> Expression.from key (TupleExpr [ key; value ]))), span
    else c.GiveUp "a map literal with an item that isn't an entry, which is a mistake"

/// `[a, b]`, `[a, ...xs]` or `[k => v]`: a list, a concatenation of its runs and spreads, or a map.
and private list (c: Cursor) : Expression =
    match bracketItems c with
    | Choice1Of2 segments, span ->
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
        c.GiveUp "a record with a spread and a field without a name, which is a mistake"
    else
        let parts = parts |> List.map (function Choice1Of2 field -> field | Choice2Of2 spread -> PositionalField spread)
        Expression.at span (FunctionCall(SpreadSyntax.builtIn, None, [ Expression.at span (RecordExpr parts) ]))

/// A primary expression, `Expressions.primaryExprNoWs`, with its alternatives in that order. A keyword that doesn't start
/// one of them is read as a name, as the FParsec parser's `identifierNoWs` reads it: `pattern = pattern`.
and private primary (c: Cursor) : Expression =
    let start = c.Start
    let token = c.Token.Value
    let next = match c.At 1 with Code next -> Some next | _ -> None
    let nextAdjacent = next |> Option.exists (fun next -> next.Start = token.End)
    match token.Kind with
    | Keyword when constructKeywords.Contains(c.Text token) -> c.GiveUp $"`{c.Text token}`, which starts what the descent parser doesn't read yet"
    | LeftBrace -> lambdaRef.Value c
    | StringStart -> doubleQuoted c
    | Name when nextAdjacent && next |> Option.exists (fun next -> next.Kind = Operator && c.Text next = "!") ->
        c.GiveUp "a workflow keyword with `!`"
    | LeftParen when next |> Option.exists (fun next -> next.Kind = RightParen) ->
        c.Take() |> ignore
        c.Take() |> ignore
        Expression.at (c.SpanFrom start) UnitExpr
    | Name
    | Keyword
    | BacktickedName
    | Dollar ->
        match c.Attempt(fun () -> contextMember c) with
        | Some expr -> expr
        | None ->
            match c.Attempt(fun () -> call c) with
            | Some expr -> expr
            | None when token.Kind = Name && (c.Text token = "true" || c.Text token = "false") ->
                let literal, span = literal c
                Expression.at span (LiteralExpr literal)
            | None ->
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
        Expression.at (c.SpanFrom start) (IdentifierExpr "???")
    | LeftBracket -> list c
    | LeftParen -> bracketed c
    | _ -> raise NoMatch

/// A primary expression and the `.field`s after it, with the span of its text (`Expressions.memberAccessChain`).
and private accessed (c: Cursor) : Operand =
    // In a shorthand lambda's body, the first operand is the argument it leaves out, made up at the operator or `.`
    // written in its place, which takes up no text (`Expressions.implicitArgumentOperand`).
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
    while going do
        match c.Attempt field with
        | Some(fieldName, fieldSpan) ->
            text <- SourceSpan.between text fieldSpan
            expr <- Expression.at text (MemberAccess(expr, fieldName, Some fieldSpan))
        | None -> going <- false
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
            sepBy1 c update (fun () -> (if not c.OnSameLine then raise NoMatch); c.Expect Comma |> ignore; across c (fun c -> c.Token.IsSome) |> ignore)
    let updatesSpan = c.SpanFrom updatesStart
    let updated = Expression.at updatesSpan (RecordExpr updates)
    let span = SourceSpan.between text updatesSpan
    Expression.at span (FunctionCall(WithSyntax.builtIn, None, [ expr; updated ])), span

/// What a `\` applies the value before it to, with the span from the function's name: a call of a function with the
/// value as its first argument.
and private pipeTarget (c: Cursor) : (Identifier * SourceSpan * Expression list) * SourceSpan =
    if c.IsKeyword "match" then c.GiveUp "a `\\match`, whose arms the descent parser doesn't read yet"
    let start = c.Start
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
    let args = if lambdaFollows c then args @ [ lambdaRef.Value c ] else args
    (callee, calleeSpan, args), c.SpanFrom start

/// `value` and then each `\` step after it (`Expressions.withPipes`): on the line, or for `Crossing` on any line after
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
        let (callee, calleeSpan, args), targetSpan = pipeTarget c
        let acc, accText = result
        let span = SourceSpan.between accText targetSpan
        result <- Expression.at span (Pipe(acc, callee, Some calleeSpan, args)), span
    result

/// An operand: a primary expression after any `not` and `-` before it, and the pipes after it (`Expressions.operand`).
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
        | Some token when token.Kind = Operator && c.Text token = "!" && c.ImplicitArgumentNext.IsNone -> c.GiveUp "a prefix `!`, which is a mistake"
        | _ -> going <- false
    let term =
        prefixes
        |> Seq.rev
        |> Seq.fold (fun ((inner: Expression), innerText) (builtIn, prefixSpan) ->
            let span = SourceSpan.between prefixSpan innerText
            Expression.at span (FunctionCall(builtIn, None, [ inner ])), span) (accessed c)
    withPipes lines c term

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

/// Operands joined by operators that bind at least as tightly as `minimum`, after `parent`, the operator before them
/// (see FParsec's `OperatorPrecedenceParser`). Two operators of one precedence that don't group the same way, or don't
/// group, can't be written side by side.
and private climb (lines: Lines) (c: Cursor) (minimum: int) (parent: BinaryOperator option) : Operand =
    let mutable left = operand lines c
    let mutable previous = parent
    let mutable going = true
    while going do
        match operatorAt lines c with
        | Some op when op.Precedence >= minimum ->
            match previous with
            | Some p when p.Precedence = op.Precedence && (p.Associativity <> op.Associativity || op.Associativity = NonAssociative) ->
                c.GiveUp "two operators that can't be written side by side, which is a mistake"
            | _ -> ()
            let token = c.Take()
            let opSpan = c.SpanOf token
            if op.Text <> ".." then
                let isSpace (ch: char) = ch = ' ' || ch = '\t' || ch = '\r' || ch = '\n'
                let before = if token.Start > 0 then c.Source[token.Start - 1] else ' '
                let after = if token.End < c.Source.Length then c.Source[token.End] else ' '
                if not ((isSpace before || before = '{') && isSpace after) then c.GiveUp "an operator that isn't spaced, which is a mistake"
            match lines with
            | Crossing -> across c (fun c -> c.Token.IsSome) |> ignore
            | Inline -> if not c.OnSameLine then raise NoMatch
            let next = if op.Associativity = RightAssociative then op.Precedence else op.Precedence + 1
            let right, rightText = climb lines c next (Some op)
            let leftExpr, leftText = left
            let span = SourceSpan.between leftText rightText
            let kind =
                if op.Text = ".." then FunctionCall(RangeSyntax.builtIn, None, [ leftExpr; right ])
                else BinaryOp(op.Text, Some opSpan, leftExpr, right)
            left <- Expression.at span kind, span
            previous <- Some op
        | _ -> going <- false
    left

/// An expression as `Expressions.expression` reads it (`Crossing`), or `exprWithoutCrossingNewlines` (`Inline`).
and expression (lines: Lines) (c: Cursor) : Expression =
    if c.IsKeyword "match" then c.GiveUp "`match`, which starts what the descent parser doesn't read yet"
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
    if exceptNext then c.GiveUp "`except`, whose arms the descent parser doesn't read yet"
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
    let items = sepBy1 c (fun () -> expression Crossing c) (fun () -> (if not c.OnSameLine then raise NoMatch); c.Expect Comma |> ignore)
    match items with
    | [ single ] -> single
    | several -> Expression.at (c.SpanFrom start) (TupleExpr several)
