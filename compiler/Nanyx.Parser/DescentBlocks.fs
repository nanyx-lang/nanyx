/// The recursive-descent parser's lambdas, blocks, match arms and statements: the lambda forms of `Expressions.fs`, a
/// def's value written as an indented block, a match's arms, and the statements of a block (`def`, `rec`, `mut`, `use`
/// and expression statements, several on a line after `;`), read from the layout pass's tokens. It gives up at an
/// `import`, a local type and an attribute in a block, which come with the top level.
module Parser.Program.DescentBlocks

open Parser.Program.Lexer
open Parser.Program.Layout
open Parser.Program.DescentCursor
open Parser.Program.DescentExpressions

/// Skips the layout tokens at the cursor, as FParsec's `ws` skips the line breaks and indentation between a lambda's
/// statements, which may be at any column until its `}`.
let internal skipLayout (c: Cursor) =
    while (match c.At 0 with Code _ -> false | _ -> true) do
        c.Index <- c.Index + 1

/// Whether the first code token at or after the cursor, past any layout tokens, is `kind`.
let internal nextCodeIs (c: Cursor) (kind: TokenKind) =
    let index = c.Index
    skipLayout c
    let found = c.Is kind
    c.Index <- index
    found

/// Where the first code token at or after the cursor starts, past any layout tokens.
let internal nextCodeStart (c: Cursor) =
    let index = c.Index
    skipLayout c
    let start = c.Start
    c.Index <- index
    start

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
        | "use" ->
            match c.Attempt(fun () -> useStatement c start) with
            | Some statement -> statement
            | None -> expressionStatement c start
        | "import" ->
            c.Take() |> ignore
            if not c.OnSameLine then raise NoMatch
            statementFrom c start (ImportStatement(Forward.importItems.Value c))
        | "type" | "context" -> typeDefinitionStatement c start
        | "export" when (match c.At 1 with Code next -> c.Text next = "type" || c.Text next = "context" | _ -> false) ->
            typeDefinitionStatement c start
        | "export" | "mut" ->
            c.GiveUp $"a `{c.Text token}` statement, which the descent parser doesn't read yet"
        | _ -> expressionStatement c start
    // `@allow(default/unusedBindings)`, which applies to the local def after it.
    | Some token when token.Kind = At ->
        c.Take() |> ignore
        if not c.Adjacent then raise NoMatch
        let attributeName, _ = identifierWithSpan c
        let argument = if c.Adjacent && c.Is LeftParen then Some(Forward.attributeArgument.Value c) else None
        statementFrom c start (AttributeStatement(attributeName, argument))
    | Some token when token.Kind = Name && c.Text token = "scoped" && (match c.At 1 with Code next -> next.Kind = Keyword | _ -> false) ->
        typeDefinitionStatement c start
    | _ -> expressionStatement c start

/// A local type's or context's definition.
and private typeDefinitionStatement (c: Cursor) (start: int) =
    match Forward.typeDefinition.Value c with
    | TypeDef(name, modifiers, parameters, body) -> statementFrom c start (TypeDefStatement(name, modifiers, parameters, body))
    | ValueDef _ -> raise NoMatch

/// One line's statement as the statements it stands for: one, or the defs a destructuring def desugars into
/// (`TopLevel`'s `statementItemsRef`).
and private statementItems (c: Cursor) : Statement list =
    let start = c.Start
    let isDef = c.IsKeyword "def" || (c.IsKeyword "export" && (match c.At 1 with Code next -> c.Text next = "def" | _ -> false))
    if isDef then
        match c.Attempt(fun () -> destructuringDefinition c start) with
        | Some statements -> statements
        | None -> [ statement c ]
    else [ statement c ]

