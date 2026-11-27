/// The recursive-descent parser's expressions that start with a keyword: `match`, `if` (with `is`, and with arms),
/// `for`, `while`, `do`, `use ... in`, the workflow keywords `return`, `try`, `yield` and `name!`, a cell passed as
/// `mut x`, and a type passed as `type T`. It fills the forward references the files before it read
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
            let blockStart = c.Start
            let block = unwrapSingle (indentedBlock c)
            // The arms must be indented less than a block scrutinee, so that it can't be taken for them.
            let column (offset: int) = offset - (if offset = 0 then 0 else c.Source.LastIndexOf('\n', offset - 1) + 1)
            let index = c.Index
            skipLayout c
            let armsColumn = if c.Is Bar then Some(column c.Start) else None
            c.Index <- index
            if armsColumn |> Option.exists (fun arms -> arms >= column blockStart) then
                c.Report (nextCodeStart c) "The arms of a match are indented as far as the block before them: indent them less than the block"
            [ block ]
        | Code _ when c.OnSameLine -> sepBy1If c (fun () -> expression Crossing c) (fun () -> c.Expects Comma) (fun () -> c.Expect Comma |> ignore)
        | _ -> c.GiveUp "a match's scrutinee the layout pass doesn't open"
    let written = try arms c with NoMatch -> c.GiveUp "a `match` with no arms, which is a mistake"
    Expression.at (c.SpanFrom start) (Match(scrutinees, written))

/// A branch after `then` or `else`: a block on the lines after it, or a body.
let private branch (c: Cursor) =
    match c.At 0 with
    | Indent _ ->
        c.Index <- c.Index + 1
        indentedBlock c
    | Code _ -> sameLineBody c (expression Crossing)
    | _ -> c.GiveUp "a branch the layout pass doesn't open"

/// A condition and the branch `readBranch` reads after it, which declares the names the tests along the condition's
/// `and` chain bind (see `IsSyntax.chainNames`).
let private headThen (c: Cursor) (readBranch: unit -> Expression) =
    let head = expression Crossing c
    let body = readBranch ()
    c.Declare ArmBody body.Id (IsSyntax.chainNames head)
    head, body

/// An if spanning `span`. An if-match, `if value is pattern`, is a match whose second arm's wildcard is made up at the
/// else branch; any other condition is an `IfExpr`, which is checked as a match when its tests bind names (see
/// `IsSyntax.asMatch`). A left-out `else` is `()` made up at the then branch.
let private desugarIf (span: SourceSpan) (head: Expression) (thenExpr: Expression) (elseOpt: Expression option) =
    let elseExpr = elseOpt |> Option.defaultWith (fun () -> IfSyntax.leftOutElse thenExpr)
    match head.Kind with
    | IsSyntax.Test(value, pattern, None) ->
        Expression.at span (Match([ value ], [ [ pattern ], thenExpr; [ Pattern.at elseExpr.Span WildcardPattern ], elseExpr ]))
    | _ -> Expression.at span (IfExpr(head, thenExpr, elseExpr))

/// `if head then a else b`, or with arms, `if | head -> a | else -> b`, nested ifs.
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
    let iterables = sepBy1If c (fun () -> expression Crossing c) (fun () -> c.Expects Comma) (fun () -> c.Expect Comma |> ignore)
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

/// `while condition -> body`: a call to the built-in "__while" (see `WhileSyntax`). The names the tests along the
/// condition's `and` chain bind are declared at the condition, where the loop that binds them is made (see
/// `IsSyntax.whileAsIf`).
let private whileExpression (c: Cursor) =
    let start = c.Start
    c.Expect(Keyword, "while") |> ignore
    let condition = expression Crossing c
    c.Expect Arrow |> ignore
    let body = armBody c
    c.Declare ArmBody condition.Id (IsSyntax.chainNames condition)
    Expression.at (c.SpanFrom start) (FunctionCall(WhileSyntax.builtIn, None, [ condition; body ]))

/// `do` and an indented block of statements, which is that block.
let private doExpression (c: Cursor) =
    c.Expect(Keyword, "do") |> ignore
    match c.At 0 with
    | Indent _ ->
        c.Index <- c.Index + 1
        indentedBlock c
    | _ -> c.Report (c.LastEnd - 2) "`do` starts a block: put its statements on the lines after it, indented"

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
        if name = WorkflowSyntax.yieldKeyword && c.Adjacent && c.Is LeftParen then
            c.Report start $"`{WorkflowSyntax.yieldKeyword}` is not a function: write `{WorkflowSyntax.yieldKeyword} x`, with no parentheses"
        if not c.OnSameLine then raise NoMatch
        let value = expression Crossing c
        Expression.at (c.SpanFrom start) (WorkflowBindExpr(name, value))
    else
        let bangToken = c.Expect(Operator, "!")
        if name = WorkflowSyntax.yieldKeyword then
            c.Report bangToken.Start $"`{name}` is written without `!`: write `{name} x`, as `try x` and `return x` are"
        across c (fun c -> c.Token.IsSome) |> ignore
        let value = expression Crossing c
        Expression.at (c.SpanFrom start) (WorkflowBindExpr(name, value))

/// `mut x` as an argument, passing the cell itself (see `CellPassingSyntax`).
let private cellArgument (c: Cursor) =
    let start = c.Start
    c.Expect(Keyword, "mut") |> ignore
    let notExpression () : 'a =
        c.Report start "`mut` is not an expression: declare a cell on a line of its own, as `mut count = 0` in place of `def count = mut 0`"
    if not c.OnSameLine || not (c.Is Name) then notExpression ()
    let cell, span = identifierWithSpan c
    if not ((c.Is Comma || c.Is RightParen) && c.OnSameLine) then notExpression ()
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

/// A statement's expression, which ends at the end of its line (`Inline`).
let statementValue (c: Cursor) : Expression =
    fill ()
    expression Inline c
