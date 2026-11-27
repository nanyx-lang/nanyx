/// Inline: a def's uses become its value, or a function's calls its body with the arguments put in for its
/// parameters. Positions are character offsets into the source as the parser saw it, with line endings normalised to
/// `\n`, as for `Hover`.
module NyxCompiler.Refactorings.Inline

open Parser.Program
open NyxCompiler
open Nanyx.Formatter
open NyxCompiler.Refactorings.RefactoringText

/// A def that can be inlined: what it declares, its value, and the text that removing it removes.
type private Inlined =
    { Symbol: Symbol
      Value: Expression
      Removed: int * int }

/// The text of `span` in `source` with `edits` made, those of them that are inside it.
let private textWith (source: string) (span: SourceSpan) (edits: (int * int * string) list) =
    edits
    |> List.filter (fun (start, finish, _) -> span.Start <= start && finish <= span.End)
    |> List.map (fun (start, finish, text) -> start - span.Start, finish - span.Start, text)
    |> applied (SourceSpan.textIn source span)

/// The def that declares `symbol` among `syntax`, with the text removing it removes: from its doc comment and
/// attributes, or the start of its line, to the end of its line and the blank lines after it.
let private declarationOf (source: string) (syntax: Module) (symbol: Symbol) : Result<Inlined, string> =
    let removal = removal source
    let found =
        match symbol.Kind with
        | TopLevelValue ->
            syntax
            |> List.tryPick (fun item ->
                match item.Kind with
                | Def (ValueDef(_, name, _, value)) when name = symbol.Name && SourceSpan.contains item.Span symbol.Declaration ->
                    Some (value, removal (insertionPoint source syntax item.Span.Start) item.Span.End)
                | _ -> None)
        | _ ->
            topLevelValues syntax
            |> List.collect within
            |> List.collect (fun expr -> match expr.Kind with Block statements -> statements | _ -> [])
            |> List.tryPick (fun statement ->
                match statement.Kind with
                | DefStatement(_, name, _, value) when name = symbol.Name && SourceSpan.contains statement.Span symbol.Declaration ->
                    Some (value, removal (insertionPoint source [] statement.Span.Start) statement.Span.End)
                | _ -> None)
    match found with
    | Some ({ Kind = MutSyntax.Call _ }, _) -> Error "It is a cell, whose value changes"
    | Some (value, removed) -> Ok { Symbol = symbol; Value = value; Removed = removed }
    | None -> Error "Only a def of one name can be inlined"

/// The symbol `name` refers to at `offset`, among the declarations of `file`: the innermost of that name whose scope
/// holds `offset`, or `None` when none of `file` does, so that it refers to one of another module's.
let private resolvedAt (file: string) (typed: TypedModule) (name: string) (offset: int) =
    typed.Symbols
    |> Map.values
    |> Seq.filter (fun symbol ->
        symbol.Name = name
        && symbol.Declaration.File = file
        && not (SourceSpan.isNone symbol.Declaration)
        && (symbol.Kind = TopLevelValue || symbol.Kind = ImportedModule || symbol.Declaration.Start < offset)
        && typed.Scopes |> Map.tryFind symbol.Id |> Option.exists (fun scope -> scope.Start <= offset && offset <= scope.End))
    |> Seq.sortByDescending (fun symbol -> symbol.Declaration.Start)
    |> Seq.tryHead

/// The first name `exprs` use, other than those declared within `inside`, that would refer to something else at
/// `offset`.
let private shadowedAt (file: string) (typed: TypedModule) (inside: SourceSpan) (exprs: Expression list) (offset: int) =
    exprs
    |> List.collect within
    |> List.tryPick (fun part ->
        WrittenReferences.ofModule typed
        |> Map.tryFind part.Id
        |> Option.bind typed.Symbols.TryFind
        |> Option.filter (fun symbol -> not (SourceSpan.contains inside symbol.Declaration) && not (MadeUpNames.isMadeUp symbol.Name))
        |> Option.bind (fun symbol ->
            let resolved = resolvedAt file typed symbol.Name offset
            let same = if symbol.Declaration.File = file then resolved |> Option.map (fun r -> r.Id) = Some symbol.Id else resolved.IsNone
            if same then None else Some symbol.Name))

/// The uses of `inlined`'s symbol in `syntax`: the expressions that name it, each once.
let private usesOf (typed: TypedModule) (syntax: Module) (inlined: Inlined) =
    Hover.allExpressions syntax
    |> List.filter (fun expr -> not (SourceSpan.isNone expr.Span) && WrittenReferences.ofModule typed |> Map.tryFind expr.Id = Some inlined.Symbol.Id)
    |> List.distinctBy (fun expr -> expr.Span.Start, expr.Span.End)
    |> List.sortBy (fun expr -> expr.Span.Start)