/// `def pattern = value`, from `start`, as the defs it desugars into: the value bound to a made-up name, and a def of
/// each name the pattern binds, each spanning the whole (see `Destructuring.destructuringDefWith`). A pattern the
/// descent parser gives up at is no destructuring def here, as an ordinary def's name is none.
and internal destructuringDefinition (c: Cursor) (start: int) : Statement list =
    let isExport = c.IsKeyword "export"
    if isExport then c.Take() |> ignore
    let defToken = c.Expect(Keyword, "def")
    let defSpan = c.SpanOf defToken
    let temp = DestructuringSyntax.temp (int64 defSpan.StartLine + 1L) (int64 defSpan.StartColumn + 1L)
    let pattern, names =
        c.Collecting(fun () ->
            try
                let patternStart = c.Start
                match sepBy1 c (fun () -> DescentPatterns.pattern c) (fun () -> c.Expect Comma |> ignore) with
                | [ single ] -> single
                | several -> Pattern.at (c.SpanFrom patternStart) (TuplePattern several)
            with GiveUp _ -> raise NoMatch)
    match pattern.Kind with
    | IdentifierPattern _ -> raise NoMatch
    | _ -> ()
    if not (c.Is Equals) then raise NoMatch
    let value = defValue c
    let declareBindings (bindings: (Identifier * Expression) list) =
        for name, bound in bindings do
            c.Declare DefValue bound.Id (names |> List.filter (fun (declared, _) -> declared = name))
        match names with
        | (_, first) :: _ -> c.NoteDocumentable start first.Start
        | [] -> ()
        bindings
    let bindings =
        match Destructuring.destructuringBindings temp pattern value with
        | Some bindings -> declareBindings bindings
        | None ->
            let body, bindings = Destructuring.matchingBindings temp pattern (names |> List.map fst |> List.distinct) value
            c.Declare ArmBody body.Id names
            declareBindings bindings
    let span = endingAt (snd bindings.Head).Span (c.SpanFrom start)
    bindings |> List.mapi (fun index (name, bound) -> Statement.at span (DefStatement(isExport && index > 0, name, None, bound)))

/// An expression on a line of its own (`TopLevel`'s `exprStatement`).
and private expressionStatement (c: Cursor) (start: int) =
    statementFrom c start (ExprStatement(expression Inline c))

/// `use value`, with no `in` after it: the value's instance is loaded for the statements after it in the block.
and private useStatement (c: Cursor) (start: int) =
    c.Expect(Keyword, "use") |> ignore
    let value = useValue c
    let index = c.Index
    skipLayout c
    let inFollows = c.IsKeyword "in"
    c.Index <- index
    if inFollows then raise NoMatch
    statementFrom c start (UseStatement value)

/// The value a `use` loads: an instance written out, `Add(Point)(zero = ..., ...)`, which is a block that defines it
/// under a made-up name and gives it back (see `UseInstanceSyntax`), or any expression, `use addInt`.
and internal useValue (c: Cursor) : Expression =
    let instance () =
        let start = c.Start
        let contextName, _ = identifierWithSpan c
        if not (System.Char.IsUpper contextName[0]) || not (c.Adjacent && c.Is LeftParen) then raise NoMatch
        c.Take() |> ignore
        let typeArgument () =
            let named () =
                let argStart = c.Start
                let argName, _ = identifierWithSpan c
                if not (c.Is Equals && c.OnSameLine) then raise NoMatch
                c.Take() |> ignore
                let value = DescentTypes.typeExpr c
                TypeExpr.at (c.SpanFrom argStart) (TypeNamedArgument(argName, value))
            match c.Attempt named with
            | Some argument -> argument
            | None -> DescentTypes.typeExpr c
        let arguments = sepBy1 c typeArgument (fun () -> c.Expect Comma |> ignore)
        c.Expect RightParen |> ignore
        let context = TypeExpr.at (c.SpanFrom start) (TypeApply(contextName, arguments))
        if c.Is Equals then c.GiveUp "`use C(T) = ...`, which is a mistake"
        if not (c.Adjacent && c.Is LeftParen) then raise NoMatch
        let fields, membersSpan = parenFields c
        let span = c.SpanFrom start
        let members = Expression.at membersSpan (RecordExpr fields)
        let definition = Statement.at span (DefStatement(false, UseInstanceSyntax.name, Some context, members))
        let result = Statement.at span (ExprStatement(Expression.at span (IdentifierExpr UseInstanceSyntax.name)))
        Expression.at span (Block [ definition; result ])
    match c.Attempt instance with
    | Some instance -> instance
    | None -> expression Crossing c

