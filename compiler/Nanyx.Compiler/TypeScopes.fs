/// The types the info view describes (see `InfoView`): the type at a position, or the one selected, and the `type`,
/// `context`, `dimension` and `unit` declarations around a position. Positions are character offsets into the source as
/// the parser saw it, as for `Hover`.
module NyxCompiler.TypeScopes

open Parser.Program

/// A parameter of a type's or a context's declaration: its name, and the contexts it is bounded by, as written. A
/// context's associated types (`elem` in `context Iter(c) = (elem: type, ...)`) are its parameters too.
type TypeParameter = { Name: string; Bounds: string list; IsAssociated: bool }

/// A `type`, `context`, `dimension` or `unit` declaration around a position. `Heading` is its keyword and name, with its
/// parameters, as declared: `context Iter(c)`. `Name` is the span of the name it declares, which the heading links to,
/// and `Span` the span of the whole declaration. `Definition` and `Note` are what hover writes of it, and `Attached`
/// are the definitions attached to it, in the order they are declared (see `attachedTo`).
type Declaration =
    { Heading: string
      Name: SourceSpan
      Span: SourceSpan
      Definition: string
      Note: string option
      Symbol: Symbol option
      Parameters: TypeParameter list
      Attached: Symbol list }

/// What the info view describes at the top when it is a type.
type FoundType =
    /// A type's name, written at `span`: `heading` is its keyword and name (`type Point`, `context Iter`), and
    /// `definition` and `note` are what hover writes of it. A built-in type has no symbol.
    | NamedType of heading: string * definition: string * note: string option * symbol: Symbol option * span: SourceSpan
    /// The type expression selected, and the type the compiler resolves it to.
    | WrittenType of TypeExpr * Ty
    /// A type variable: its name, where the type it is written in first uses it, and the contexts that type requires
    /// of it.
    | TypeVariable of name: string * firstUse: SourceSpan * contexts: TypeExpr list

/// The keyword a type's declaration starts with, as its heading writes it.
let internal keyword (info: TypeDefInfo) =
    if info.IsDimension then UnitSyntax.dimensionKeyword
    elif info.IsScoped then "scoped context"
    elif info.IsContext then "context"
    else "type"

/// The definition of the type `symbol` names, `info`, as hover writes it, and what hover says after it: a dimension's
/// with its units (see `DimensionHover`), and any other's as `Definition.typeDefinitionWritten` writes it.
let private definitionOf (names: TypeNotation) (file: string) (typed: TypedModule) (symbol: Symbol) (info: TypeDefInfo) =
    if info.IsDimension then DimensionHover.ofDimension typed info
    else Definition.typeDefinitionWritten names file typed symbol info

/// The definitions attached to the type `symbol` declares, the defs and types named `Type.name`, in the order they are
/// declared: those in the file that declares it, beside it or anywhere else in the module.
let internal attachedTo (typed: TypedModule) (symbol: Symbol) : Symbol list =
    let prefix = symbol.Name + "."
    typed.Symbols
    |> Map.values
    |> Seq.filter (fun attached ->
        (attached.Kind = TopLevelValue || attached.Kind = TypeDefinition)
        && attached.Name.StartsWith(prefix, System.StringComparison.Ordinal)
        && attached.Declaration.File = symbol.Declaration.File
        && not (ReservedNames.isHidden attached))
    |> Seq.sortBy (fun attached -> attached.Declaration.Start)
    |> List.ofSeq

/// `written`, code, on one line, with its runs of white space made one space, as the info view writes code in a
/// heading or a context.
let internal oneLine (written: string) = System.Text.RegularExpressions.Regex.Replace(written.Trim(), @"\s+", " ")

/// The text of `typeExpr` in `source`, on one line.
let private textOf (source: string) (typeExpr: TypeExpr) = oneLine (SourceSpan.textIn source typeExpr.Span)

