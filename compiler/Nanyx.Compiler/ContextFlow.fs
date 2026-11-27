/// How contexts flow through a def, for the info view: each `use` in the def around a position, each context the def's
/// signature writes or it was inferred to require, and each instance the compiler builds for a call in it, with the
/// calls each one serves; and what the editor
/// highlights at a position: on a `use` or a context of a signature, the calls it serves, and on a call, what provides
/// its contexts. Read from what the compiler recorded for each call (see `InfoView.callContextsOf`).
module NyxCompiler.ContextFlow

open Parser.Program

/// A call a provider serves: the name it calls, and its span, the name's where it has one.
type ServedCall = { Callee: string; Span: SourceSpan }

/// A provider of contexts in a def: a `use`, linked by the value it loads, a context the def's signature writes,
/// linked by that context, or one the def was inferred to require, linked by the def's name, each `Provided`; or an
/// instance the compiler builds, from attached definitions, derived from the type's parts or its own, which has no
/// link. `Context` is written as hover writes it, and `Name` is its name as `Context` starts with it; `Calls` are the
/// calls it serves, in source order. A `use` that serves none is unused.
type FlowEntry = { Context: string; Name: string; Source: InfoView.CallSource; Span: SourceSpan; Calls: ServedCall list }

/// The flow of contexts through the innermost def around a position: the def's name, and its providers, those of its
/// signature first, then those it was inferred to require, then its `use`s in source order, then the instances the
/// compiler builds, in the order of the first call each serves.
type Flow = { Def: string; Entries: FlowEntry list }

/// What the editor highlights at a position: the spans of the providers, and those of the calls they serve.
type Highlights = { Providers: SourceSpan list; Calls: SourceSpan list }

/// What the info view shows of contexts at a position: the flow through the def around it, if any, and what the editor
/// highlights there.
type View = { Flow: Flow option; Highlights: Highlights }

/// The name `call` calls, and its span, the name's where it has one.
let private served (call: Expression) : ServedCall =
    match call.Kind with
    | FunctionCall(name, nameSpan, _)
    | Pipe(_, name, nameSpan, _) -> { Callee = name; Span = nameSpan |> Option.defaultValue call.Span }
    | _ -> { Callee = ""; Span = call.Span }

/// The `use`s within `expressions`, in source order, each as the value it loads and the span of the `use`.
let private usesIn (expressions: Expression list) : (Expression * SourceSpan) list =
    expressions
    |> List.collect (fun expr ->
        match expr.Kind with
        | Block statements ->
            statements
            |> List.choose (fun statement ->
                match statement.Kind with
                | UseStatement value when not (SourceSpan.isNone statement.Span) -> Some (value, statement.Span)
                | _ -> None)
        | UseIn(value, _) -> [ value, expr.Span ]
        | _ -> [])
    |> List.sortBy (fun (_, span) -> span.Start)

/// The contexts the def `symbol`, written with `annotation`, provides to the calls in it, each as its type, its source
/// and the span that links it: a context its signature writes, by that context's span where each is written as one,
/// and one it was inferred to require, by the def's name.
let private defContexts (typed: TypedModule) (symbol: Symbol) (annotation: TypeExpr option) : (Ty * InfoView.ContextSource * SourceSpan) list =
    let inferred = typed.InferredContextTypes |> Map.tryFind symbol.Id |> Option.defaultValue []
    let all = typed.ContextTypes |> Map.tryFind symbol.Id |> Option.defaultValue [] |> Unifier.writtenContexts
    let written, inferredHere =
        match annotation with
        | Some _ -> all |> List.partition (fun context -> not (List.contains context inferred))
        | None -> [], all
    let writtenSpans =
        match annotation with
        | Some { Kind = TypeWithContext(contexts, _) } when contexts.Length = written.Length -> contexts |> List.map _.Span
        | Some annotation -> written |> List.map (fun _ -> annotation.Span)
        | None -> []
    (List.map2 (fun context span -> context, InfoView.Signature, span) written writtenSpans)
    @ (inferredHere |> List.map (fun context -> context, InfoView.Inferred, symbol.Declaration))

/// The innermost def around `offset` with a symbol, its name and the span of its statement, and each call in it with
/// the contexts it requires that a signature, a `use` or inference provides, by the span that links the provider, and
/// those it finds in an instance the compiler builds. Only the def's own value is searched for them.
let private callsAround (typed: TypedModule) (syntax: Module) (offset: int) =
    InfoView.defsAround syntax offset
    |> List.tryPick (fun def -> InfoView.defSymbol typed def |> Option.map (fun symbol -> symbol, def))
    |> Option.map (fun (symbol, (name, annotation, statement, value)) ->
        let within = Hover.expressionsWithin value |> List.filter (fun expr -> SourceSpan.contains statement expr.Span)
        let calls =
            within
            |> List.filter InfoView.isCall
            |> List.sortBy _.Span.Start
            |> List.map (fun call ->
                let found = InfoView.callContextsOf typed syntax (InfoView.expressionById syntax) call
                let providers =
                    found
                    |> List.choose (fun (context, source, span, _) ->
                        match source with
                        | InfoView.Provided (InfoView.Signature | InfoView.Use | InfoView.Inferred as source) when not (SourceSpan.isNone span) ->
                            Some (context, source, span)
                        | _ -> None)
                let built =
                    found
                    |> List.choose (fun (context, source, _, _) ->
                        match source with
                        | InfoView.Attached _
                        | InfoView.Derived
                        | InfoView.BuiltIn -> Some (context, source)
                        | InfoView.Provided _ -> None)
                call, providers, built)
        symbol, name, annotation, usesIn within, calls)

