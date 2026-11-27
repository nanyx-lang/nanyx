/// Introduce local: the selected expression becomes a def of its own, `def name = <expr>`, before the statement that
/// holds it, and the selection becomes `name`. Positions are character offsets into the source as the parser saw it,
/// with line endings normalised to `\n`, as for `Hover`.
module NyxCompiler.Refactorings.IntroduceLocal

open Parser.Program
open NyxCompiler
open Nanyx.Formatter
open NyxCompiler.Refactorings.RefactoringText
open NyxCompiler.SelectedExpression

let private title = "Introduce local"

/// The name a local for `expr` takes: the name of the function it calls or the field it reads, or else `result`, made
/// unique among `taken` by a number after it. The function's name is usually taken: a local of that name would hide
/// it.
let private nameFor (taken: Set<string>) (expr: Expression) =
    let named =
        match expr.Kind with
        | FunctionCall(name, _, _)
        | Pipe(_, name, _, _) when not (MadeUpNames.isMadeUp name) -> Names.ownPart name
        | MemberAccess(_, field, _) -> field
        | _ -> "result"
    unique taken "result" (if Names.isIdentifier named && not (MadeUpNames.isMadeUp named) then named else "result")

/// The names the identifiers within `exprs` are written with, each with the symbol it resolved to.
let private referencesIn (typed: TypedModule) (expr: Expression) =
    within expr
    |> List.choose (fun part ->
        match part.Kind with
        | IdentifierExpr name -> Some (name, WrittenReferences.ofModule typed |> Map.tryFind part.Id)
        | _ -> None)

/// Why `expr` can't be moved before `site`, whose text runs from `siteStart`: it uses `try` or `return`, whose meaning
/// depends on the block, or reads a cell assigned before it in what it is moved out of.
let private movingRefusal (typed: TypedModule) (outer: Expression list) (siteStart: int) (expr: Expression) =
    let usesKeyword =
        within expr |> List.exists (fun part -> match part.Kind with WorkflowBindExpr _ | WorkflowReturnExpr _ -> true | _ -> false)
    let reads = referencesIn typed expr |> List.choose snd |> Set.ofList
    let assignedBefore =
        outer
        |> List.collect within
        |> List.tryPick (fun part ->
            match part.Kind with
            | AssignSyntax.Call(({ Kind = IdentifierExpr name } as target), _) when
                part.Span.End <= expr.Span.Start
                && siteStart <= part.Span.Start
                && (WrittenReferences.ofModule typed |> Map.tryFind target.Id |> Option.exists reads.Contains) ->
                Some name
            | _ -> None)
    if usesKeyword then Some "It uses `try` or `return`, whose meaning depends on the block it is written in"
    else assignedBefore |> Option.map (fun name -> $"It reads `{name}`, which is assigned before it")

/// The other expressions after `expr` in `scope` that are written the same and mean the same: their names resolve to
/// the same symbols. None when `expr` calls a function that requires a context, which may do something different
/// each time, or reads a cell, which may change in between.
let private sameAs (typed: TypedModule) (scope: Expression list) (expr: Expression) =
    let names = referencesIn typed expr
    let effectful = requiresAContext typed (within expr)
    let readsCell =
        scope
        |> List.collect within
        |> List.exists (fun part ->
            match part.Kind with
            | AssignSyntax.Call(target, _) -> referencesIn typed target |> List.exists (fun reference -> names |> List.contains reference)
            | _ -> false)
    if effectful || readsCell then []
    else
        scope
        |> List.collect within
        |> List.filter (fun other ->
            other.Id <> expr.Id
            && other.Span.Start >= expr.Span.End
            && other = expr
            && referencesIn typed other = names)
        // An occurrence inside another is replaced with it.
        |> fun found -> found |> List.filter (fun other -> not (found |> List.exists (fun outer -> outer.Id <> other.Id && SourceSpan.contains outer.Span other.Span)))

