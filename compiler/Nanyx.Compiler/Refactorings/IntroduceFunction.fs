/// Introduce function: the selected expression, or run of statements, becomes a def of a function of its own, with a
/// parameter for each local it uses from around it, and the selection becomes a call to it. Positions are character
/// offsets into the source as the parser saw it, with line endings normalised to `\n`, as for `Hover`.
module NyxCompiler.Refactorings.IntroduceFunction

open Parser.Program
open NyxCompiler
open Nanyx.Formatter
open NyxCompiler.Refactorings.RefactoringText

let private title = "Introduce function"
let private localTitle = "Introduce local function"

/// What the selection is.
type private Selected =
    /// An expression.
    | SelectedExpression of Expression
    /// A run of whole statements of a block, and the statements of the block after them.
    | SelectedStatements of run: Statement list * after: Statement list

/// The selection, in the values of the top-level items `values`, as a run of whole statements of a block with a def
/// among them (the value of `mut x = 0` spans all of it), or the outermost expression written exactly there, or a run of
/// whole statements.
let private selectedIn (values: Expression list) (selection: SourceSpan) =
    let all = values |> List.collect within
    let isAt (span: SourceSpan) = not (SourceSpan.isNone span) && span.Start = selection.Start && span.End = selection.End
    let statements =
        all
        |> List.tryPick (fun expr ->
            match expr.Kind with
            | Block statements ->
                match statements |> List.skipWhile (fun statement -> statement.Span.Start <> selection.Start || SourceSpan.isNone statement.Span) with
                | [] -> None
                | fromStart ->
                    fromStart
                    |> List.tryFindIndex (fun statement -> statement.Span.End = selection.End)
                    |> Option.map (fun index -> List.splitAt (index + 1) fromStart)
                    |> Option.filter (fun (run, _) -> run |> List.forall (fun statement -> not (SourceSpan.isNone statement.Span)))
                    |> Option.map SelectedStatements
            | _ -> None)
    let definesALocal = function Some (SelectedStatements(run, _)) -> run |> List.exists (function Kind (DefStatement _) -> true | _ -> false) | _ -> false
    if definesALocal statements then statements
    else
        all
        |> List.tryFind (fun expr -> isAt expr.Span)
        |> Option.map SelectedExpression
        |> Option.orElse statements

/// Why the selected statements `run` can't be a function of their own, if they can't: a `use`, an import or a type
/// definition among them, which the statements after them may need.
let private statementRefusal (run: Statement list) =
    run
    |> List.tryPick (fun statement ->
        match statement.Kind with
        | UseStatement _ -> Some "It has a `use`, whose context the statements after it would lose"
        | ImportStatement _ -> Some "It has an import, which the statements after it would lose"
        | TypeDefStatement _ -> Some "It defines a type, which the statements after it would lose"
        | DefStatement _
        | ExprStatement _ -> None)

/// `text`, which starts at `offset` in `source`, with each line after its first moved left by the indent of the line
/// it starts on, and every line then indented by `indent`.
let private reindented (source: string) (offset: int) (indent: string) (text: string) =
    let _, original = lineOf source offset
    text.Split '\n'
    |> Array.mapi (fun index line ->
        if index = 0 || line.Trim() = "" then indent + line
        elif line.StartsWith original then indent + line.Substring original.Length
        else indent + line.TrimStart())
    |> String.concat "\n"

/// The innermost statement of a block in `values` whose span holds `selection`, if it starts its line in `source`.
let private statementAround (source: string) (values: Expression list) (selection: SourceSpan) =
    values
    |> List.collect within
    |> List.collect (fun expr -> match expr.Kind with Block statements -> statements | _ -> [])
    |> List.filter (fun statement -> not (SourceSpan.isNone statement.Span) && SourceSpan.contains statement.Span selection)
    |> List.sortBy (fun statement -> statement.Span.End - statement.Span.Start)
    |> List.tryHead
    |> Option.filter (fun statement ->
        let lineStart, _ = lineOf source statement.Span.Start
        source.Substring(lineStart, statement.Span.Start - lineStart).Trim() = "")

