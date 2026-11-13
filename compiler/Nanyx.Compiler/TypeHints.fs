/// The inferred types an editor shows for the declarations that do not write theirs, as F# tooling does: a lens
/// above each top-level def with its whole type, and an inlay hint after each local, parameter and pattern variable
/// (or inlay hints for the top-level defs too, for an editor set up to show no lenses).
/// Positions are character offsets into the source as the parser saw it, with line endings normalised to `\n`, as
/// for `Hover`.
module NyxCompiler.TypeHints

open Parser.Program

/// The inferred type of a declaration, the span of the name it declares and what kind of declaration it is, so an
/// editor can show some kinds and not others. `Context` is the span of an annotation the declaration writes that
/// names only its contexts (`def f: [Console] = ...`): the type is the rest of that annotation, so it is shown after it
/// rather than after the name. `Annotation` is the type as source writes it, in ASCII, for an editor to insert where the
/// hint is shown: `int` after `: ` (or after the contexts), or a function bracketed after a lambda parameter's name,
/// `(a -> b)`. It is absent where no annotation can be written (a pattern variable, a name a destructuring def binds) or
/// the type has a part that failed to check.
type TypeHint =
    { Name: string
      Type: string
      Span: SourceSpan
      Kind: SymbolKind
      Context: SourceSpan option
      Annotation: string option }

/// Whether an annotation names only contexts, `[Console]`.
let private isContextOnly (typeExpr: TypeExpr) =
    match typeExpr.Kind with
    | TypeContext _ -> not (SourceSpan.isNone typeExpr.Span)
    | _ -> false

/// Each def in `syntax` whose annotation names only contexts, as the span of the def and of the annotation.
let private contextOnlyAnnotations (syntax: Module) : (SourceSpan * SourceSpan) list =
    let topLevel =
        syntax
        |> List.choose (fun item ->
            match item.Kind with
            | Def (ValueDef(_, _, Some annotation, _)) when isContextOnly annotation -> Some (item.Span, annotation.Span)
            | _ -> None)
    let local =
        Hover.allExpressions syntax
        |> List.collect (fun expr ->
            match expr.Kind with
            | Block statements ->
                statements
                |> List.choose (fun statement ->
                    match statement.Kind with
                    | DefStatement(_, _, Some annotation, _) when isContextOnly annotation -> Some (statement.Span, annotation.Span)
                    | _ -> None)
            | _ -> [])
    topLevel @ local

/// The context-only annotation `declaration`'s def writes after its name, if it writes one.
let private contextOf (annotations: (SourceSpan * SourceSpan) list) (declaration: SourceSpan) : SourceSpan option =
    annotations
    |> List.filter (fun (def, annotation) -> SourceSpan.contains def declaration && declaration.End <= annotation.Start)
    // A def inside another starts after the other's annotation, so only its own is left; the narrowest def wins anyway.
    |> List.sortBy (fun (def, _) -> def.End - def.Start)
    |> List.tryHead
    |> Option.map snd

/// Every value def in `syntax`, a `module name =` block's and a block's among them, as its span, its name and its
/// annotation. The defs a destructuring def stands for share its span.
let private defsOf (syntax: Module) : (SourceSpan * string * TypeExpr option) list =
    let rec topLevel (items: TopLevelItem list) =
        items
        |> List.collect (fun item ->
            match item.Kind with
            | Def (ValueDef(_, name, annotation, _)) -> [ item.Span, name, annotation ]
            | ModuleBlock(_, items) -> topLevel items
            | _ -> [])
    let local =
        Hover.allExpressions syntax
        |> List.collect (fun expr ->
            match expr.Kind with
            | Block statements ->
                statements
                |> List.choose (fun statement ->
                    match statement.Kind with
                    | DefStatement(_, name, annotation, _) -> Some (statement.Span, name, annotation)
                    | _ -> None)
            | _ -> [])
    topLevel syntax @ local

/// The narrowest of `spanned` whose span contains `declaration`.
let private narrowest (span: 'T -> SourceSpan) (declaration: SourceSpan) (spanned: 'T list) : 'T option =
    spanned
    |> List.filter (fun item -> SourceSpan.contains (span item) declaration)
    |> List.sortBy (fun item -> (span item).End - (span item).Start)
    |> List.tryHead