/// The `type`, `context`, `dimension` and `unit` declarations of the compiled root module, compiled as `file` from
/// `source`, around `offset`, innermost first, with made-up type variables named by `names`: at the top level, in a
/// module block, and in a block around `offset`.
let internal declarationsAround (names: TypeNotation) (file: string) (source: string) (result: CompileResult) (offset: int) : Declaration list =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        let symbolOf (info: TypeDefInfo) = info.Symbol |> Option.bind (fun id -> typed.Symbols |> Map.tryFind id)
        // The type a declaration declares is the first type it names, since its name comes before its body.
        let declaredIn (span: SourceSpan) =
            typed.Symbols
            |> Map.values
            |> Seq.filter (fun symbol -> symbol.Kind = TypeDefinition && SourceSpan.contains span symbol.Declaration)
            |> Seq.sortBy (fun symbol -> symbol.Declaration.Start)
            |> Seq.tryHead
        let ofType (span: SourceSpan) (parameters: (Identifier * TypeExpr option) list) =
            declaredIn span
            |> Option.bind (fun symbol -> Definition.typeDefinitionOf typed symbol |> Option.map (fun info -> symbol, info))
            |> Option.map (fun (symbol, info) ->
                let definition, note = definitionOf names file typed symbol info
                let written = parameters |> List.map fst
                let bounds (bound: TypeExpr option) =
                    match bound with
                    | Some { Kind = TypeContext contexts } -> contexts |> List.map (textOf source)
                    | Some bound -> [ textOf source bound ]
                    | None -> []
                { Heading = $"""{keyword info} {symbol.Name}{if written.IsEmpty then "" else "(" + String.concat ", " written + ")"}"""
                  Name = symbol.Declaration
                  Span = span
                  Definition = definition
                  Note = note
                  Symbol = Some symbol
                  Parameters =
                    (parameters |> List.map (fun (name, bound) -> { Name = name; Bounds = bounds bound; IsAssociated = false }))
                    @ (info.AssociatedTypes |> List.map (fun name -> { Name = name; Bounds = []; IsAssociated = true }))
                  Attached = attachedTo typed symbol })
        let ofDimension (span: SourceSpan) (name: Identifier) =
            typed.TypeDefs
            |> Map.tryFind name
            |> Option.filter _.IsDimension
            |> Option.map (fun info ->
                let symbol = symbolOf info
                let definition, note = DimensionHover.ofDimension typed info
                { Heading = $"{UnitSyntax.dimensionKeyword} {name}"
                  Name = symbol |> Option.map _.Declaration |> Option.defaultValue span
                  Span = span
                  Definition = definition
                  Note = note
                  Symbol = symbol
                  Parameters = []
                  Attached = symbol |> Option.map (attachedTo typed) |> Option.defaultValue [] })
        let ofUnit (span: SourceSpan) (name: Identifier) =
            typed.Units
            |> Map.tryFind name
            |> Option.map (fun info ->
                let symbol = info.Symbol |> Option.bind (fun id -> typed.Symbols |> Map.tryFind id)
                let definition, note = DimensionHover.ofUnit typed info
                { Heading = $"{UnitSyntax.unitKeyword} {name}"
                  Name = symbol |> Option.map _.Declaration |> Option.defaultValue span
                  Span = span
                  Definition = definition
                  Note = note
                  Symbol = symbol
                  Parameters = []
                  Attached = [] })
        let rec topLevel (items: TopLevelItem list) =
            items
            |> List.collect (fun item ->
                match item.Kind with
                | ModuleBlock(_, inner) -> topLevel inner
                | _ when not (Hover.contains offset item.Span) -> []
                | Def (TypeDef(_, _, parameters, _)) -> ofType item.Span parameters |> Option.toList
                | DimensionDecl(_, name, _, _) -> ofDimension item.Span name |> Option.toList
                | UnitDecl(_, name, _, _) -> ofUnit item.Span name |> Option.toList
                | _ -> [])
        let local =
            Hover.expressionsAt offset syntax
            |> List.collect (fun expr ->
                match expr.Kind with
                | Block statements ->
                    statements
                    |> List.choose (fun statement ->
                        match statement.Kind with
                        | TypeDefStatement(_, _, parameters, _) when Hover.contains offset statement.Span -> ofType statement.Span parameters
                        | _ -> None)
                | _ -> [])
        local @ topLevel syntax |> List.sortBy (fun declaration -> declaration.Span.End - declaration.Span.Start)
    | _ -> []

/// The type the info view describes at the top for `selection` in the compiled root module, compiled as `file` from
/// `source`, with made-up type variables named by `names`. For a selection, the type expression written exactly at it,
/// less the spaces around it, the outermost if several are, as for an expression (see `SelectedExpression.trimmed`). For
/// an empty one, a type's name there, as hover finds it, in a type or where a declaration declares it (see
/// `Definition.symbolAndNameAt`), or a type variable. None where no type is there, or the module did not type-check.
let internal typeAt (names: TypeNotation) (file: string) (source: string) (result: CompileResult) (selection: SourceSpan) : FoundType option =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed when selection.End > selection.Start ->
        let selection = SelectedExpression.trimmed source selection
        Hover.typeExprsAt selection.Start syntax
        |> List.filter (fun typeExpr -> typeExpr.Span.Start = selection.Start && typeExpr.Span.End = selection.End)
        |> List.tryLast
        |> Option.map (fun typeExpr ->
            let state = { InferState.emptyState with TypeDefs = typed.TypeDefs; ExprTypes = typed.ExprTypes }
            WrittenType(typeExpr, fst (TypeResolution.typeExprToTy state typeExpr)))
    | Some syntax, Some typed ->
        let offset = selection.Start
        let named () =
            match Hover.builtInTypeAt result offset, Definition.symbolAndNameAt file result offset with
            | Some builtIn, _ -> Some (NamedType($"type {SourceSpan.textIn source builtIn.Span}", builtIn.Type, builtIn.Note, None, builtIn.Span))
            | None, Some (symbol, span) when symbol.Kind = TypeDefinition ->
                Definition.typeDefinitionOf typed symbol
                |> Option.map (fun info ->
                    let definition, note = definitionOf names file typed symbol info
                    NamedType($"{keyword info} {symbol.Name}", definition, note, Some symbol, span))
            | _ -> None
        match Hover.typeExprsAt offset syntax with
        | { Kind = TypeVar name } :: _ as around ->
            // The type the variable is written in is the outermost around it.
            let written = TypeExpr.within (List.last around)
            let uses = written |> List.filter (fun part -> part.Kind = TypeVar name)
            let contexts =
                written
                |> List.collect (fun part ->
                    match part.Kind with
                    | TypeWithContext(contexts, _)
                    | TypeContext contexts -> contexts
                    | _ -> [])
                |> List.filter (fun context -> TypeExpr.within context |> List.exists (fun part -> part.Kind = TypeVar name))
            Some (TypeVariable(name, (uses |> List.minBy _.Span.Start).Span, contexts))
        | inner :: _ when Definition.typeNameSpan inner |> Option.exists (Hover.contains offset) -> named ()
        | [] ->
            // On the name a type's declaration declares.
            match named () with
            | Some (NamedType(_, _, _, Some symbol, span)) as found when span = symbol.Declaration -> found
            | _ -> None
        | _ -> None
    | _ -> None
