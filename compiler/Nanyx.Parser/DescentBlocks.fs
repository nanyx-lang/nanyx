/// The recursive-descent parser's lambdas, blocks and statements: the lambda forms of `Expressions.fs`, a def's value
/// written as an indented block, and the statements of a block (`def`, `rec`, `mut` and expression statements, several
/// on a line after `;`), read from the layout pass's tokens. It gives up at a lambda of match arms and at the
/// statements that come with the keyword-led expressions (`use`, `import`, a local type, an attribute), which are still
/// to be written (see the Work in progress page "The parser").
module Parser.Program.DescentBlocks

open Parser.Program.Lexer
open Parser.Program.Layout
open Parser.Program.DescentCursor
open Parser.Program.DescentExpressions

/// Skips the layout tokens at the cursor, as FParsec's `ws` skips the line breaks and indentation between a lambda's
/// statements, which may be at any column until its `}`.
let private skipLayout (c: Cursor) =
    while (match c.At 0 with Code _ -> false | _ -> true) do
        c.Index <- c.Index + 1

/// Whether the first code token at or after the cursor, past any layout tokens, is `kind`.
let private nextCodeIs (c: Cursor) (kind: TokenKind) =
    let index = c.Index
    skipLayout c
    let found = c.Is kind
    c.Index <- index
    found

/// Whether the cursor is at a closing bracket or brace, which ends whatever block is open inside it.
let private atClose (c: Cursor) = c.Is RightParen || c.Is RightBracket || c.Is RightBrace

/// A statement spanning from `start` to the last token taken, ending where its final expression does when it has one,
/// which may be in brackets that aren't part of it (`ParseState.spannedStatement`).
let private statementFrom (c: Cursor) (start: int) (kind: StatementKind) =
    let span = c.SpanFrom start
    let span =
        match Statement.finalExpression kind with
        | Some expr -> endingAt expr.Span span
        | None -> span
    Statement.at span kind

/// A block of `statements`, spanning from `start` and ending where its last statement does (`ParseState.blockSpanned`).
let private blockOf (c: Cursor) (start: int) (statements: Statement list) =
    let span = c.SpanFrom start
    let span =
        match List.tryLast statements with
        | Some last -> endingAt last.Span span
        | None -> span
    Expression.at span (Block statements)

/// `:` and a def's type, after its name on its line or at the start of a line indented further than the def's
/// (`Types.defNameAndType`).
let private defType (c: Cursor) : TypeExpr option =
    if c.Is Colon && c.Token.IsSome then
        c.Take() |> ignore
        Some(DescentTypes.typeSignature c)
    else None

/// A statement: `def`, `export def`, `rec`, `mut`, or an expression (`TopLevel`'s `statementRef`).
let rec statement (c: Cursor) : Statement =
    let start = c.Start
    match c.Token with
    | Some token when token.Kind = Keyword ->
        match c.Text token with
        | "def" -> definition c start false
        | "export" when (match c.At 1 with Code next -> c.Text next = "def" | _ -> false) ->
            c.Take() |> ignore
            definition c start true
        | "rec" -> wrappedDefinition c start RecSyntax.builtIn
        | "mut" when (match c.At 1 with Code next -> next.Kind = Name | _ -> false) -> wrappedDefinition c start MutSyntax.builtIn
        | "import" | "type" | "context" | "use" | "export" | "mut" ->
            c.GiveUp $"a `{c.Text token}` statement, which the descent parser doesn't read yet"
        | _ -> expressionStatement c start
    | Some token when token.Kind = At -> c.GiveUp "an attribute statement"
    | _ -> expressionStatement c start

/// An expression on a line of its own (`TopLevel`'s `exprStatement`).
and private expressionStatement (c: Cursor) (start: int) =
    if c.IsKeyword "match" then c.GiveUp "`match`, which starts what the descent parser doesn't read yet"
    statementFrom c start (ExprStatement(expression Inline c))

/// `def name: T = value`, from `start`, where an `export` before it starts.
and private definition (c: Cursor) (start: int) (isExport: bool) =
    c.Expect(Keyword, "def") |> ignore
    let name, nameSpan, typeOpt = nameAndType c
    let value = defValue c
    c.Declare DefValue value.Id [ name, nameSpan ]
    c.NoteDocumentable start nameSpan.Start
    statementFrom c start (DefStatement(isExport, name, typeOpt, value))