/// Whether source can write an annotation after the name `symbol` declares: the name of a def that writes no type, or
/// only its contexts, but not one of the names a destructuring def binds; or a lambda parameter that writes no type.
/// A pattern variable can't be annotated.
let private canAnnotate
    (defs: (SourceSpan * string * TypeExpr option) list)
    (lambdas: (SourceSpan * (string * TypeExpr option) list) list)
    (symbol: Symbol)
    =
    match symbol.Kind with
    | TopLevelValue
    | LocalValue ->
        match defs |> narrowest (fun (span, _, _) -> span) symbol.Declaration with
        | Some (span, name, annotation) ->
            name = symbol.Name
            && (defs |> List.filter (fun (other, _, _) -> other = span) |> List.length) = 1
            && (annotation |> Option.forall isContextOnly)
        | None -> false
    | Parameter ->
        match lambdas |> narrowest fst symbol.Declaration with
        | Some (_, parameters) -> parameters |> List.contains (symbol.Name, None)
        | None -> false
    | _ -> false

/// Whether `ty` has a part that failed to check, which source can't write.
let rec private mentionsError (ty: Ty) =
    Unifier.isError ty
    || match ty with
       | TyVar _
       | TyPrimitive _
       | TyNominal _ -> false
       | TyAlias(_, args, _)
       | TyApply(_, args) -> List.exists mentionsError args
       | TyFunc(arg, ret, contexts) -> List.exists mentionsError (arg :: ret :: contexts)
       | TyRecord fields
       | TyOpenRecord(fields, _) -> fields |> Map.exists (fun _ field -> mentionsError field)
       | TyTag(_, payload) -> payload |> Option.exists mentionsError
       | TyUnion items -> List.exists mentionsError items

/// The symbols the root module compiled as `file` declares itself, not those of the modules it imports, in source order.
let private ownSymbols (file: string) (typed: TypedModule) : Symbol list =
    typed.Symbols
    |> Map.values
    |> Seq.filter (fun symbol -> symbol.Declaration.File = file && not (SourceSpan.isNone symbol.Declaration))
    |> Seq.sortBy (fun symbol -> symbol.Declaration.Start)
    |> List.ofSeq

