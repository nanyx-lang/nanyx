/// What the info view shows at a position in the compiled root module: the type of the expression there, the names in
/// scope with their types, and the contexts in scope with where each comes from. Positions are character offsets into
/// the source as the parser saw it, as for `Hover`.
module NyxCompiler.InfoView

open Parser.Program

/// Where a context in scope at a position comes from.
type ContextSource =
    /// The annotation of a def around the position writes it.
    | Signature
    /// A `use` before the position, in a block around it, loads it.
    | Use
    /// A def around the position requires it without writing it: a def that does not write its type, or a
    /// parameterised context that a def's annotation of only contexts leaves out.
    | Inferred
    /// The runtime supplies it, at the top level of the module (see `TypedModule.AmbientContexts`).
    | Runtime

/// A name in scope, with its type as hover writes it, if it has one.
type ScopedName = { Name: string; Type: string option; Kind: SymbolKind; Declaration: SourceSpan }

/// A context in scope, written as hover writes it, and the span of what provides it: the annotation, the `use`, or the
/// name of the def that was inferred to require it. The runtime's has none.
type ScopedContext = { Context: string; Source: ContextSource; Span: SourceSpan }

/// What the info view shows. `Locals` are the locals, parameters and pattern variables in scope, nearest first;
/// `TopLevel` the top-level defs and imports in scope, the module's own first, then by name, less intrinsics.
type Info =
    { Expression: Hover.HoverInfo option
      Locals: ScopedName list
      TopLevel: ScopedName list
      Contexts: ScopedContext list }

/// The value defs whose statements contain `offset`, innermost first, each as its name, its annotation and the span
/// of its statement.
let private defsAround (syntax: Module) (offset: int) : (Identifier * TypeExpr option * SourceSpan) list =
    let local =
        Hover.expressionsAt offset syntax
        |> List.collect (fun expr ->
            match expr.Kind with
            | Block statements ->
                statements
                |> List.choose (fun statement ->
                    match statement.Kind with
                    | DefStatement(_, name, annotation, _) when Hover.contains offset statement.Span -> Some (name, annotation, statement.Span)
                    | _ -> None)
            | _ -> [])
    let rec topLevel (items: TopLevelItem list) =
        items
        |> List.collect (fun item ->
            match item.Kind with
            | Def (ValueDef(_, name, annotation, _)) when Hover.contains offset item.Span -> [ name, annotation, item.Span ]
            | ModuleBlock(_, inner) -> topLevel inner
            | _ -> [])
    local @ topLevel syntax

/// The symbol a def declares: the first value of its name declared in its statement, since a def's name comes before
/// its value.
let private defSymbol (typed: TypedModule) (name: Identifier, _, statement: SourceSpan) : Symbol option =
    typed.Symbols
    |> Map.values
    |> Seq.filter (fun symbol ->
        (symbol.Kind = TopLevelValue || symbol.Kind = LocalValue) && symbol.Name = name && SourceSpan.contains statement symbol.Declaration)
    |> Seq.sortBy (fun symbol -> symbol.Declaration.Start)
    |> Seq.tryHead

/// The values the `use`s around `offset` load, nearest first, each with the span of its `use`: those before `offset`
/// in each block around it, and the `use ... in` whose body it is in.
let private usesAround (syntax: Module) (offset: int) : (Expression * SourceSpan) list =
    Hover.expressionsAt offset syntax
    |> List.collect (fun expr ->
        match expr.Kind with
        | Block statements ->
            statements
            |> List.choose (fun statement ->
                match statement.Kind with
                | UseStatement value when not (SourceSpan.isNone statement.Span) && statement.Span.End <= offset -> Some (value, statement.Span)
                | _ -> None)
            |> List.rev
        | UseIn(value, body) when Hover.contains offset body.Span -> [ value, expr.Span ]
        | _ -> [])

/// The contexts in scope at `offset`, nearest first, as types: those the `use`s around it load, then those each def
/// around it requires, innermost first, or the runtime's where no def is around it.
let private contextsAt (typed: TypedModule) (syntax: Module) (offset: int) : (Ty * ContextSource * SourceSpan) list =
    let defs = defsAround syntax offset |> List.choose (fun def -> defSymbol typed def |> Option.map (fun symbol -> symbol, def))
    let fromUses =
        usesAround syntax offset
        |> List.choose (fun (value, span) -> typed.ExprTypes |> Map.tryFind value.Id |> Option.map (fun ty -> ty, Use, span))
    let fromDefs =
        defs
        |> List.collect (fun (symbol, (_, annotation, _)) ->
            let inferred = typed.InferredContextTypes |> Map.tryFind symbol.Id |> Option.defaultValue []
            typed.ContextTypes
            |> Map.tryFind symbol.Id
            |> Option.defaultValue []
            |> Unifier.writtenContexts
            |> List.map (fun context ->
                match annotation with
                | Some annotation when not (List.contains context inferred) -> context, Signature, annotation.Span
                | _ -> context, Inferred, symbol.Declaration))
    let fromRuntime =
        if defs.IsEmpty then typed.AmbientContexts |> List.map (fun name -> TyApply(name, []), Runtime, SourceSpan.none)
        else []
    fromUses @ fromDefs @ fromRuntime