/// `text` with `edit` made.
let private withEdit (text: string) (edit: TextEdit) = applied text [ edit.Span.Start, edit.Span.End, edit.NewText ]

/// The refactorings at `selection` in `source`, the text of the root module of `result` compiled as `file`, laid out
/// with `options`, where the selection is exactly an expression or a run of whole statements of a block: introducing a
/// function for it, a top-level def after the item that holds it, and a local function, a def before the statement
/// that holds it. Each local, parameter or pattern variable it uses from around it is a parameter, in the order they are
/// first used; a cell is passed as one, `mut x`, so the function reads and changes the same cell. An expression's
/// value is the function's; statements give the locals they define that the statements after them use, which the call
/// defines again (`def (a, b) = extracted(x)`). The def's type is the one inferred for it, written as Write the inferred
/// type writes it. `recompile` compiles a text of the module as `result` was compiled: the result is refused if it
/// would not compile. Refused where the selection uses `try` or `return`, whose meaning depends on its block, or where
/// statements have a `use`, an import, a type definition or a cell the statements after them use, and where the
/// selection, inside a def, is not a whole expression or whole statements. Empty elsewhere, and where the module did
/// not type-check.
let at
    (options: Formatter.Options)
    (file: string)
    (source: string)
    (result: CompileResult)
    (selection: SourceSpan)
    (recompile: string -> CompileResult)
    : Offer list =
    let source = normalised source
    match result.Syntax, result.Typed with
    | Some syntax, Some typed when selection.End > selection.Start ->
        // The selection less the spaces around it.
        let text = source.Substring(selection.Start, selection.End - selection.Start)
        let selection =
            { selection with Start = selection.Start + (text.Length - text.TrimStart().Length); End = selection.End - (text.Length - text.TrimEnd().Length) }
        let values = topLevelValues syntax
        let refused reason = [ Refused(title, RefactoringKind.Extract, reason) ]
        match selectedIn values selection with
        | None when values |> List.exists (fun value -> SourceSpan.contains value.Span selection) ->
            refused "The selection is not a whole expression or whole statements"
        | None -> []
        | Some selected ->
            let inSelection (span: SourceSpan) = selection.Start <= span.Start && span.End <= selection.End
            let expressions, run, after =
                match selected with
                | SelectedExpression expr -> within expr, [], []
                | SelectedStatements(run, after) -> run |> List.collect SyntaxTree.ofStatement |> List.collect within, run, after
            let symbolOf (part: Expression) = typed.References |> Map.tryFind part.Id |> Option.bind typed.Symbols.TryFind
            let isLocal (symbol: Symbol) =
                symbol.Declaration.File = file
                && not (SourceSpan.isNone symbol.Declaration)
                && (match symbol.Kind with LocalValue | Parameter | PatternVariable -> true | _ -> false)
            let parameters =
                expressions
                |> List.choose (fun part -> symbolOf part |> Option.map (fun symbol -> part.Span.Start, symbol))
                |> List.filter (fun (_, symbol) -> isLocal symbol && not (inSelection symbol.Declaration))
                |> List.sortBy fst
                |> List.map snd
                |> List.distinctBy (fun symbol -> symbol.Id)
            // The locals the statements define that the statements after them use.
            let results =
                let usedAfter = after |> List.collect SyntaxTree.ofStatement |> List.collect within |> List.choose symbolOf
                usedAfter
                |> List.filter (fun symbol -> isLocal symbol && inSelection symbol.Declaration)
                |> List.distinctBy (fun symbol -> symbol.Id)
                |> List.sortBy (fun symbol -> symbol.Declaration.Start)
            let cells = cellsIn values
            let isCell = isCell cells
            let usesKeyword =
                expressions |> List.exists (fun part -> match part.Kind with WorkflowBindExpr _ | WorkflowReturnExpr _ -> true | _ -> false)
            let refusal =
                if usesKeyword then Some "It uses `try` or `return`, whose meaning depends on the block it is written in"
                else
                    statementRefusal run
                    |> Option.orElse (results |> List.tryFind isCell |> Option.map (fun cell -> $"It declares the cell `{cell.Name}`, which the statements after it use"))
                    |> Option.orElse (
                        (parameters @ results)
                        |> List.tryFind (fun symbol -> MadeUpNames.isMadeUp symbol.Name)
                        |> Option.map (fun _ -> "It uses a name the compiler made up"))
            match refusal with
            | Some reason -> refused reason
            | None ->
                let name = unique (takenNames syntax typed) "extracted" "extracted"
                let indentStep = String.replicate options.IndentSize " "
                let parameterList =
                    match parameters with
                    | [] -> "()"
                    | _ -> parameters |> List.map (fun symbol -> if isCell symbol then $"mut {symbol.Name}" else symbol.Name) |> String.concat ", "
                let call =
                    let arguments = parameters |> List.map (fun symbol -> if isCell symbol then $"mut {symbol.Name}" else symbol.Name)
                    $"""{name}({String.concat ", " arguments})"""
                let selectedText = source.Substring(selection.Start, selection.End - selection.Start)
                // The function's body, written as its lines are, and what the selection becomes.
                let body, replacement =
                    match selected, results with
                    | SelectedExpression _, _ -> selectedText, call
                    | SelectedStatements _, [] ->
                        let ends = match (List.last run).Kind with DefStatement _ -> "\n()" | _ -> ""
                        selectedText + ends, call
                    | SelectedStatements _, [ single ] -> $"{selectedText}\n{single.Name}", $"def {single.Name} = {call}"
                    | SelectedStatements _, several ->
                        let names = several |> List.map (fun symbol -> symbol.Name) |> String.concat ", "
                        $"{selectedText}\n({names})", $"def ({names}) = {call}"
                // The def, with each line after its first indented by `indent`.
                let defText (indent: string) =
                    if not (body.Contains '\n') then $"def {name} = {{ {parameterList} -> {body} }}"
                    else $"def {name} = {{ {parameterList} ->\n{reindented source selection.Start (indent + indentStep) body}\n{indent}}}"
                let replace = selection.Start, selection.End, replacement
                let offer (offerTitle: string) (edits: (int * int * string) list) =
                    let untyped = applied source edits
                    let compiled = recompile untyped
                    match newError result compiled with
                    | Some reason -> Refused(offerTitle, RefactoringKind.Extract, $"The function would not compile{reason}")
                    | None ->
                        // The def's type, written as it was inferred, unless the text with it written does not compile.
                        let written =
                            declarationOf untyped name
                            |> Option.bind (WriteInferredType.at file untyped compiled)
                            |> Option.bind (function
                                | Available writing -> writing.Edits |> List.tryExactlyOne |> Option.map (withEdit untyped)
                                | Refused _ -> None)
                            |> Option.filter (fun text -> (newError result (recompile text)).IsNone)
                            |> Option.defaultValue untyped
                        let changed = difference file source written
                        let after = formatted options file written changed.Span.Start (changed.Span.Start + changed.NewText.Length)
                        Available
                            { Title = offerTitle
                              Kind = RefactoringKind.Extract
                              Edits = [ difference file source after ]
                              Rename = declarationOf after name }
                let topLevel =
                    syntax
                    |> List.tryFind (fun item -> not (SourceSpan.isNone item.Span) && SourceSpan.contains item.Span selection)
                    |> Option.map (fun item -> offer $"{title} `{name}`" [ replace; item.Span.End, item.Span.End, "\n\n" + defText "" ])
                let local =
                    let around =
                        match selected with
                        | SelectedStatements(first :: _, _) -> Some first
                        | _ -> statementAround source values selection
                    around
                    |> Option.map (fun statement ->
                        let _, indent = lineOf source statement.Span.Start
                        let at = insertionPoint source [] statement.Span.Start
                        offer $"{localTitle} `{name}`" [ replace; at, at, $"{indent}{defText indent}\n" ])
                Option.toList topLevel @ Option.toList local
    | _ -> []
