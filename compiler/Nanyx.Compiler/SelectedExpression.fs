/// The expression written exactly at a selection, as the refactorings that take one (`IntroduceLocal`) and the info
/// view find it. Positions are character offsets into the source as the parser saw it, with line endings normalised to
/// `\n`, as for `Hover`.
module NyxCompiler.SelectedExpression

open Parser.Program

/// Where a def of the expression found would go, for a refactoring that introduces one.
type internal Site =
    /// Before a statement of a block.
    | BeforeStatement of Statement
    /// Before a top-level item, an expression of which holds the selection outside any block.
    | BeforeItem of TopLevelItem
    /// At the start of a lambda's body, which is one expression: the body becomes a block.
    | LambdaBody of lambda: Expression * body: Expression

/// How an expression is reached from the one around it.
type internal Reached =
    /// Every time the expression around it is.
    | Always
    /// Only in some cases, which the reason says.
    | Sometimes of string

/// The expressions directly within `expr`, each with how it is reached, and the statements of it if it is a block.
let internal partsOf (expr: Expression) : (Expression * Reached) list * Statement list =
    let always = List.map (fun part -> part, Always)
    let sometimes reason = List.map (fun part -> part, Sometimes reason)
    let inArms (arms: MatchArm list) =
        sometimes "It is in a match arm, which runs only when its pattern matches" (Pattern.armExpressions arms @ List.map snd arms)
    match expr.Kind with
    | IfExpr(condition, ifTrue, ifFalse) -> always [ condition ] @ sometimes "It is in a branch of an `if`, which runs only when it is taken" [ ifTrue; ifFalse ], []
    | Match(values, arms) -> always values @ inArms arms, []
    | Except(value, arms) -> always [ value ] @ inArms arms, []
    | BinaryOp(op, _, left, right) when op = "and" || op = "or" ->
        always [ left ] @ sometimes $"It is on the right of `{op}`, which runs only when the left does not decide the result" [ right ], []
    | _ ->
        let children, statements = SyntaxTree.childrenOf expr
        always children, statements

/// Whether `lambda` is written `{ ... }`, so that its body can be made a block, rather than made up by the parser
/// around an expression, as the right of `??` is.
let internal isWritten (source: string) (lambda: Expression) =
    not (SourceSpan.isNone lambda.Span) && lambda.Span.Start < source.Length && source[lambda.Span.Start] = '{'

/// The expression written exactly at `selection` within `expr`, the outermost if several are, with the site its def
/// goes at and why it can't, if the way to it runs only in some cases.
let rec internal findIn (source: string) (selection: SourceSpan) (site: Site) (sometimes: string option) (expr: Expression) =
    if SourceSpan.isNone expr.Span |> not && not (SourceSpan.contains expr.Span selection) then None
    elif expr.Span.Start = selection.Start && expr.Span.End = selection.End then Some (expr, site, sometimes)
    else
        let children, statements = partsOf expr
        let inChildren () =
            children
            |> List.tryPick (fun (child, reached) ->
                match expr.Kind, reached with
                | Lambda _, _ when (match child.Kind with Block _ | ContextNames _ -> false | _ -> true) && isWritten source expr ->
                    findIn source selection (LambdaBody(expr, child)) None child
                | Lambda _, _ -> findIn source selection site (Some "It is in a lambda, which runs only when it is called") child
                | _, Sometimes reason -> findIn source selection site (Some (defaultArg sometimes reason)) child
                | _, Always -> findIn source selection site sometimes child)
        let inStatements () =
            statements
            |> List.tryPick (fun statement ->
                // A statement the parser made up, with no span, has no place of its own to put a def before.
                let site, sometimes = if SourceSpan.isNone statement.Span then site, sometimes else BeforeStatement statement, None
                Statement.finalExpression statement.Kind |> Option.bind (findIn source selection site sometimes))
        match inStatements () with
        | Some found -> Some found
        | None -> inChildren ()

/// `selection`, a span of `source`, less the spaces around it: what is selected, for the rule that what is written
/// exactly there is what the selection picks.
let internal trimmed (source: string) (selection: SourceSpan) : SourceSpan =
    let text = SourceSpan.textIn source selection
    { selection with Start = selection.Start + (text.Length - text.TrimStart().Length); End = selection.End - (text.Length - text.TrimEnd().Length) }

/// The expression of `syntax`, parsed from `source`, written exactly at `selection` less the spaces around it, the
/// outermost if several are (see `findIn`): in the value of a def or a top-level expression. None for an empty selection,
/// or one that is not a single expression, such as part of one or two statements.
let internal at (source: string) (syntax: Module) (selection: SourceSpan) : (Expression * Site * string option) option =
    if selection.End <= selection.Start then None
    else
        let selection = trimmed source selection
        syntax
        |> List.tryPick (fun item ->
            match item.Kind with
            | Def (ValueDef(_, _, _, value))
            | Expr value -> findIn source selection (BeforeItem item) None value
            | _ -> None)
