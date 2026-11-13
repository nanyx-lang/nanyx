/// The declarations a module makes, as an editor's outline shows them. Only a parse is needed, not a type-check,
/// so a module with a type error still has an outline. Positions are character offsets into the source as the
/// parser saw it, with line endings normalised to `\n`, as for `Hover`.
module NyxCompiler.Outline

open Parser.Program

[<RequireQualifiedAccess>]
type OutlineKind =
    /// A def whose value is a lambda.
    | Function
    /// Any other def.
    | Value
    | RecordType
    | UnionType
    /// A type that stands for another, neither a record nor a union: `type Count = int`.
    | OtherType
    | ContextType
    /// A context declared `scoped context`.
    | ScopedContextType
    | RecordField
    /// A member of a context whose type is a function.
    | ContextFunction
    | ContextValue
    /// A `module name =` block.
    | ModuleBlock

/// A declaration: its name, what it is, the span of all of it and of its name, and the declarations inside it.
type OutlineItem =
    { Name: string
      Kind: OutlineKind
      Span: SourceSpan
      NameSpan: SourceSpan
      Children: OutlineItem list }

/// The name `holder` declares as `declarer`, and where it is written, if the parser saw one.
let private declaredName (declared: DeclaredNames) (holder: NodeId) (declarer: Declarer) =
    declared |> Map.tryFind (holder, declarer) |> Option.bind List.tryHead

/// A type with any `where` predicate taken off: what it is a type of.
let rec private constrained (typeExpr: TypeExpr) =
    match typeExpr.Kind with
    | TypeWhere(inner, _) -> constrained inner
    | _ -> typeExpr

/// The named fields of a record type, each with its type and where its name is written.
let private fieldsOf (declared: DeclaredNames) (record: TypeExpr) =
    let names = declared |> Map.tryFind (record.Id, RecordTypeFields) |> Option.defaultValue []
    match record.Kind with
    | TypeRecord fields ->
        fields
        |> List.choose (function
            | TypeField(name, _, typeOpt, _) ->
                names |> List.tryFind (fun (declaredName, _) -> declaredName = name) |> Option.map (fun (_, span) -> name, typeOpt, span)
            | TypeMember _ -> None)
    | _ -> []

let private typeItem (declared: DeclaredNames) (span: SourceSpan) (modifiers: TypeDefModifier list) (body: TypeExpr) =
    declaredName declared body.Id TypeDefBody
    |> Option.map (fun (name, nameSpan) ->
        let core = constrained body
        let field kind (fieldName, _, fieldSpan) =
            { Name = fieldName; Kind = kind; Span = fieldSpan; NameSpan = fieldSpan; Children = [] }
        let kind, children =
            if modifiers |> List.contains Context then
                let memberKind (_, typeOpt: TypeExpr option, _) =
                    match typeOpt |> Option.map (fun t -> t.Kind) with
                    | Some (TypeFunc _) -> OutlineKind.ContextFunction
                    | _ -> OutlineKind.ContextValue
                let contextKind = if modifiers |> List.exists TypeDefModifier.isScoped then OutlineKind.ScopedContextType else OutlineKind.ContextType
                contextKind, fieldsOf declared core |> List.map (fun f -> field (memberKind f) f)
            else
                match core.Kind with
                | TypeRecord _ -> OutlineKind.RecordType, fieldsOf declared core |> List.map (field OutlineKind.RecordField)
                | TypeUnion _
                | TypeTag _ -> OutlineKind.UnionType, []
                | _ -> OutlineKind.OtherType, []
        { Name = name; Kind = kind; Span = span; NameSpan = nameSpan; Children = children })

/// The defs and types declared in `value`'s block, or in the block that is its lambda's body.
let rec private localItems (declared: DeclaredNames) (value: Expression) : OutlineItem list =
    let statements =
        match value.Kind with
        | Block statements
        | Lambda(_, Kind (Block statements), _) -> statements
        | _ -> []
    statements
    |> List.choose (fun statement ->
        match statement.Kind with
        | DefStatement(_, _, _, defValue) -> defItem declared statement.Span defValue
        | TypeDefStatement(_, modifiers, _, body) -> typeItem declared statement.Span modifiers body
        | ImportStatement _
        | UseStatement _
        | ExprStatement _ -> None)