/// Whether a provider that the call found as `source` at `span` for `context` is the def's own `entry`: the same span,
/// and, for one inferred, which links every one by the def's name, the same context.
let private serves (entry: Ty * InfoView.ContextSource * SourceSpan) (context: Ty, source: InfoView.ContextSource, span: SourceSpan) =
    let entryContext, entrySource, entrySpan = entry
    entrySource = source && entrySpan = span
    && (source <> InfoView.Inferred || FunctionContexts.contextName entryContext = FunctionContexts.contextName context)

/// The providers of the innermost def around `offset`, each with the calls it serves, and the span a position must be in
/// to highlight them: the whole `use`, or the provider's own span. An instance the compiler builds has neither, and is
/// one entry for each context and source, whichever calls it serves.
let private entriesAt (typed: TypedModule) (syntax: Module) (offset: int) =
    callsAround typed syntax offset
    |> Option.map (fun (symbol, name, annotation, uses, calls) ->
        let ofDef = defContexts typed symbol annotation |> List.map (fun (context, source, span) -> (context, source, span), span)
        let ofUses =
            uses
            |> List.choose (fun (value, useSpan) ->
                typed.ExprTypes |> Map.tryFind value.Id |> Option.map (fun ty -> (ty, InfoView.Use, value.Span), useSpan))
        let provided =
            ofDef @ ofUses
            |> List.map (fun ((context, source, span) as entry, trigger) ->
                let servedCalls = calls |> List.filter (fun (_, providers, _) -> providers |> List.exists (serves entry)) |> List.map (fun (call, _, _) -> call)
                (context, InfoView.Provided source, span), trigger, servedCalls)
        let built =
            calls
            |> List.collect (fun (call, _, built) -> built |> List.map (fun instance -> instance, call))
            |> List.groupBy fst
            |> List.map (fun ((context, source), served) -> (context, source, SourceSpan.none), SourceSpan.none, served |> List.map snd |> List.distinctBy _.Id)
        symbol, name, provided @ built, calls |> List.map (fun (call, providers, _) -> call, providers))


/// The flow of contexts through the def `entriesAt` found, `symbol`, with made-up type variables named by `names`, and
/// the contexts named by `rename` (see `Signatures.relativeAt`). The contexts are written as hover writes them in the
/// def's type, named together with it, so an associated type the def's type uses is bound by name: `Num(a, fraction = b)`.
let private flowOf (typed: TypedModule) (names: TypeNotation) (rename: Ty -> Ty) (symbol: Symbol, name: string, entries, _) : Flow =
    let defType = typed.SymbolTypes |> Map.tryFind symbol.Id |> Option.map rename |> Option.toList
    let written =
        Unifier.tysToDisplayStringsIn typed.TypeDefs names (defType @ (entries |> List.map (fun ((context, _, _), _, _) -> rename context)))
        |> List.skip defType.Length
    { Def = name
      Entries =
        List.map2
            (fun ((context, source, span), _, calls) contextWritten ->
                { Context = contextWritten
                  Name = FunctionContexts.contextName (rename context) |> Option.defaultValue ""
                  Source = source
                  Span = span
                  Calls = calls |> List.map served })
            entries
            written }

/// What the editor highlights at `offset` in the def `entriesAt` found there: on a `use` or a context of the def's
/// signature, that provider and the calls it serves; else, on the innermost call around it that one of the def's
/// providers serves, the call and those providers. Empty elsewhere.
let private highlightsOf (syntax: Module) (offset: int) (_, _, entries, calls) : Highlights =
    match entries |> List.tryFind (fun (_, trigger, _) -> Hover.contains offset trigger) with
    | Some ((_, _, span), _, served') -> { Providers = [ span ]; Calls = served' |> List.map (fun call -> (served call).Span) }
    | None ->
        let atCall =
            Hover.expressionsAt offset syntax
            |> List.filter InfoView.isCall
            |> List.tryPick (fun call ->
                calls
                |> List.tryFind (fun (found: Expression, providers: _ list) -> found.Id = call.Id && not providers.IsEmpty))
        match atCall with
        | Some (call, providers) ->
            { Providers = providers |> List.map (fun (_, _, span) -> span) |> List.distinct; Calls = [ (served call).Span ] }
        | None -> { Providers = []; Calls = [] }

/// The flow of contexts through the innermost def around `offset` in the compiled root module, with made-up type
/// variables named by `names` and contexts named as the code at `offset` can name them (see `Signatures.relativeAt`),
/// and what the editor highlights at `offset`, from one search of the def. No flow and
/// nothing highlighted outside any def, or when the module did not type-check.
let at (names: TypeNotation) (result: CompileResult) (offset: int) : View =
    let none = { Flow = None; Highlights = { Providers = []; Calls = [] } }
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        match entriesAt typed syntax offset with
        | Some found -> { Flow = Some (flowOf typed names (Signatures.relativeAt typed.TypeDefs syntax offset) found); Highlights = highlightsOf syntax offset found }
        | None -> none
    | _ -> none