/// The refactorings at `selection` in `source`, the text of the root module of `result` compiled as `file`, laid out
/// with `options`: where the selection is exactly an expression, introducing a local for it, and for it and the other
/// expressions after it in the same block that are written and mean the same, when there are any. The def goes
/// before the statement of the innermost block that holds the expression, or, outside any block, before the top-level
/// item; a lambda whose body is one expression has its body made a block for it. It is refused where moving the
/// expression would change what it means: where it runs only in some cases (a branch of an `if`, a match arm, the right
/// of `and`, a lambda), uses `try` or `return`, or reads a cell assigned before it. Empty where the selection is not
/// an expression, or where the module did not type-check.
let at (options: Formatter.Options) (file: string) (source: string) (result: CompileResult) (selection: SourceSpan) : Offer list =
    let source = normalised source
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        match SelectedExpression.at source syntax selection with
        | None -> []
        | Some (_, _, Some reason) -> [ Refused(title, RefactoringKind.Extract, $"{reason}: a local before the statement would run it always") ]
        | Some (expr, site, None) ->
            // What the expression is moved out of, and where the def goes.
            let outer, siteStart, scope =
                match site with
                | BeforeStatement statement ->
                    let block = syntax |> List.collect (function Kind (Def (ValueDef(_, _, _, value))) | Kind (Expr value) -> within value | _ -> []) |> List.tryFind (fun candidate -> match candidate.Kind with Block statements -> statements |> List.exists (fun s -> s.Id = statement.Id) | _ -> false)
                    let laterStatements =
                        match block with
                        | Some { Kind = Block statements } -> statements |> List.skipWhile (fun s -> s.Id <> statement.Id)
                        | _ -> [ statement ]
                    SyntaxTree.ofStatement statement, statement.Span.Start, laterStatements |> List.collect SyntaxTree.ofStatement
                | BeforeItem item ->
                    let values = match item.Kind with Def (ValueDef(_, _, _, value)) | Expr value -> [ value ] | _ -> []
                    values, item.Span.Start, values
                | LambdaBody(_, body) -> [ body ], body.Span.Start, [ body ]
            match movingRefusal typed outer siteStart expr with
            | Some reason -> [ Refused(title, RefactoringKind.Extract, reason) ]
            | None ->
                let name = nameFor (takenNames syntax typed) expr
                let exprText = SourceSpan.textIn source expr.Span
                let offer (title: string) (uses: Expression list) =
                    let replacements = uses |> List.map (fun used -> used.Span.Start, used.Span.End, name)
                    let edits =
                        match site with
                        | BeforeStatement statement ->
                            let lineStart, indent = lineOf source statement.Span.Start
                            if source.Substring(lineStart, statement.Span.Start - lineStart).Trim() = "" then
                                let at = insertionPoint source [] statement.Span.Start
                                (at, at, $"{indent}def {name} = {exprText}\n") :: replacements
                            else
                                // A statement after another on its line.
                                (statement.Span.Start, statement.Span.Start, $"def {name} = {exprText}\n{indent}") :: replacements
                        | BeforeItem item ->
                            let at = insertionPoint source syntax item.Span.Start
                            let _, indent = lineOf source item.Span.Start
                            (at, at, $"{indent}def {name} = {exprText}\n\n") :: replacements
                        | LambdaBody(lambda, body) ->
                            let _, lambdaIndent = lineOf source lambda.Span.Start
                            let indent = lambdaIndent + String.replicate options.IndentSize " "
                            let bodyText =
                                applied
                                    (SourceSpan.textIn source body.Span)
                                    (replacements |> List.map (fun (start, finish, text) -> start - body.Span.Start, finish - body.Span.Start, text))
                            [ body.Span.Start, body.Span.End, $"\n{indent}def {name} = {exprText}\n{indent}{bodyText}\n{lambdaIndent}" ]
                    let after = appliedAndFormatted options file source edits
                    Available { Title = title; Kind = RefactoringKind.Extract; Edits = [ difference file source after ]; Rename = declarationOf after name }
                let others = sameAs typed scope expr
                offer $"{title} `{name}`" [ expr ]
                :: (if others.IsEmpty then [] else [ offer $"{title} `{name}` for all {others.Length + 1} occurrences" (expr :: others) ])
    | _ -> []
