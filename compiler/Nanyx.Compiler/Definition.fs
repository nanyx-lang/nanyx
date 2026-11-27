/// Where a name an editor points at is declared. Positions are character offsets into the source as the parser
/// saw it, with line endings normalised to `\n`, as for `Hover`.
module NyxCompiler.Definition

open Parser.Program

let private contains (offset: int) (span: SourceSpan) =
    not (SourceSpan.isNone span) && span.Start <= offset && offset < span.End

/// The span of the name a referring expression is written with: a call's or pipe's function name (not its
/// arguments), an operator (not its operands), a qualified context member's name after the context and its dot, a
/// number's unit (`km` in `2km`), or all of an identifier or qualified name.
let internal nameSpan (expr: Expression) =
    match expr.Kind with
    | LiteralExpr(UnitLit(_, unitName)) ->
        Some { expr.Span with Start = expr.Span.End - unitName.Length; StartColumn = expr.Span.StartColumn + expr.Span.End - unitName.Length - expr.Span.Start }
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
let internal symbolAndNameAt (file: string) (result: CompileResult) (offset: int) : (Symbol * SourceSpan) option =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        let referencedAt (id: NodeId) (span: SourceSpan option) =
            match WrittenReferences.ofModule typed |> Map.tryFind id, span with
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
/// stands for, written by `writeShape`, after the scoped context whose scope it belongs to, if any (`[Memory] private
/// box(a)`), and a note for a nominal type that says its kind and the module that declares it. `isHome` says whether it
/// is read in that module: a private type's shape is left out anywhere else.
let internal writtenTypeDefinition (writeShape: Ty -> string) (symbol: Symbol) (info: TypeDefInfo) (isHome: bool) : string * string option =
    let keyword =
        if info.IsScoped then "scoped context"
        elif info.IsContext then "context"
        else "type"
    // A context's associated types are its last parameters, which it declares as members (see `AssociatedTypes`).
    let parameters =
        match info.Parameters |> List.truncate (info.Parameters.Length - info.AssociatedTypes.Length) |> List.choose (fun var -> var.Name) with
        | [] -> ""
        | parameterNames -> "(" + String.concat ", " parameterNames + ")"
    let info =
        match info.AssociatedTypes, Unifier.unalias info.Underlying with
        | [], _ -> info
        | associated, TyRecord(fields, FieldOrder order) ->
            let fields = associated |> List.fold (fun acc name -> acc |> Map.add name (TyPrimitive "type")) fields
            { info with Underlying = TyRecord(fields, FieldOrder(associated @ order)) }
        | _ -> info
    let kind =
        if info.IsPrivate then "private"
        elif info.IsNominal then "protected"
        else ""
    let scopedKind =
        match info.ScopedBy, info.ContextVariable |> Option.bind _.Name with
        | Some scope, _ -> $"[{scope}] {kind}"
        | None, Some contexts -> $"[{contexts}] {kind}"
        | None, None -> kind
    let declared = if isHome then "this module" else Unifier.moduleText info.Home
    // A private type's shape is its home module's to know.
    let definition =
        if info.IsPrivate && not isHome then $"{keyword} {symbol.Name}{parameters}"
        elif info.IsNominal then
            // One that names itself is written with its own name there (see `RecursiveNominals`), and without the
            // contexts it carries there, which are its own.
            let id = TypeDefInfo.nominalId info
            let rec asWritten (ty: Ty) =
                match ty with
                | TyNominal(nominal, args, underlying, isPrivate) when nominal = id ->
                    let args = args |> List.map (function FunctionContexts.Carried _ -> FunctionContexts.carried [] | arg -> asWritten arg)
                    TyNominal(nominal, args, underlying, isPrivate)
                | TyRecursive _ -> ty
                | _ -> Ty.map asWritten ty
            let shape = asWritten (RecursiveNominals.underlying (TyNominal(id, [], info.Underlying, info.IsPrivate)))
            $"{keyword} {symbol.Name}{parameters} = {scopedKind} {writeShape shape}"
        else $"{keyword} {symbol.Name}{parameters} = {writeShape info.Underlying}"
    let note =
        let what = if info.IsContext then keyword else "type"
        if info.IsNominal then Some $"A {kind} {what}, declared in {declared}." else None
    definition, note

/// `typeDefs` without the type `info` defines, under any name: its definition is not written with its own name.
let internal typeDefsBesides (info: TypeDefInfo) (typeDefs: Map<string, TypeDefInfo>) =
    typeDefs |> Map.filter (fun _ other -> TypeDefInfo.nominalId other <> TypeDefInfo.nominalId info)