/// `rec name = value` or `mut name = value`: a def whose value is a call to the built-in `builtIn` with the value
/// written, spanning the value for `rec` and the declaration for `mut` (see `RecSyntax` and `MutSyntax`).
and private wrappedDefinition (c: Cursor) (start: int) (builtIn: Identifier) =
    c.Take() |> ignore
    let name, nameSpan, typeOpt = nameAndType c
    let written = defValue c
    let span = if builtIn = MutSyntax.builtIn then { c.SpanFrom start with End = written.Span.End } else written.Span
    let value = Expression.at span (FunctionCall(builtIn, None, [ written ]))
    c.Declare DefValue value.Id [ name, nameSpan ]
    c.NoteDocumentable start nameSpan.Start
    statementFrom c start (DefStatement(false, name, typeOpt, value))

/// A def's name, qualified with dots, its span, and its type, if it writes one. A pattern in its place is a
/// destructuring def.
and private nameAndType (c: Cursor) : Identifier * SourceSpan * TypeExpr option =
    let start = c.Start
    let first = c.Attempt(fun () -> name c)
    let parts = ResizeArray(Option.toList (Option.map fst first))
    while first.IsSome && c.Adjacent && c.Is Dot do
        c.Take() |> ignore
        parts.Add(fst (name c))
    let nameSpan = c.SpanFrom start
    if first.IsNone || not (c.Is Colon || c.Is Equals) then c.GiveUp "a destructuring def"
    String.concat "." parts, nameSpan, defType c

/// `= value` after a def's name and type: on the line, or at the start of a line indented further than the def's. The
/// value is an indented block on the lines after it, or what is on the line.
and private defValue (c: Cursor) : Expression =
    c.Expect Equals |> ignore
    match c.At 0 with
    | Indent _ ->
        c.Index <- c.Index + 1
        indentedBlock c
    | Code _ when c.OnSameLine -> DescentExpressions.defValue c
    | _ -> c.GiveUp "a def's value on the next line, not indented further than the block it is in"

/// The statements of a block opened by an `Indent`, from its first, to the `Dedent` that closes it
/// (`Expressions.blockExpr`). A line indented further than the block that its statement before didn't take is a
/// statement of its own, as the FParsec parser reads it.
and indentedBlock (c: Cursor) : Expression =
    let start = c.Start
    let statements = ResizeArray(statementsOnLine c)
    let mutable going = true
    while going do
        match c.At 0 with
        | Newline _ ->
            c.Index <- c.Index + 1
            statements.AddRange(statementsOnLine c)
        | Dedent _ ->
            c.Index <- c.Index + 1
            going <- false
        | Code token when token.Kind <> EndOfFile && not c.OnSameLine && not (atClose c) -> statements.AddRange(statementsOnLine c)
        | _ -> going <- false
    blockOf c start (List.ofSeq statements)

/// The statements on one line: one, and then each after a `;` (`Expressions.statementsOnLine`).
and private statementsOnLine (c: Cursor) : Statement list =
    let statements = ResizeArray [ statement c ]
    while c.Is Semicolon && c.OnSameLine do
        c.Take() |> ignore
        if not c.OnSameLine || c.Is Semicolon || c.Is RightBrace || c.Is RightParen || c.Is RightBracket then
            c.GiveUp "a `;` with no statement after it, which is a mistake"
        statements.Add(statement c)
    List.ofSeq statements