/// The hints for the root module, as top-level defs (for lenses) and everything else (for inlay hints), each in
/// source order.
///
/// The declarations are taken a top-level def at a time, from its name to the next top-level def or expression, and
/// the types in each are named together with the def's own type, so a parameter's `α` is the `α` of the def's
/// signature. An annotated def is named with the rest but gets no lens.
let private hints (names: TypeNotation) (file: string) (result: CompileResult) : TypeHint list * TypeHint list =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        let own = ownSymbols file typed
        // Where each run of declarations starts, and the top-level def it is named with, if any.
        let isInferred (symbol: Symbol) =
            typed.SymbolTypes.ContainsKey symbol.Id && not (typed.AnnotatedSymbols.Contains symbol.Id)
        let starts =
            let defs =
                own
                |> List.filter (fun symbol -> symbol.Kind = TopLevelValue)
                |> List.map (fun symbol -> symbol.Declaration.Start, Some symbol)
            let expressions =
                syntax
                |> List.choose (function
                    | Kind (Expr value) when not (SourceSpan.isNone value.Span) -> Some (value.Span.Start, None)
                    | _ -> None)
            (System.Int32.MinValue, None) :: defs @ expressions |> List.sortBy fst
        let runs =
            starts
            |> List.mapi (fun index (start, def) ->
                let finish = if index + 1 < starts.Length then fst starts.[index + 1] else System.Int32.MaxValue
                let members =
                    own
                    |> List.filter (fun symbol ->
                        symbol.Kind <> TopLevelValue
                        && start <= symbol.Declaration.Start
                        && symbol.Declaration.Start < finish
                        && isInferred symbol)
                def, members)
        let annotations = contextOnlyAnnotations syntax
        let defs = defsOf syntax
        let lambdas =
            Hover.allExpressions syntax
            |> List.choose (fun expr ->
                match expr.Kind with
                | Lambda(parameters, _, _) -> Some (expr.Span, parameters)
                | _ -> None)
        let lensesAndHints =
            runs
            |> List.map (fun (def, members) ->
                let memberTypes = members |> List.map (fun symbol -> typed.SymbolTypes.[symbol.Id])
                let hint (symbol: Symbol, ty: Ty) (written: string, source: string) =
                    // A lambda parameter's annotation ends at an arrow, so a function there is bracketed.
                    let annotation =
                        match ty with
                        | TyFunc _ when symbol.Kind = Parameter -> $"({source})"
                        | _ -> source
                    { Name = symbol.Name
                      Type = written
                      Span = symbol.Declaration
                      Kind = symbol.Kind
                      Context = contextOf annotations symbol.Declaration
                      Annotation = if canAnnotate defs lambdas symbol && not (mentionsError ty) then Some annotation else None }
                // The types written for a reader in `names`, or as source writes them.
                let writtenIn notation =
                    match def |> Option.bind (fun symbol -> typed.SymbolTypes |> Map.tryFind symbol.Id |> Option.map (fun ty -> symbol, ty)) with
                    | Some (symbol, defType) ->
                        // The def's type is written after the contexts it requires, as hover writes it, except that a
                        // def that writes its contexts has its type shown after them, so only the parameterised ones it
                        // was inferred to need are written.
                        let contexts =
                            let required =
                                if (contextOf annotations symbol.Declaration).IsSome then typed.InferredContextTypes
                                else typed.ContextTypes
                            required |> Map.tryFind symbol.Id |> Option.defaultValue []
                        let defWritten, membersWritten = Signatures.typesWithContexts notation defType contexts memberTypes
                        (if isInferred symbol then [ (symbol, defType), defWritten ] else []), membersWritten
                    | None -> [], Unifier.tysToDisplayStrings notation memberTypes
                let lens, membersWritten = writtenIn names
                let lensSource, membersSource = writtenIn Source
                List.map2 (fun (typed, written) (_, source) -> hint typed (written, source)) lens lensSource,
                List.map2 hint (List.zip members memberTypes) (List.zip membersWritten membersSource))
        List.collect fst lensesAndHints, List.collect snd lensesAndHints
    | _ -> [], []

/// A lens for each top-level def in the compiled root module that does not write its type, in source order, with
/// made-up type variables named by `names`. A destructuring def gets one for each name it declares. `file` is the
/// file the root module was compiled as (empty for source compiled directly), which tells its own declarations from
/// those of the modules it imports. Empty when the module did not type-check.
let typeLenses (names: TypeNotation) (file: string) (result: CompileResult) : TypeHint list =
    fst (hints names file result)

/// The hints of `typeLenses`, and one for each local def, lambda parameter and pattern variable that does not write
/// its type, together in source order, for an editor that shows a top-level def's type after its name, as an inlay
/// hint, rather than in a lens above it.
let inlayHintsWithTopLevel (names: TypeNotation) (file: string) (result: CompileResult) : TypeHint list =
    let lenses, others = hints names file result
    lenses @ others |> List.sortBy (fun hint -> hint.Span.Start)

/// The type of each top-level def in the compiled root module, whether it writes its type or not, in source order,
/// after the contexts it requires, as a hover on its name shows it, with made-up type variables named by `names`, as
/// `nanyx check` prints them. `file` is as for `typeLenses`. Empty when the module did not type-check.
let topLevelTypes (names: TypeNotation) (file: string) (result: CompileResult) : TypeHint list =
    match result.Typed with
    | Some typed ->
        ownSymbols file typed
        |> List.filter (fun symbol -> symbol.Kind = TopLevelValue)
        |> List.choose (fun symbol ->
            typed.SymbolTypes
            |> Map.tryFind symbol.Id
            |> Option.map (fun ty ->
                let contexts = typed.ContextTypes |> Map.tryFind symbol.Id |> Option.defaultValue []
                { Name = symbol.Name
                  Type = Signatures.typeWithContexts names ty contexts
                  Span = symbol.Declaration
                  Kind = symbol.Kind
                  Context = None
                  Annotation = None }))
    | None -> []