let private isLocal (symbol: Symbol) =
    match symbol.Kind with
    | LocalValue
    | Parameter
    | PatternVariable -> true
    | TopLevelValue
    | ImportedModule
    | TypeDefinition
    | ContextMember -> false

/// The type of a top-level def or import in scope, after the contexts it requires, as hover writes it.
let private topLevelType (names: TypeNotation) (typed: TypedModule) (symbol: Symbol) : string option =
    match typed.SymbolTypes |> Map.tryFind symbol.Id, typed.ImportedContextTypes |> Map.tryFind symbol.Id with
    | Some ty, _ -> Some (Signatures.typeWithContexts names ty (typed.ContextTypes |> Map.tryFind symbol.Id |> Option.defaultValue []))
    | None, Some (general, contexts) -> Some (Signatures.typeWithContexts names general contexts)
    | None, None -> Completion.symbolType typed symbol |> Option.map (Unifier.tyToDisplayString names)

/// `written` cut into runs of the lengths of `groups`.
let rec private splitAs (groups: 'a list list) (written: string list) : string list list =
    match groups with
    | [] -> []
    | group :: rest ->
        let here, after = List.splitAt group.Length written
        here :: splitAs rest after

/// What the info view shows at `offset` in the compiled root module, compiled as `file` (empty for source compiled
/// directly), with made-up type variables named by `names`. The expression is what hover shows there. The locals' types
/// and the contexts are named together, so an `α` in a parameter's type is the `α` of a context; a local def that
/// writes its contexts shows them before its type. Empty when the module did not type-check.
let at (names: TypeNotation) (file: string) (result: CompileResult) (offset: int) : Info =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        let expression =
            match Definition.typeDefinitionAt names file result offset with
            | Some info -> Some info
            | None -> Hover.typeAt names result offset
        let inScope = Completion.namesInScope result offset
        let locals =
            inScope
            |> List.filter isLocal
            |> List.sortBy (fun symbol ->
                let scope = typed.Scopes.[symbol.Id]
                scope.End - scope.Start, -symbol.Declaration.Start)
        let localTypes =
            locals
            |> List.map (fun symbol ->
                Completion.symbolType typed symbol,
                typed.ContextTypes |> Map.tryFind symbol.Id |> Option.defaultValue [] |> Unifier.writtenContexts)
        let contexts = contextsAt typed syntax offset
        let localGroups = localTypes |> List.map (fun (ty, required) -> Option.toList ty @ required)
        let contextGroups = contexts |> List.map (fun (ty, _, _) -> [ ty ])
        let written = splitAs (localGroups @ contextGroups) (Unifier.tysToDisplayStrings names (List.concat (localGroups @ contextGroups)))
        let localsWritten, contextsWritten = List.splitAt locals.Length written
        let scoped (symbol: Symbol) typeWritten = { Name = symbol.Name; Type = typeWritten; Kind = symbol.Kind; Declaration = symbol.Declaration }
        { Expression = expression
          Locals =
            List.map2
                (fun (symbol, (ty: Ty option, _)) typeWritten ->
                    match ty, typeWritten with
                    | Some _, written :: required -> scoped symbol (Some (Signatures.withContexts required written))
                    | _ -> scoped symbol None)
                (List.zip locals localTypes)
                localsWritten
          TopLevel =
            inScope
            // Names that start with `__` are intrinsics and what syntax desugars to, which no one writes.
            |> List.filter (fun symbol -> (symbol.Kind = TopLevelValue || symbol.Kind = ImportedModule) && not (symbol.Name.StartsWith "__"))
            |> List.sortBy (fun symbol -> symbol.Declaration.File <> file, symbol.Name)
            |> List.map (fun symbol -> scoped symbol (topLevelType names typed symbol))
          Contexts =
            List.map2 (fun (_, source, span) contextWritten -> { Context = List.exactlyOne contextWritten; Source = source; Span = span }) contexts contextsWritten }
    | _ -> { Expression = None; Locals = []; TopLevel = []; Contexts = [] }