/// A body on the line it starts on: what `value` reads, or a statement that defines a name, and then each statement
/// after a `;`, which make it a block of them all (`Expressions.sameLineBody`).
and private sameLineBody (c: Cursor) (value: Cursor -> Expression) : Expression =
    let start = c.Start
    let defines = c.IsKeyword "def" || c.IsKeyword "rec" || c.IsKeyword "mut"
    let leading = if defines then Choice1Of2 [ statement c ] else Choice2Of2(value c)
    let rest = ResizeArray()
    while c.Is Semicolon && c.OnSameLine do
        c.Take() |> ignore
        if not c.OnSameLine || c.Is Semicolon || c.Is RightBrace || c.Is RightParen || c.Is RightBracket then
            c.GiveUp "a `;` with no statement after it, which is a mistake"
        rest.Add(statement c)
    match leading, List.ofSeq rest with
    | Choice2Of2 value, [] -> value
    | leading, rest ->
        let first =
            match leading with
            | Choice1Of2 statements -> statements
            | Choice2Of2 value -> [ Statement.at value.Span (ExprStatement value) ]
        let statements = first @ rest
        let span = c.SpanFrom start
        Expression.at (endingAt (List.last statements).Span span) (Block statements)

/// A lambda's body after its `->`, or after its `{` when it has no parameters: statements on the lines after it, any
/// number at any column until the `}`, or a body on the line (`Expressions.blockBody`).
and private lambdaBody (c: Cursor) : Expression =
    if c.Is RightBrace then raise NoMatch
    if c.OnSameLine then sameLineBody c (expression Crossing)
    else
        skipLayout c
        let start = c.Start
        let statements = ResizeArray()
        while not (c.Is RightBrace) do
            if c.IsKeyword "match" then c.GiveUp "`match`, which starts what the descent parser doesn't read yet"
            statements.AddRange(statementsOnLine c)
            skipLayout c
        blockOf c start (List.ofSeq statements)

/// A lambda parameter: a name and its type if written, `x` or `x: int` (`Types.typedIdentifier`), noted as declared by
/// the lambda.
and private parameter (c: Cursor) (annotation: Cursor -> TypeExpr) : Identifier * TypeExpr option =
    let name, span = identifierWithSpan c
    c.NoteName name span
    let typeOpt =
        if c.Is Colon && c.OnSameLine then
            c.Take() |> ignore
            Some(annotation c)
        else None
    name, typeOpt

/// A parameter in brackets, `(x: int -> int)`, whose type may be a function type without brackets of its own.
and private bracketedParameter (c: Cursor) =
    c.Expect LeftParen |> ignore
    let parameter = parameter c DescentTypes.typeExpr
    c.Expect RightParen |> ignore
    parameter

/// The parameters before a lambda's `->`, each a name, a bracketed name, `mut cell`, or a pattern with the names it
/// binds.
and private parameters (c: Cursor) : Choice<Identifier * TypeExpr option, Pattern * (Identifier * SourceSpan) list> list =
    let one () =
        if c.IsKeyword "mut" then
            c.Take() |> ignore
            let name, held = parameter c DescentTypes.typeUnion
            Choice1Of2(name, Some(CellPassingSyntax.parameterType held))
        else
            match c.Attempt(fun () -> bracketedParameter c) with
            | Some named -> Choice1Of2 named
            | None ->
                // A pattern the descent parser gives up at is no pattern parameter here: the FParsec parser tries the
                // next reading, a name, as it does after any pattern that fails.
                let patternParameter () =
                    let pattern, names =
                        try c.Collecting(fun () -> DescentPatterns.pattern c)
                        with GiveUp _ -> raise NoMatch
                    match pattern.Kind with
                    | IdentifierPattern _
                    | WildcardPattern -> raise NoMatch
                    | _ -> Choice2Of2(pattern, names)
                match c.Attempt patternParameter with
                | Some destructured -> destructured
                | None -> Choice1Of2(parameter c DescentTypes.typeUnion)
    if c.Is Arrow then [] else sepBy1 c one (fun () -> c.Expect Comma |> ignore)