/// The definition of the type `symbol` names, `info`, in the compiled root module, compiled as `file`, as hover writes it
/// (see `writtenTypeDefinition`), with made-up type variables named by `names`, and the note after it.
let internal typeDefinitionWritten (names: TypeNotation) (file: string) (typed: TypedModule) (symbol: Symbol) (info: TypeDefInfo) : string * string option =
    writtenTypeDefinition (Unifier.tyToDisplayStringIn (typeDefsBesides info typed.TypeDefs) names) symbol info (symbol.Declaration.File = file)

/// What hovering a type's name shows: its definition, as `writtenTypeDefinition` writes it, with made-up type
/// variables named by `names`, over the span of the name. The name can be in an annotation, a type definition (its
/// own or another's) or a signature's contexts, and the type can be the module's own or an imported one. `None` when
/// no type's name is at `offset`.
let typeDefinitionAt (names: TypeNotation) (file: string) (result: CompileResult) (offset: int) : Hover.HoverInfo option =
    match result.Typed, symbolAndNameAt file result offset with
    | Some typed, Some (symbol, span) when symbol.Kind = TypeDefinition ->
        typeDefinitionOf typed symbol
        |> Option.map (fun info ->
            let definition, note = typeDefinitionWritten names file typed symbol info
            { Type = definition; Span = span; Note = note })
    | _ -> None

/// Every name in the compiled root module that refers to a symbol, with the symbol, in the order they are written: a
/// call's or pipe's function name, an operator, an identifier or qualified name, a qualified context member's name, or
/// a type name. Empty when the module did not type-check.
let internal referenceSites (result: CompileResult) : (SourceSpan * SymbolId) list =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        let referenced (id: NodeId) (span: SourceSpan option) =
            match WrittenReferences.ofModule typed |> Map.tryFind id, span with
            | Some symbol, Some span -> Some (span, symbol)
            | _ -> None
        let expressionSites = Hover.allExpressions syntax |> List.choose (fun expr -> referenced expr.Id (nameSpan expr))
        let typeSites =
            (syntax |> List.collect SyntaxTree.topLevelTypes) @ (Hover.allExpressions syntax |> List.collect SyntaxTree.expressionTypes)
            |> List.collect TypeExpr.within
            |> List.choose (fun typeExpr -> referenced typeExpr.Id (typeNameSpan typeExpr))
        // A node desugaring made up can share the span of the syntax it came from.
        expressionSites @ typeSites
        |> List.filter (fst >> SourceSpan.isNone >> not)
        |> List.distinct
        |> List.sortBy (fun (span, _) -> span.Start)
    | _ -> []

/// The span of every name in the compiled root module that refers to `symbol`, in the order they are written, as
/// `referenceSites` finds them. For an import, the alias each qualified name it brought in is written with (`m` in
/// `m.foo`). The declaration itself is not among them. Empty when the module did not type-check.
let referencesTo (result: CompileResult) (symbol: Symbol) : SourceSpan list =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        let aliasSpans =
            if symbol.Kind <> ImportedModule then []
            else
                Hover.allExpressions syntax
                |> List.filter (fun expr -> WrittenReferences.ofModule typed |> Map.tryFind expr.Id <> Some symbol.Id && typed.ImportReferences |> Map.tryFind expr.Id = Some symbol.Id)
                // The name is written under the import's alias, which is its first segment.
                |> List.map (fun expr -> { expr.Span with End = min expr.Span.End (expr.Span.Start + symbol.Name.Length) })
        (referenceSites result |> List.filter (fun (_, id) -> id = symbol.Id) |> List.map fst) @ aliasSpans
        |> List.filter (SourceSpan.isNone >> not)
        |> List.distinct
        |> List.sortBy (fun span -> span.Start)
    | _ -> []

/// Whether `a` and `b`, symbols of two compiles, are the same name: symbol ids are made afresh by each compile, so the
/// same declaration, of a name of the same kind, is the same name.
let sameSymbol (a: Symbol) (b: Symbol) =
    a.Name = b.Name
    && a.Kind = b.Kind
    && a.Declaration.File = b.Declaration.File
    && a.Declaration.Start = b.Declaration.Start
    && not (SourceSpan.isNone a.Declaration)

/// The span of every name in each of `results`, the compiles of a package's files, that refers to `symbol`, a symbol
/// of any compile of one of them, as `referencesTo` finds them in each, in the order of `results` and then of the text.
/// A declaration is not among them.
let referencesAcross (results: CompileResult list) (symbol: Symbol) : SourceSpan list =
    results
    |> List.collect (fun result ->
        match result.Typed with
        | Some typed ->
            typed.Symbols
            |> Map.values
            |> Seq.filter (sameSymbol symbol)
            |> Seq.toList
            |> List.collect (referencesTo result)
            |> List.distinct
            |> List.sortBy (fun span -> span.Start)
        | None -> [])
