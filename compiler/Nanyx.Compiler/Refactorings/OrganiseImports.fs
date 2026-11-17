/// Organise imports: remove the imports and selected names nothing uses, and write the rest as one import, sorted as
/// the formatter sorts imports. Positions are character offsets into the source as the parser saw it, with line endings
/// normalised to `\n`, as for `Hover`.
module NyxCompiler.Refactorings.OrganiseImports

open Parser.Program
open NyxCompiler
open Nanyx.Formatter
open NyxCompiler.Refactorings.RefactoringText

let private title = "Organise imports"

/// `item` as an import writes it: its source, alias and selection.
let private written (item: ImportItem) =
    let source = match item.Source with ModuleImport name -> name | PathImport path -> $"\"{path}\""
    let names (selection: (Identifier * SourceSpan) list) = "(" + (selection |> List.map (fst >> Names.written) |> String.concat ", ") + ")"
    match item.Alias, item.Selection with
    | Some alias, Some selection when not selection.IsEmpty -> $"{source} as {alias} & {names selection}"
    | Some alias, _ -> $"{source} as {alias}"
    | None, Some selection when not selection.IsEmpty -> $"{source} as {names selection}"
    | None, _ -> source

/// The names written in `syntax` that refer to something the import `symbol` brings in: each expression's and type's
/// name, without what is before its last dot.
let private namesUsedThrough (syntax: Module) (typed: TypedModule) (symbol: Symbol) =
    let through (id: NodeId) = typed.ImportReferences |> Map.tryFind id = Some symbol.Id
    let ownName (name: string) = name.Substring(name.LastIndexOf '.' + 1)
    let expressions = Hover.allExpressions syntax
    let fromExpressions =
        expressions
        |> List.filter (fun expr -> through expr.Id)
        |> List.choose (fun expr ->
            match expr.Kind with
            | IdentifierExpr name
            | FunctionCall(name, _, _)
            | Pipe(_, name, _, _) -> Some (ownName name)
            | _ -> None)
    let fromTypes =
        (syntax |> List.collect SyntaxTree.topLevelTypes) @ (expressions |> List.collect SyntaxTree.expressionTypes)
        |> List.collect TypeExpr.within
        |> List.filter (fun typeExpr -> through typeExpr.Id)
        |> List.choose (fun typeExpr ->
            match typeExpr.Kind with
            | TypeName name
            | TypeApply(name, _) -> Some (ownName name)
            | _ -> None)
    fromExpressions @ fromTypes |> Set.ofList

/// What is kept of the import `item`: nothing when nothing uses it, and otherwise the item with only the names of its
/// selection something uses. An alias starting with `_` is kept, as the linter's `unusedImports` keeps it.
let private kept (syntax: Module) (typed: TypedModule) (used: Set<SymbolId>) (item: ImportItem) =
    let symbol =
        typed.Symbols |> Map.values |> Seq.tryFind (fun symbol -> symbol.Kind = ImportedModule && symbol.Declaration = item.Span)
    match symbol with
    | None -> Some item
    | Some symbol when symbol.Name.StartsWith "_" -> Some item
    | Some symbol when not (used.Contains symbol.Id) -> None
    | Some symbol ->
        match item.Selection with
        | Some selection ->
            let names = namesUsedThrough syntax typed symbol
            Some { item with Selection = Some (selection |> List.filter (fun (name, _) -> names.Contains name)) }
        | None -> Some item

/// From the start of the line `start` is on to the end of the line `finish` is on, and the blank lines after it.
let private wholeLines (source: string) (start: int) (finish: int) =
    let lineStart, _ = lineOf source start
    let lineEnd = source.IndexOf('\n', finish)
    let rec blanks (at: int) =
        let next = source.IndexOf('\n', at)
        if next >= 0 && source.Substring(at, next - at).Trim() = "" then blanks (next + 1) else at
    lineStart, (if lineEnd < 0 then source.Length else blanks (lineEnd + 1))

/// The refactoring of `source`, the text of the root module of `result` compiled as `file`, laid out with `options`,
/// when it changes anything: the top-level imports, less those nothing uses and the names of a selection nothing uses,
/// written as one import where the first was, sorted as the formatter sorts them (`ImportOrder`): the standard library
/// first, then packages, then imports by path. `recompile` compiles a text of the module as `result` was compiled,
/// and the result is refused if it would not compile. Refused where the imports have comments among them, which it
/// would lose. Imports in a `module name =` block and re-exports are left as they are. Empty when there is nothing to
/// change, and where the module did not type-check.
let at (options: Formatter.Options) (file: string) (source: string) (result: CompileResult) (recompile: string -> CompileResult) : Offer list =
    let source = normalised source
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        let statements =
            syntax |> List.choose (fun item -> match item.Kind with Import items when not (SourceSpan.isNone item.Span) -> Some (item, items) | _ -> None)
        match statements with
        | [] -> []
        | (first, _) :: rest ->
            let used = typed.ImportReferences |> Map.values |> Set.ofSeq
            let items =
                statements
                |> List.collect snd
                |> List.choose (kept syntax typed used)
                |> List.sortWith ImportOrder.compare
                |> List.map written
                |> List.distinct
            let text =
                match items with
                | [] -> ""
                | [ single ] -> $"import {single}"
                | _ ->
                    let indent = String.replicate options.IndentSize " "
                    $"""import ({items |> List.map (fun item -> $"\n{indent}{item}") |> String.concat ""}
)"""
            let removals = rest |> List.map (fun (statement, _) -> wholeLines source statement.Span.Start statement.Span.End)
            let edits =
                (if text = "" then (let start, finish = wholeLines source first.Span.Start first.Span.End in start, finish, "")
                 else first.Span.Start, first.Span.End, text)
                :: (removals |> List.map (fun (start, finish) -> start, finish, ""))
            let after = appliedAndFormatted options file source edits
            let hasComments =
                statements |> List.exists (fun (statement, _) -> source.Substring(statement.Span.Start, statement.Span.End - statement.Span.Start).Contains "--")
            if after = formatted options file source 0 source.Length then []
            elif hasComments then [ Refused(title, RefactoringKind.OrganiseImports, "The imports have comments among them, which organising would lose") ]
            else
                [ Deferred(title, RefactoringKind.OrganiseImports, fun () ->
                      match newError result (recompile (applied source edits)) with
                      | Some reason -> Refused(title, RefactoringKind.OrganiseImports, $"The organised module would not compile{reason}")
                      | None -> Available { Title = title; Kind = RefactoringKind.OrganiseImports; Edits = [ difference file source after ]; Rename = None }) ]
    | _ -> []
