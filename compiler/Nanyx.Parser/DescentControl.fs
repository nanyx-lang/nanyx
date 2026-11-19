/// The recursive-descent parser's expressions that start with a keyword: `match`, `if` (with `is`, and with arms),
/// `for`, `while`, `do`, `use ... in`, the workflow keywords `return`, `try`, `yield` and `name!`, a cell passed as
/// `mut x`, and a type passed as `type T` (`Expressions.fs`). It fills the forward references the files before it read
/// through (`Forward`), and has the entry points the corpus check reads with.
module Parser.Program.DescentControl

open Parser.Program.Lexer
open Parser.Program.Layout
open Parser.Program.DescentCursor
open Parser.Program.DescentExpressions
open Parser.Program.DescentBlocks

/// The block a single expression stands for: that expression (as a match's or a `use ... in`'s indented one is).
let private unwrapSingle (block: Expression) =
    match block.Kind with
    | Block [ { Kind = ExprStatement expr } ] -> expr
    | _ -> block

/// `match` and its scrutinees, on its line or as a block on the lines after it, then its arms.
let private matchExpression (c: Cursor) =
    let start = c.Start
    c.Expect(Keyword, "match") |> ignore
    let scrutinees =
        match c.At 0 with
        | Indent _ ->
            c.Index <- c.Index + 1
            [ unwrapSingle (indentedBlock c) ]
        | Code _ when c.OnSameLine -> sepBy1 c (fun () -> expression Crossing c) (fun () -> c.Expect Comma |> ignore)
        | _ -> c.GiveUp "a match's scrutinee the layout pass doesn't open"
    // With no arms, the FParsec parser reads `match` as a name, and fails after it.
    let written = try arms c with NoMatch -> c.GiveUp "a `match` with no arms, which is a mistake"
    Expression.at (c.SpanFrom start) (Match(scrutinees, written))

/// A branch after `then` or `else`: a block on the lines after it, or a body (`Expressions.ifExpr`'s `branch`).
let private branch (c: Cursor) =
    match c.At 0 with
    | Indent _ ->
        c.Index <- c.Index + 1
        indentedBlock c
    | Code _ -> sameLineBody c (expression Crossing)
    | _ -> c.GiveUp "a branch the layout pass doesn't open"

/// An if's head: `value is pattern`, or a condition.
let private ifHead (c: Cursor) : Choice<Expression, Pattern * Expression> =
    let value = expression Crossing c
    if c.IsKeyword "is" then
        c.Take() |> ignore
        Choice2Of2(DescentPatterns.pattern c, value)
    else Choice1Of2 value

/// A head and the branch `readBranch` reads after it, which declares the names the head's pattern binds.
let private headThen (c: Cursor) (readBranch: unit -> Expression) =
    let head, names = c.Collecting(fun () -> ifHead c)
    let body = readBranch ()
    c.Declare ArmBody body.Id names
    head, body

/// An if spanning `span`: a condition's, or an if-match, a match whose second arm's wildcard is made up at the else
/// branch. A left-out `else` is `()` made up at the then branch.
let private desugarIf (span: SourceSpan) head (thenExpr: Expression) (elseOpt: Expression option) =
    let elseExpr = elseOpt |> Option.defaultWith (fun () -> Expression.from thenExpr UnitExpr)
    match head with
    | Choice1Of2 condition -> Expression.at span (IfExpr(condition, thenExpr, elseExpr))
    | Choice2Of2(pattern, value) ->
        Expression.at span (Match([ value ], [ [ pattern ], thenExpr; [ Pattern.at elseExpr.Span WildcardPattern ], elseExpr ]))