/// The edits that inline a value that is not a lambda at `used`, or why it can't be.
let private inlineValue (file: string) (source: string) (typed: TypedModule) (values: Expression list) (inlined: Inlined) (used: Expression) =
    let value = inlined.Value
    let valueText = SourceSpan.textIn source value.Span
    let readsCell =
        within value
        |> List.choose (fun part -> WrittenReferences.symbolOf typed part.Id)
        |> List.tryFind (isCell (cellsIn values))
    match used.Kind, readsCell, shadowedAt file typed value.Span [ value ] used.Span.Start with
    | _, Some cell, _ -> Error $"It reads the cell `{cell.Name}`, which may change before it is used"
    | _, _, Some name -> Error $"`{name}` would refer to something else where it is used"
    | IdentifierExpr _, _, _ -> Ok [ used.Span.Start, used.Span.End, bracketed value valueText ]
    | (FunctionCall _ | Pipe _), _, _ ->
        match value.Kind, Definition.nameSpan used with
        | IdentifierExpr _, Some name -> Ok [ name.Start, name.End, valueText ]
        | _ -> Error "It is called, and its value is not a lambda or a name"
    | _ -> Error "It is used in a way that can't be inlined"

/// The edits that inline the lambda `inlined`'s value, `{ parameters -> body }`, at the call `used`, or why it can't
/// be. Each argument that is a name or a literal, or that the body uses once and not as a function, is put in for its
/// parameter; any other is bound by a def first, before the statement that holds the call, as are the statements of a
/// body that is a block.
let private inlineCall
    (file: string)
    (source: string)
    (syntax: Module)
    (typed: TypedModule)
    (inlined: Inlined)
    (parameters: (string * TypeExpr option) list)
    (body: Expression)
    (used: Expression)
    =
    let lambda = inlined.Value
    let arguments =
        match used.Kind with
        | FunctionCall(_, _, args) -> Some (SyntaxTree.flattenCallArgs args)
        | Pipe(value, _, _, args) -> Some (SyntaxTree.flattenCallArgs (pipeCallArguments value args))
        | IdentifierExpr _ -> None
        | _ -> None
    let parameterSymbols =
        parameters
        |> List.map (fun (name, _) ->
            typed.Symbols
            |> Map.values
            |> Seq.tryFind (fun symbol ->
                symbol.Kind = Parameter && symbol.Name = name && SourceSpan.contains lambda.Span symbol.Declaration && symbol.Declaration.Start < body.Span.Start))
    let statements, result =
        match body.Kind with
        | Block statements ->
            match List.rev statements with
            | { Kind = ExprStatement last } :: before -> Some (List.rev before), Some last
            | _ -> Some statements, None
        | _ -> Some [], Some body
    match arguments with
    | None -> Ok [ used.Span.Start, used.Span.End, SourceSpan.textIn source lambda.Span ]
    | Some arguments ->
    let arguments = match arguments with [ { Kind = UnitExpr } ] when parameters.IsEmpty -> [] | _ -> arguments
    let isNamed (argument: Expression) = match argument.Kind with RecordExpr fields -> fields |> List.exists (function NamedField _ -> true | _ -> false) | _ -> false
    let bodyParts = body |> within
    let usesOf (symbol: Symbol) = bodyParts |> List.filter (fun part -> WrittenReferences.ofModule typed |> Map.tryFind part.Id = Some symbol.Id)
    if parameters |> List.exists (fun (_, annotation) -> match annotation with CellPassingSyntax.CellParameter _ -> true | _ -> false) then
        Error "It takes a cell"
    elif arguments |> List.exists isNamed then Error "It is called with named arguments"
    elif arguments.Length <> parameters.Length then Error $"It is called with {arguments.Length} arguments, not {parameters.Length}"
    elif parameterSymbols |> List.exists Option.isNone then Error "A parameter is not a name"
    elif result.IsNone then Error "Its body does not end with a value"
    elif bodyParts |> List.exists (fun part -> match part.Kind with WorkflowBindExpr _ | WorkflowReturnExpr _ -> true | _ -> false) then
        Error "It uses `try` or `return`, whose meaning depends on the block it is written in"
    else
    let pairs = List.zip (List.map Option.get parameterSymbols) arguments
    let isSimple (argument: Expression) = match argument.Kind with IdentifierExpr _ | LiteralExpr _ | UnitExpr -> true | _ -> false
    // A parameter whose argument is put in where it is used, or bound by a def first.
    let putIn, bound =
        pairs
        |> List.partition (fun (symbol, argument) ->
            let uses = usesOf symbol
            isSimple argument
            || (uses.Length = 1 && uses |> List.forall (fun part -> match part.Kind with IdentifierExpr _ -> true | _ -> false)))
    let argumentText (argument: Expression) = SourceSpan.textIn source argument.Span
    let substitutions =
        putIn
        |> List.collect (fun (symbol, argument) ->
            usesOf symbol
            |> List.choose (fun part ->
                match part.Kind with
                | IdentifierExpr _ -> Some (part.Span.Start, part.Span.End, bracketed argument (argumentText argument))
                | _ -> Definition.nameSpan part |> Option.map (fun name -> name.Start, name.End, argumentText argument)))
    // The names the body declares, which an argument put in must not use, and which the defs it hoists must not hide.
    let declaredInBody =
        typed.Symbols
        |> Map.values
        |> Seq.filter (fun symbol -> SourceSpan.contains body.Span symbol.Declaration && symbol.Kind <> TopLevelValue)
        |> Seq.map (fun symbol -> symbol.Name)
        |> Set.ofSeq
    let argumentNames = putIn |> List.collect (snd >> within) |> List.choose (fun part -> match part.Kind with IdentifierExpr name -> Some name | _ -> None)
    let statements = Option.get statements
    let result = Option.get result
    let hoisted =
        (bound |> List.map (fun (symbol, argument) -> $"def {symbol.Name} = {argumentText argument}"))
        @ (statements |> List.map (fun statement -> textWith source statement.Span substitutions))
    let hoistedNames =
        (bound |> List.map (fun (symbol, _) -> symbol.Name))
        @ (statements |> List.choose (fun statement -> match statement.Kind with DefStatement(_, name, _, _) -> Some name | _ -> None))
    let resultText = textWith source result.Span substitutions
    match argumentNames |> List.tryFind declaredInBody.Contains, shadowedAt file typed lambda.Span [ body ] used.Span.Start with
    | Some name, _ -> Error $"`{name}` in an argument would refer to the function's own `{name}`"
    | _, Some name -> Error $"`{name}` would refer to something else where it is called"
    | None, None when hoisted.IsEmpty -> Ok [ used.Span.Start, used.Span.End, bracketed result resultText ]
    | None, None ->
        let item = syntax |> List.find (fun item -> SourceSpan.contains item.Span used.Span)
        let site =
            match item.Kind with
            | Def (ValueDef(_, _, _, value))
            | Expr value -> SelectedExpression.findIn source used.Span (SelectedExpression.BeforeItem item) None value
            | _ -> None
        // Names the item uses or declares outside the function, which a hoisted def would hide or clash with.
        let namesAround =
            let outside (span: SourceSpan) = not (SourceSpan.contains lambda.Span span)
            (topLevelValues [ item ] |> List.collect within |> List.filter (fun part -> outside part.Span) |> List.collect SyntaxTree.referencedNames)
            @ (typed.Symbols |> Map.values |> Seq.filter (fun symbol -> SourceSpan.contains item.Span symbol.Declaration && outside symbol.Declaration) |> Seq.map (fun symbol -> symbol.Name) |> List.ofSeq)
            |> Set.ofList
        match site, hoistedNames |> List.tryFind namesAround.Contains with
        | _, Some name -> Error $"Its `{name}` would hide or clash with a name where it is called"
        | Some (_, _, Some reason), _ -> Error $"{reason}: its statements would run always before the statement"
        | Some (_, SelectedExpression.BeforeStatement statement, None), _ ->
            let lineStart, indent = lineOf source statement.Span.Start
            if source.Substring(lineStart, statement.Span.Start - lineStart).Trim() <> "" then Error "The statement that holds it is not on a line of its own"
            else
                let at = insertionPoint source [] statement.Span.Start
                let lines =
                    hoisted
                    |> List.map (fun text -> text.Split '\n' |> Array.mapi (fun index line -> if index = 0 then indent + line else line) |> String.concat "\n")
                    |> String.concat "\n"
                Ok [ at, at, lines + "\n"; used.Span.Start, used.Span.End, bracketed result resultText ]
        | _ -> Error "It is not in a block, where its statements and arguments could go first"