/// The def whose value is `value`. A def the compiler made up declares no name, and is left out.
and private defItem (declared: DeclaredNames) (span: SourceSpan) (value: Expression) =
    declaredName declared value.Id DefValue
    |> Option.map (fun (name, nameSpan) ->
        let kind =
            match value.Kind with
            | Lambda _ -> OutlineKind.Function
            | _ -> OutlineKind.Value
        { Name = name; Kind = kind; Span = span; NameSpan = nameSpan; Children = localItems declared value })

/// The span of `text`, written at `offset` in `source`.
let private spanIn (source: string) (file: string) (offset: int) (text: string) : SourceSpan =
    let before = source.Substring(0, offset)
    let line = before |> Seq.filter (fun c -> c = '\n') |> Seq.length
    { File = file; Start = offset; End = offset + text.Length; StartLine = line; StartColumn = offset - (before.LastIndexOf '\n' + 1) }

let rec private topLevelItems (source: string) (declared: DeclaredNames) (items: TopLevelItem list) : OutlineItem list =
    items
    |> List.choose (fun item ->
        match item.Kind with
        | Def (ValueDef(_, _, _, value)) -> defItem declared item.Span value
        | Def (TypeDef(_, modifiers, _, body)) -> typeItem declared item.Span modifiers body
        | ModuleBlock(name, inner) when not (SourceSpan.isNone item.Span) ->
            // The block's name is the first time it is written in the block.
            let index = source.IndexOf(name, item.Span.Start, System.StringComparison.Ordinal)
            let nameSpan =
                if index >= 0 && index + name.Length <= item.Span.End then spanIn source item.Span.File index name
                else { item.Span with End = item.Span.Start }
            Some { Name = name; Kind = OutlineKind.ModuleBlock; Span = item.Span; NameSpan = nameSpan; Children = topLevelItems source declared inner }
        | ModuleBlock _
        | ModuleDecl _
        | Import _
        | ReExport _
        | Expr _
        | Attribute _ -> None)

/// `items` with those inside a `module name =` block (each block's name and text, as the parser found them) moved
/// into an item for the block. The parser lifts a block's defs to the top level, under qualified names, so the
/// blocks are found by where they are written.
let rec private nestInBlocks (source: string) (blocks: (ModuleName * SourceSpan) list) (items: OutlineItem list) : OutlineItem list =
    let isInside (outer: SourceSpan) (inner: SourceSpan) = outer <> inner && SourceSpan.contains outer inner
    let outermost = blocks |> List.filter (fun (_, span) -> not (blocks |> List.exists (fun (_, other) -> isInside other span)))
    let blockItems =
        outermost
        |> List.map (fun (name, span) ->
            // The block's name is the last segment of the qualified name a nested block has.
            let written = name.Substring(name.LastIndexOf '.' + 1)
            let index = source.IndexOf(written, span.Start, System.StringComparison.Ordinal)
            let nameSpan =
                if index >= 0 && index + written.Length <= span.End then spanIn source span.File index written
                else { span with End = span.Start }
            { Name = written
              Kind = OutlineKind.ModuleBlock
              Span = span
              NameSpan = nameSpan
              Children =
                nestInBlocks
                    source
                    (blocks |> List.filter (fun (_, other) -> isInside span other))
                    (items |> List.filter (fun item -> SourceSpan.contains span item.Span)) })
    let loose = items |> List.filter (fun item -> not (outermost |> List.exists (fun (_, span) -> SourceSpan.contains span item.Span)))
    loose @ blockItems |> List.sortBy (fun item -> item.Span.Start)

/// The outline of `source`, the text of the file at `file`: its top-level declarations in the order they are
/// written, each with those inside it. `None` when the source does not parse.
let ofSource (file: string) (source: string) : OutlineItem list option =
    match parseModuleFileWithNames file source with
    | Result.Ok (module', declared, blocks) ->
        let normalized = source.Replace("\r\n", "\n").Replace("\r", "\n")
        Some (topLevelItems normalized declared module' |> nestInBlocks normalized blocks)
    | Result.Error _ -> None