/// Arms read by `arm`, each from its `|`, run as the layout pass lays them out: on the line, or each starting a line of
/// the block of arms the first opens. A first arm on the line after, at the column of the arms of an enclosing match,
/// takes the arms after it at that column, as the FParsec parser's `firstArmIndent` does. An arm `isLast` picks ends
/// them, as an `if`'s `else` arm does. The `Dedent` that closes each block of arms they open is theirs.
and internal armsWith (c: Cursor) (isLast: 'a -> bool) (arm: unit -> 'a list) : 'a list =
    let mutable opened = 0
    let mutable onNewLines = false
    let mutable first = true
    let atBar () =
        match c.At 0 with
        | Code token -> token.Kind = Bar
        | Indent _ when nextCodeIs c Bar ->
            c.Index <- c.Index + 1
            opened <- opened + 1
            true
        | Newline _ when (opened > 0 || onNewLines || first) && nextCodeIs c Bar ->
            if first then onNewLines <- true
            c.Index <- c.Index + 1
            true
        | _ -> false
    if not (atBar ()) then raise NoMatch
    first <- false
    let arms = ResizeArray(arm ())
    while not (arms |> Seq.exists isLast) && atBar () do
        arms.AddRange(arm ())
    for _ in 1..opened do
        match c.At 0 with
        | Dedent _ -> c.Index <- c.Index + 1
        | _ -> c.GiveUp "a block of arms the layout pass closes somewhere else"
    List.ofSeq arms

/// One arm: its alternatives, each patterns separated by commas, and the body they share after `->`, which declares
/// the names they bind (`Expressions.armAlternativesAfter`). Each alternative is an arm of its own.
and private arm (c: Cursor) : MatchArm list =
    let alternatives, names =
        c.Collecting(fun () ->
            let alternative () = sepBy1 c (fun () -> DescentPatterns.pattern c) (fun () -> c.Expect Comma |> ignore)
            let all = ResizeArray [ alternative () ]
            let mutable going = true
            while going do
                match c.At 0 with
                | Code token when token.Kind = Bar ->
                    c.Take() |> ignore
                    all.Add(alternative ())
                | Newline _ when nextCodeIs c Bar ->
                    c.Index <- c.Index + 1
                    c.Take() |> ignore
                    all.Add(alternative ())
                | _ -> going <- false
            List.ofSeq all)
    if not (c.Is Arrow) then raise NoMatch
    c.Take() |> ignore
    let body = armBody c
    c.Declare ArmBody body.Id names
    alternatives |> List.map (fun patterns -> patterns, body)

/// An arm's body after its `->`: a block on the lines after it, or a body on the line (`Expressions.matchArmBody`).
and internal armBody (c: Cursor) : Expression =
    match c.At 0 with
    | Indent _ ->
        c.Index <- c.Index + 1
        indentedBlock c
    | Code _ when c.OnSameLine -> sameLineBody c (expression Inline)
    | _ -> c.GiveUp "an arm's body the layout pass doesn't open"

/// A match's arms, each from its `|` (`Expressions.matchArms`).
and arms (c: Cursor) : MatchArm list =
    armsWith c (fun _ -> false) (fun () ->
        c.Expect Bar |> ignore
        arm c)

/// What follows `except`: arms, or one arm without its `|` (`Expressions.exceptArms`).
and exceptArms (c: Cursor) : MatchArm list =
    if nextCodeIs c Bar then arms c
    else
        skipLayout c
        arm c

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
and internal nameAndType (c: Cursor) : Identifier * SourceSpan * TypeExpr option =
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
and internal defValue (c: Cursor) : Expression =
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
    let statements = ResizeArray(statementItems c)
    while c.Is Semicolon && c.OnSameLine do
        c.Take() |> ignore
        if not c.OnSameLine || c.Is Semicolon || c.Is RightBrace || c.Is RightParen || c.Is RightBracket then
            c.GiveUp "a `;` with no statement after it, which is a mistake"
        statements.AddRange(statementItems c)
    List.ofSeq statements

