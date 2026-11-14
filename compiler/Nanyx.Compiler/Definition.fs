/// Where a name an editor points at is declared. Positions are character offsets into the source as the parser
/// saw it, with line endings normalised to `\n`, as for `Hover`.
module NyxCompiler.Definition

open Parser.Program

let private contains (offset: int) (span: SourceSpan) =
    not (SourceSpan.isNone span) && span.Start <= offset && offset < span.End

/// The span of the name a referring expression is written with: a call's or pipe's function name (not its
/// arguments), an operator (not its operands), a qualified context member's name after the context and its dot, or
/// all of an identifier or qualified name.
let internal nameSpan (expr: Expression) =
    match expr.Kind with
    | FunctionCall(_, nameSpan, _)
    | BinaryOp(_, nameSpan, _, _)
    | Pipe(_, _, nameSpan, _) -> nameSpan
    | ContextMemberAccess(context, memberName)
    | ContextMemberCall(context, memberName, _) when not (SourceSpan.isNone context.Span) ->
        let start = context.Span.End + 1
        Some { expr.Span with Start = start; End = start + memberName.Length; StartColumn = expr.Span.StartColumn + start - expr.Span.Start }
    | _ -> Some expr.Span

/// The span of the name a type expression refers to a type with: all of a type name, or the name before an
/// applied type's arguments.
let internal typeNameSpan (typeExpr: TypeExpr) =
    match typeExpr.Kind with
    | TypeName _ -> Some typeExpr.Span
    | TypeApply(name, _) -> Some { typeExpr.Span with End = min typeExpr.Span.End (typeExpr.Span.Start + name.Length) }
    | _ -> None

/// The symbol for the name at `offset`, as `symbolAt` finds it, with the span of the name.
let private symbolAndNameAt (file: string) (result: CompileResult) (offset: int) : (Symbol * SourceSpan) option =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        let referencedAt (id: NodeId) (span: SourceSpan option) =
            match typed.References |> Map.tryFind id, span with
            | Some id, Some span when contains offset span ->
                typed.Symbols
                |> Map.tryFind id
                // What syntax is desugared to (`__pow` for `**`) is not a name the user wrote: the expression inside it is.
                |> Option.filter (fun symbol -> not (ReservedNames.isHidden symbol))
                |> Option.map (fun symbol -> symbol, span)
            | _ -> None
        let referenced =
            Hover.expressionsAt offset syntax
            |> List.tryPick (fun expr -> referencedAt expr.Id (nameSpan expr))
            |> Option.orElseWith (fun () ->
                Hover.typeExprsAt offset syntax |> List.tryPick (fun typeExpr -> referencedAt typeExpr.Id (typeNameSpan typeExpr)))
        match referenced with
        | Some _ -> referenced
        | None ->
            typed.Symbols
            |> Map.values
            |> Seq.filter (fun symbol -> symbol.Declaration.File = file && contains offset symbol.Declaration)
            |> Seq.sortBy (fun symbol -> symbol.Declaration.End - symbol.Declaration.Start)
            |> Seq.tryHead
            |> Option.map (fun symbol -> symbol, symbol.Declaration)
    | _ -> None

/// The symbol for the name at `offset` in the compiled root module: what it refers to, or, on a declaration's
/// own name, the symbol it declares. `file` is the file the root module was compiled as (empty for source
/// compiled directly), which tells its own declarations from those of the modules it imports. A reserved name of the
/// standard library's (see `ReservedNames.isHidden`) is never the symbol. `None` when the module did not type-check
/// or no name is at `offset`.
let symbolAt (file: string) (result: CompileResult) (offset: int) : Symbol option =
    symbolAndNameAt file result offset |> Option.map fst

/// The type `symbol` names, as `typed` defines it, if it is one.
let internal typeDefinitionOf (typed: TypedModule) (symbol: Symbol) : TypeDefInfo option =
    typed.TypeDefs |> Map.tryPick (fun _ info -> if info.Symbol = Some symbol.Id then Some info else None)