/// What follows `{` and its spaces, as a function of the lambda's span: the forms of `Expressions.lambdaRef`, in its
/// order.
and private lambdaKind (c: Cursor) : SourceSpan -> ExpressionKind =
    let shorthandOperator () =
        match c.Token with
        | Some token when token.Kind = Operator && (binaryOperatorNamed (c.Text token)).IsSome -> token
        | _ -> raise NoMatch
    let empty () =
        skipLayout c
        if not (c.Is RightBrace) then raise NoMatch
        fun span -> Lambda([], Expression.at span UnitExpr, false)
    let binaryAlone () =
        let token = shorthandOperator ()
        c.Take() |> ignore
        skipLayout c
        if not (c.Is RightBrace) then raise NoMatch
        let op = c.Text token
        let opSpan = c.SpanOf token
        fun span ->
            let made kind = Expression.at span kind
            Lambda(
                [ (implicitArgument, None); (implicitSecondArgument, None) ],
                made (BinaryOp(op, Some opSpan, made (IdentifierExpr implicitArgument), made (IdentifierExpr implicitSecondArgument))),
                false)
    let withArgument () =
        let leftOut =
            match c.Attempt shorthandOperator with
            | Some token -> c.SpanOf token
            | None ->
                if c.Is Dot && (match c.At 1 with Code next -> (next.Kind = Name || next.Kind = Keyword) && next.Start = c.Start + 1 | _ -> false) then
                    c.SpanOf c.Token.Value
                else raise NoMatch
        c.ImplicitArgumentNext <- Some leftOut
        let body = expression Crossing c
        skipLayout c
        fun _ -> Lambda([ (implicitArgument, None) ], body, false)
    let withParameters () =
        if c.IsKeyword "context" then c.GiveUp "a lambda that names its context's instance"
        let written = parameters c
        if c.Is Colon && c.OnSameLine then c.GiveUp "a lambda's result type"
        if not (c.Is Arrow && c.OnSameLine) then raise NoMatch
        c.Take() |> ignore
        if nextCodeIs c Bar then c.GiveUp "a lambda of match arms"
        let body = lambdaBody c
        skipLayout c
        let destructured =
            written
            |> List.indexed
            |> List.choose (function
                | index, Choice2Of2(pattern, names) -> Some(index + 1, pattern, names)
                | _ -> None)
        match written, destructured with
        | [ Choice2Of2({ Kind = UnitPattern }, _) ], _ -> fun _ -> Lambda([], body, true)
        | _, [] -> fun _ -> Lambda(written |> List.choose (function Choice1Of2 named -> Some named | _ -> None), body, false)
        | _ ->
            let named =
                written |> List.mapi (fun index parameter ->
                    match parameter with
                    | Choice1Of2 named -> named
                    | Choice2Of2 _ -> patternArgument (index + 1), None)
            c.Declare ArmBody body.Id (destructured |> List.collect (fun (_, _, names) -> names))
            fun span ->
                let scrutinees = destructured |> List.map (fun (position, pattern, _) -> Expression.at pattern.Span (IdentifierExpr(patternArgument position)))
                let patterns = destructured |> List.map (fun (_, pattern, _) -> pattern)
                Lambda(named, Expression.at span (Match(scrutinees, [ patterns, body ])), false)
    let withoutParameters () =
        let body = lambdaBody c
        skipLayout c
        fun _ -> Lambda([], body, false)
    if nextCodeIs c Bar then c.GiveUp "a lambda of match arms"
    match c.Attempt empty with
    | Some kind -> kind
    | None ->
        match c.Attempt binaryAlone with
        | Some kind -> kind
        | None ->
            match c.Attempt withArgument with
            | Some kind -> kind
            | None ->
                match c.Attempt withParameters with
                | Some kind -> kind
                | None -> withoutParameters ()

/// A lambda, `{ ... }`, with the names its parameters declare (`Expressions.lambdaRef`).
and lambda (c: Cursor) : Expression =
    let start = c.Start
    let kind, names =
        c.Collecting(fun () ->
            c.Expect LeftBrace |> ignore
            let kind = lambdaKind c
            skipLayout c
            c.Expect RightBrace |> ignore
            kind)
    let span = c.SpanFrom start
    let lambda = Expression.at span (kind span)
    c.Declare LambdaParameters lambda.Id names
    lambda

/// Fills the forward references the files before this one read lambdas through.
let fill () = lambdaRef.Value <- lambda

/// A def's value, `= value` read from after the `=`: what is on its line, or the block on the lines after it.
let defValueAfterEquals (c: Cursor) : Expression =
    fill ()
    match c.At -1 with
    | Indent _ -> indentedBlock c
    | _ -> DescentExpressions.defValue c

/// A statement's expression, read as `exprWithoutCrossingNewlines` reads it.
let statementValue (c: Cursor) : Expression =
    fill ()
    expression Inline c