/// `if head then a else b`, or with arms, `if | head -> a | else -> b`, nested ifs (`Expressions.ifExpr`).
let private ifExpression (c: Cursor) =
    let start = c.Start
    c.Expect(Keyword, "if") |> ignore
    if nextCodeIs c Bar then
        // Each arm, with the span from its `|`: a head and its body, or `else` and its.
        let arm () =
            let armStart = c.Start
            c.Expect Bar |> ignore
            if c.IsKeyword "else" then
                c.Take() |> ignore
                c.Expect Arrow |> ignore
                let body = armBody c
                [ Choice2Of2(body, c.SpanFrom armStart) ]
            else
                let head, body = headThen c (fun () -> c.Expect Arrow |> ignore; armBody c)
                [ Choice1Of2((head, body), c.SpanFrom armStart) ]
        let written = armsWith c (function Choice2Of2 _ -> true | Choice1Of2 _ -> false) arm
        let conditions = written |> List.choose (function Choice1Of2 arm -> Some arm | Choice2Of2 _ -> None)
        let elseArm = written |> List.tryPick (function Choice2Of2 arm -> Some arm | Choice1Of2 _ -> None)
        if conditions.IsEmpty then c.GiveUp "an if of arms with only `else`, which is a mistake"
        let lastText = match elseArm with Some(_, text) -> text | None -> snd (List.last conditions)
        let rest =
            List.foldBack (fun ((head, body), armText) rest ->
                Some(desugarIf (SourceSpan.between armText lastText) head body rest)) (List.tail conditions) (Option.map fst elseArm)
        let (head, body), _ = List.head conditions
        desugarIf (c.SpanFrom start) head body rest
    else
        let head, thenExpr =
            headThen c (fun () ->
                c.Expect(Keyword, "then") |> ignore
                branch c)
        let elseOpt =
            if c.IsKeyword "else" then
                c.Take() |> ignore
                Some(branch c)
            else None
        desugarIf (c.SpanFrom start) head thenExpr elseOpt

/// `for iterables | arms` or `for iterables -> body`: a call to the built-in "__for" with the iterables and a lambda of
/// one element of each (see `ForSyntax`).
let private forExpression (c: Cursor) =
    let start = c.Start
    c.Expect(Keyword, "for") |> ignore
    let iterables = sepBy1 c (fun () -> expression Crossing c) (fun () -> c.Expect Comma |> ignore)
    let elements = List.init iterables.Length (fun index -> ForSyntax.element (index + 1), None)
    let body =
        if nextCodeIs c Bar then
            let armsStart = nextCodeStart c
            let written = arms c
            let span = c.SpanFrom armsStart
            let scrutinees = elements |> List.map (fun (name, _) -> Expression.at span (IdentifierExpr name))
            Expression.at span (Lambda(elements, Expression.at span (Match(scrutinees, written)), false))
        else
            let arrowStart = c.Start
            c.Expect Arrow |> ignore
            let body = armBody c
            Expression.at (c.SpanFrom arrowStart) (Lambda(elements, body, false))
    Expression.at (c.SpanFrom start) (FunctionCall(ForSyntax.builtIn, None, iterables @ [ body ]))

/// `while condition -> body`: a call to the built-in "__while" (see `WhileSyntax`).
let private whileExpression (c: Cursor) =
    let start = c.Start
    c.Expect(Keyword, "while") |> ignore
    let condition = expression Crossing c
    c.Expect Arrow |> ignore
    let body = armBody c
    Expression.at (c.SpanFrom start) (FunctionCall(WhileSyntax.builtIn, None, [ condition; body ]))

/// `do` and an indented block of statements, which is that block.
let private doExpression (c: Cursor) =
    c.Expect(Keyword, "do") |> ignore
    match c.At 0 with
    | Indent _ ->
        c.Index <- c.Index + 1
        indentedBlock c
    | _ -> c.GiveUp "a `do` without a block, which is a mistake"

/// `use value in body`: the body an expression, or a block on the lines after `in`.
let private useIn (c: Cursor) =
    let start = c.Start
    c.Expect(Keyword, "use") |> ignore
    let value = useValue c
    c.Expect(Keyword, "in") |> ignore
    let body =
        match c.At 0 with
        | Indent _ ->
            c.Index <- c.Index + 1
            unwrapSingle (indentedBlock c)
        | Code _ -> expression Crossing c
        | _ -> c.GiveUp "a `use ... in` body the layout pass doesn't open"
    Expression.at (c.SpanFrom start) (UseIn(value, body))