/// The definition of the type `symbol` names, `info`, as `type Name(params) = <the type it stands for>` (or
/// `context Name = ...` or `scoped context Name = ...`), with `protected` or `private` before the type a nominal one
/// stands for, written by `writeShape`, and a note for a nominal type that says its kind and the module that declares
/// it. `isHome` says whether it is read in that module: a private type's shape is left out anywhere else.
let internal writtenTypeDefinition (writeShape: Ty -> string) (symbol: Symbol) (info: TypeDefInfo) (isHome: bool) : string * string option =
    let keyword =
        if info.IsScoped then "scoped context"
        elif info.IsContext then "context"
        else "type"
    let parameters =
        match info.Parameters |> List.choose (fun var -> var.Name) with
        | [] -> ""
        | parameterNames -> "(" + String.concat ", " parameterNames + ")"
    let kind =
        if info.IsPrivate then "private"
        elif info.IsNominal then "protected"
        else ""
    let declared = if isHome then "this module" else Unifier.moduleText info.Home
    // A private type's shape is its home module's to know.
    let definition =
        if info.IsPrivate && not isHome then $"{keyword} {symbol.Name}{parameters}"
        elif info.IsNominal then $"{keyword} {symbol.Name}{parameters} = {kind} {writeShape info.Underlying}"
        else $"{keyword} {symbol.Name}{parameters} = {writeShape info.Underlying}"
    let note =
        let what = if info.IsContext then keyword else "type"
        if info.IsNominal then Some $"A {kind} {what}, declared in {declared}." else None
    definition, note

/// What hovering a type's name shows: its definition, as `writtenTypeDefinition` writes it, with made-up type
/// variables named by `names`, over the span of the name. The name can be in an annotation, a type definition (its
/// own or another's) or a signature's contexts, and the type can be the module's own or an imported one. `None` when
/// no type's name is at `offset`.
let typeDefinitionAt (names: TypeNotation) (file: string) (result: CompileResult) (offset: int) : Hover.HoverInfo option =
    match result.Typed, symbolAndNameAt file result offset with
    | Some typed, Some (symbol, span) when symbol.Kind = TypeDefinition ->
        typeDefinitionOf typed symbol
        |> Option.map (fun info ->
            let definition, note = writtenTypeDefinition (Unifier.tyToDisplayString names) symbol info (symbol.Declaration.File = file)
            { Type = definition; Span = span; Note = note })
    | _ -> None

/// The span of every name in the compiled root module that refers to `symbol`, in the order they are written: a
/// call's or pipe's function name, an operator, an identifier or qualified name, a qualified context member's name,
/// or a type name. For an import, the alias each qualified name it brought in is written with (`m` in `m.foo`). The
/// declaration itself is not among them. Empty when the module did not type-check.
let referencesTo (result: CompileResult) (symbol: Symbol) : SourceSpan list =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        let refersTo (table: Map<NodeId, SymbolId>) (id: NodeId) = table |> Map.tryFind id = Some symbol.Id
        let expressionSpans =
            Hover.allExpressions syntax
            |> List.choose (fun expr ->
                if refersTo typed.References expr.Id then
                    nameSpan expr
                elif symbol.Kind = ImportedModule && refersTo typed.ImportReferences expr.Id then
                    // The name is written under the import's alias, which is its first segment.
                    Some { expr.Span with End = min expr.Span.End (expr.Span.Start + symbol.Name.Length) }
                else
                    None)
        let typeSpans =
            (syntax |> List.collect SyntaxTree.topLevelTypes) @ (Hover.allExpressions syntax |> List.collect SyntaxTree.expressionTypes)
            |> List.collect TypeExpr.within
            |> List.filter (fun typeExpr -> refersTo typed.References typeExpr.Id)
            |> List.choose typeNameSpan
        // A node desugaring made up can share the span of the syntax it came from.
        expressionSpans @ typeSpans
        |> List.filter (SourceSpan.isNone >> not)
        |> List.distinct
        |> List.sortBy (fun span -> span.Start)
    | _ -> []