/// A body on the line it starts on: what `value` reads, or a statement that defines a name, and then each statement
/// after a `;`, which make it a block of them all (`Expressions.sameLineBody`).
and internal sameLineBody (c: Cursor) (value: Cursor -> Expression) : Expression =
    let start = c.Start
    let defines = c.IsKeyword "def" || c.IsKeyword "rec" || c.IsKeyword "mut"
    let leading = if defines then Choice1Of2(statementItems c) else Choice2Of2(value c)
    let rest = ResizeArray()
    while c.Is Semicolon && c.OnSameLine do
        c.Take() |> ignore
        if not c.OnSameLine || c.Is Semicolon || c.Is RightBrace || c.Is RightParen || c.Is RightBracket then
            c.GiveUp "a `;` with no statement after it, which is a mistake"
        rest.AddRange(statementItems c)
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
    // `context l` or `context l: Log`, the names of the instances of the contexts the lambda is given, before its
    // parameters, with a comma after the last if parameters follow (see `ContextNames`).
    let contextNames () =
        if not (c.IsKeyword "context" && (match c.At 1 with Code next -> next.Kind = Name | _ -> false)) then None
        else
            let one () =
                c.Expect(Keyword, "context") |> ignore
                let instance, span = identifierWithSpan c
                c.NoteName instance span
                let typeOpt =
                    if c.Is Colon && c.OnSameLine then
                        c.Take() |> ignore
                        Some(DescentTypes.typeUnion c)
                    else None
                instance, typeOpt, span
            let names = sepBy1 c one (fun () -> c.Expect Comma |> ignore; if not (c.IsKeyword "context") then raise NoMatch)
            if c.Is Comma then c.Take() |> ignore
            Some names
    let underContextNames (names: (Identifier * TypeExpr option * SourceSpan) list option) (body: Expression) =
        match names with
        | Some names -> Expression.at body.Span (ContextNames(names, body))
        | None -> body
    // `{ (x: int): int -> ... }`: the lambda's result type, its body a block that defines its value under a made-up
    // name and gives it back (see `LambdaResultSyntax`).
    let withResult (resultType: TypeExpr option) (body: Expression) =
        match resultType with
        | Some resultType ->
            let made kind = Expression.at body.Span kind
            made (Block [ Statement.at body.Span (DefStatement(false, LambdaResultSyntax.name, Some resultType, body))
                          Statement.at body.Span (ExprStatement(made (IdentifierExpr LambdaResultSyntax.name))) ])
        | None -> body
    let withParameters () =
        let contexts = c.Attempt contextNames |> Option.flatten
        let written = parameters c
        let resultType =
            if c.Is Colon && c.OnSameLine then
                c.Take() |> ignore
                Some(DescentTypes.typeUnion c)
            else None
        if not (c.Is Arrow && c.OnSameLine) then raise NoMatch
        c.Take() |> ignore
        // Names and then arms match the arms against the parameters, which stay in scope in each arm.
        if nextCodeIs c Bar && contexts.IsNone && resultType.IsNone then
            let named = written |> List.map (function Choice1Of2 named -> named | Choice2Of2 _ -> c.GiveUp "a pattern parameter before arms, which is a mistake")
            let armsStart = nextCodeStart c
            let written = arms c
            let armsSpan = c.SpanFrom armsStart
            skipLayout c
            fun span ->
                let scrutinees = named |> List.map (fun (name, _) -> Expression.at armsSpan (IdentifierExpr name))
                Lambda(named, Expression.at span (Match(scrutinees, written)), false)
        else
        let body = withResult resultType (lambdaBody c)
        skipLayout c
        let destructured =
            written
            |> List.indexed
            |> List.choose (function
                | index, Choice2Of2(pattern, names) -> Some(index + 1, pattern, names)
                | _ -> None)
        match written, destructured with
        | [ Choice2Of2({ Kind = UnitPattern }, _) ], _ -> fun _ -> Lambda([], underContextNames contexts body, true)
        | _, [] -> fun _ -> Lambda(written |> List.choose (function Choice1Of2 named -> Some named | _ -> None), underContextNames contexts body, false)
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
                Lambda(named, underContextNames contexts (Expression.at span (Match(scrutinees, [ patterns, body ]))), false)
    let withoutParameters () =
        let body = lambdaBody c
        skipLayout c
        fun _ -> Lambda([], body, false)
    // `{ | pattern -> ... }`: arms, matched against as many arguments as the most patterns an arm has.
    let matchArms () =
        let written = arms c
        skipLayout c
        fun span ->
            let made kind = Expression.at span kind
            let arity = written |> List.map (fun (patterns, _) -> patterns.Length) |> List.max
            let names = [ 1..arity ] |> List.map (fun index -> ($"arg{index}", None))
            Lambda(names, made (Match(names |> List.map (fun (name, _) -> made (IdentifierExpr name)), written)), false)
    if nextCodeIs c Bar then matchArms () else
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
