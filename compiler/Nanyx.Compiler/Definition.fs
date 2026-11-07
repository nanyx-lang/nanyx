/// Where a name an editor points at is declared. Positions are character offsets into the source as the parser
/// saw it, with line endings normalised to `\n`, as for `Hover`.
module NyxCompiler.Definition

open Parser.Program

let private contains (offset: int) (span: SourceSpan) =
    not (SourceSpan.isNone span) && span.Start <= offset && offset < span.End

/// The span of the name a referring expression is written with: a call's or pipe's function name (not its
/// arguments), a qualified context member's name after the context and its dot, or all of an identifier or
/// qualified name.
let private nameSpan (expr: Expression) =
    match expr.Kind with
    | FunctionCall(_, nameSpan, _)
    | Pipe(_, _, nameSpan, _) -> nameSpan
    | ContextMemberAccess(context, memberName)
    | ContextMemberCall(context, memberName, _) when not (SourceSpan.isNone context.Span) ->
        let start = context.Span.End + 1
        Some { expr.Span with Start = start; End = start + memberName.Length; StartColumn = expr.Span.StartColumn + start - expr.Span.Start }
    | _ -> Some expr.Span

/// The type expressions directly inside `typeExpr`.
let private typeChildren (typeExpr: TypeExpr) : TypeExpr list =
    match typeExpr.Kind with
    | TypeName _
    | TypeVar _
    | TypeUnit
    | TypeOf _ -> []
    | TypeTuple items
    | TypeUnion items
    | TypeIntersection items
    | TypeContext items
    | TypeApply(_, items) -> items
    | TypeRecord fields ->
        fields |> List.choose (function TypeField(_, _, _, typeOpt, _) | TypeMember(_, typeOpt) -> typeOpt)
    | TypeFunc(arg, ret) -> [ arg; ret ]
    | TypeTag(_, payload) -> Option.toList payload
    | TypeWithContext(contexts, inner) -> contexts @ [ inner ]
    | TypeOptional inner
    | TypeConstraint(_, inner, _)
    | TypeWhere(inner, _) -> [ inner ]

/// The types written in a definition: its annotation, or a type definition's parameters' constraints and body.
let private definitionTypes (definition: Definition) : TypeExpr list =
    match definition with
    | ValueDef(_, _, typeOpt, _) -> Option.toList typeOpt
    | TypeDef(_, _, parameters, body) -> (parameters |> List.choose snd) @ [ body ]

/// The types written directly in `expr` (not in the expressions inside it): its lambda's parameters' annotations,
/// its block's defs' annotations and type definitions, the context it names.
let private expressionTypes (expr: Expression) : TypeExpr list =
    match expr.Kind with
    | Lambda(parameters, _) -> parameters |> List.choose snd
    | Block statements ->
        statements
        |> List.collect (fun statement ->
            match statement.Kind with
            | DefStatement(_, _, typeOpt, _) -> Option.toList typeOpt
            | TypeDefStatement(_, _, parameters, body) -> (parameters |> List.choose snd) @ [ body ]
            | UseStatement (UseContextInstance(context, _)) -> [ context ]
            | UseStatement (UseValue _)
            | ExprStatement _
            | ImportStatement _ -> [])
    | ContextMemberAccess(context, _)
    | ContextMemberCall(context, _, _)
    | UseIn(UseContextInstance(context, _), _) -> [ context ]
    | _ -> []

let rec private topLevelTypes (item: TopLevelItem) : TypeExpr list =
    match item.Kind with
    | Def definition -> definitionTypes definition
    | ModuleBlock(_, items) -> items |> List.collect topLevelTypes
    | Expr _
    | ModuleDecl _
    | Import _
    | Attribute _ -> []

/// The type expressions whose spans contain `offset`, innermost first.
let private typeExprsAt (offset: int) (module': Module) : TypeExpr list =
    let rec search (found: TypeExpr list) (typeExpr: TypeExpr) : TypeExpr list =
        if contains offset typeExpr.Span then typeChildren typeExpr |> List.fold search (typeExpr :: found)
        else found
    let written = (module' |> List.collect topLevelTypes) @ (Hover.expressionsAt offset module' |> List.collect expressionTypes)
    written |> List.fold search []

/// The span of the name a type expression refers to a type with: all of a type name, or the name before an
/// applied type's arguments.
let private typeNameSpan (typeExpr: TypeExpr) =
    match typeExpr.Kind with
    | TypeName name
    | TypeApply(name, _) -> Some { typeExpr.Span with End = min typeExpr.Span.End (typeExpr.Span.Start + name.Length) }
    | _ -> None

/// The symbol for the name at `offset` in the compiled root module: what it refers to, or, on a declaration's
/// own name, the symbol it declares. `file` is the file the root module was compiled as (empty for source
/// compiled directly), which tells its own declarations from those of the modules it imports. `None` when the
/// module did not type-check or no name is at `offset`.
let symbolAt (file: string) (result: CompileResult) (offset: int) : Symbol option =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        let referencedAt (id: NodeId) (span: SourceSpan option) =
            match typed.References |> Map.tryFind id, span with
            | Some id, Some span when contains offset span -> typed.Symbols |> Map.tryFind id
            | _ -> None
        let referenced =
            Hover.expressionsAt offset syntax
            |> List.tryPick (fun expr -> referencedAt expr.Id (nameSpan expr))
            |> Option.orElseWith (fun () ->
                typeExprsAt offset syntax |> List.tryPick (fun typeExpr -> referencedAt typeExpr.Id (typeNameSpan typeExpr)))
        match referenced with
        | Some _ -> referenced
        | None ->
            typed.Symbols
            |> Map.values
            |> Seq.filter (fun symbol -> symbol.Declaration.File = file && contains offset symbol.Declaration)
            |> Seq.sortBy (fun symbol -> symbol.Declaration.End - symbol.Declaration.Start)
            |> Seq.tryHead
    | _ -> None