/// The refactorings at `offset` in `source`, the text of the root module of `result` compiled as `file`, laid out with
/// `options`, on the name of a def or a use of it: inlining it at that use, and at every use, removing the def. A
/// value that is not a lambda is put in for each use, in brackets where it may need them. A lambda's calls become its
/// body, with each argument put in for its parameter if it is a name or a literal, or if the body uses the parameter
/// once; any other is bound by a def first, so nothing is evaluated more or fewer times than before. Those defs, and the
/// statements of a body that is a block, go before the statement that holds the call. `recompile` compiles a text of
/// the module as `result` was compiled, and an inlining is refused if its text would not compile.
///
/// Refused where the def is recursive, is a cell or reads one, calls a function that requires a context (a value
/// only, which inlining would run at another time), is exported (inlined at a use only, never removed), where a name its
/// value or body uses would refer to something else at a use, and where an argument would be taken by a name the body
/// declares. Empty where no def of the module's own is named at `offset`, or where the module did not type-check.
let at
    (options: Formatter.Options)
    (file: string)
    (source: string)
    (result: CompileResult)
    (offset: int)
    (recompile: string -> CompileResult)
    : Offer list =
    let source = normalised source
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        let named =
            Definition.symbolAndNameAt file result offset
            |> Option.orElseWith (fun () -> if offset > 0 then Definition.symbolAndNameAt file result (offset - 1) else None)
            |> Option.filter (fun (symbol, _) ->
                symbol.Declaration.File = file
                && not (SourceSpan.isNone symbol.Declaration)
                && not (MadeUpNames.isMadeUp symbol.Name)
                && (symbol.Kind = TopLevelValue || symbol.Kind = LocalValue))
        match named with
        | None -> []
        | Some (symbol, nameSpan) ->
            let allTitle = $"Inline `{symbol.Name}`"
            let thisTitle = $"Inline this use of `{symbol.Name}`"
            let onDeclaration = nameSpan = symbol.Declaration
            let refused reason = [ Refused((if onDeclaration then allTitle else thisTitle), RefactoringKind.Inline, reason) ]
            match declarationOf source syntax symbol with
            | Error reason -> refused reason
            | Ok inlined ->
                let uses = usesOf typed syntax inlined
                let values = topLevelValues syntax
                let requiresAContext =
                    match inlined.Value.Kind with
                    | Lambda _ -> false
                    | _ -> requiresAContext typed (within inlined.Value)
                let isFunction = match inlined.Value.Kind with Lambda _ -> SelectedExpression.isWritten source inlined.Value | _ -> false
                if uses |> List.exists (fun used -> SourceSpan.contains inlined.Value.Span used.Span) then refused "It is recursive"
                elif requiresAContext then refused "It calls a function that requires a context, which inlining would run at another time"
                else
                    let editsAt (used: Expression) =
                        match inlined.Value.Kind with
                        | Lambda(parameters, body, _) when isFunction -> inlineCall file source syntax typed inlined parameters body used
                        | _ -> inlineValue file source typed values inlined used
                    let offer (offerTitle: string) (edits: Result<(int * int * string) list, string>) =
                        match edits with
                        | Error reason -> Refused(offerTitle, RefactoringKind.Inline, reason)
                        | Ok edits ->
                            Deferred(offerTitle, RefactoringKind.Inline, fun () ->
                                let untouched = applied source edits
                                match newError result (recompile untouched) with
                                | Some reason -> Refused(offerTitle, RefactoringKind.Inline, $"The inlined module would not compile{reason}")
                                | None ->
                                    let after = appliedAndFormatted options file source edits
                                    Available { Title = offerTitle; Kind = RefactoringKind.Inline; Edits = [ difference file source after ]; Rename = None })
                    let all =
                        if symbol.IsExported then Refused(allTitle, RefactoringKind.Inline, "It is exported: other modules may use it, so it is inlined at a use only")
                        else
                            let edits =
                                uses
                                |> List.fold
                                    (fun edits used ->
                                        match edits, editsAt used with
                                        | Ok edits, Ok more -> Ok (edits @ more)
                                        | Error reason, _
                                        | _, Error reason -> Error reason)
                                    (Ok [ fst inlined.Removed, snd inlined.Removed, "" ])
                            offer allTitle edits
                    let this =
                        if onDeclaration then []
                        else
                            match uses |> List.tryFind (fun used -> SourceSpan.contains used.Span nameSpan) with
                            | Some used -> [ offer thisTitle (editsAt used) ]
                            | None -> []
                    this @ [ all ]
    | _ -> []