/// `return x`, `try x`, `yield x` and `name! x`, the workflow keywords applied to a value (see `WorkflowSyntax`).
let private workflow (c: Cursor) =
    let start = c.Start
    let token = c.Take()
    let name = c.Text token
    // `name! x`, the older way to write a keyword, comes first: `try! x` is a bind as `try x` is.
    let bang = c.Adjacent && c.Is(Operator, "!")
    if name = WorkflowSyntax.returnKeyword && not bang then
        across c (fun c -> c.Token.IsSome) |> ignore
        let value = expression Crossing c
        Expression.at (c.SpanFrom start) (WorkflowReturnExpr value)
    elif token.Kind = Keyword && not bang then
        if name = WorkflowSyntax.yieldKeyword && c.Adjacent && c.Is LeftParen then c.GiveUp "`yield(...)`, which is a mistake"
        if not c.OnSameLine then raise NoMatch
        let value = expression Crossing c
        Expression.at (c.SpanFrom start) (WorkflowBindExpr(name, value))
    else
        c.Expect(Operator, "!") |> ignore
        if name = WorkflowSyntax.yieldKeyword then c.GiveUp "`yield!`, which is a mistake"
        across c (fun c -> c.Token.IsSome) |> ignore
        let value = expression Crossing c
        Expression.at (c.SpanFrom start) (WorkflowBindExpr(name, value))

/// `mut x` as an argument, passing the cell itself (see `CellPassingSyntax`).
let private cellArgument (c: Cursor) =
    let start = c.Start
    c.Expect(Keyword, "mut") |> ignore
    if not c.OnSameLine then c.GiveUp "`mut` as an expression, which is a mistake"
    let cell, span = identifierWithSpan c
    if not ((c.Is Comma || c.Is RightParen) && c.OnSameLine) then c.GiveUp "`mut` as an expression, which is a mistake"
    Expression.at (c.SpanFrom start) (FunctionCall(CellPassingSyntax.argument, None, [ Expression.at span (IdentifierExpr cell) ]))

/// `type Point`, a type passed as a type argument (see `TypeValue`).
let private typeValue (c: Cursor) =
    let start = c.Start
    c.Expect(Keyword, "type") |> ignore
    if not c.OnSameLine then raise NoMatch
    let typeExpr = DescentTypes.typePostfix c
    Expression.at (c.SpanFrom start) (TypeValue typeExpr)

/// An expression that starts with a keyword, or a name and `!` (`Forward.keywordExpression`).
let keywordExpression (c: Cursor) : Expression =
    let bang = match c.At 1 with Code next -> c.Token.IsSome && next.Start = c.Token.Value.End && c.Text next = "!" | _ -> false
    match c.Token with
    | Some token when token.Kind = Keyword && bang && c.Text token <> WorkflowSyntax.returnKeyword -> workflow c
    | Some token when token.Kind = Keyword ->
        match c.Text token with
        | "match" -> matchExpression c
        | "if" -> ifExpression c
        | "for" -> forExpression c
        | "while" -> whileExpression c
        | "do" -> doExpression c
        | "use" -> useIn c
        | "return" | "try" | "yield" -> workflow c
        | "mut" -> cellArgument c
        | "type" -> typeValue c
        | other -> c.GiveUp $"`{other}`, which starts what the descent parser doesn't read"
    | Some token when token.Kind = Name -> workflow c
    | _ -> raise NoMatch

/// Fills the forward references the files before this one read through.
let fill () =
    Forward.lambda.Value <- lambda
    Forward.expression.Value <- expression Crossing
    Forward.arms.Value <- arms
    Forward.exceptArms.Value <- exceptArms
    Forward.keywordExpression.Value <- keywordExpression
    Forward.attributeArgument.Value <- attributeArgument

/// A def's value, read from after its `=`: what is on its line, or the block on the lines after it.
let defValueAfterEquals (c: Cursor) : Expression =
    fill ()
    match c.At -1 with
    | Indent _ -> indentedBlock c
    | _ -> DescentExpressions.defValue c

/// A statement's expression, read as `exprWithoutCrossingNewlines` reads it.
let statementValue (c: Cursor) : Expression =
    fill ()
    expression Inline c
