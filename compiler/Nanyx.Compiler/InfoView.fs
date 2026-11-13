/// What the info view shows at a position in the compiled root module: the type of the expression there; each scope
/// around it, from the innermost lambda out to the module, with the names it declares and the contexts it provides;
/// and the contexts the call there requires with where it finds each. Positions are character offsets into
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

/// A name in scope, with its type as hover writes it, if it has one. `Symbol` is what it names, whose doc comment the
/// info view shows (for an import, its module's).
type ScopedName = { Name: string; Type: string option; Kind: SymbolKind; Declaration: SourceSpan; Symbol: Symbol }

/// A context in scope, written as hover writes it, and the span of what provides it: the annotation, the `use`, or the
/// name of the def that was inferred to require it. The runtime's has none. `Documented` are the symbols whose doc
/// comment describes it, the first that has one first: for a `use`, the def it names or calls, then the context itself.
type ScopedContext = { Context: string; Source: ContextSource; Span: SourceSpan; Documented: Symbol list }

/// Where a call finds a context its callee requires (see `TypedModule.ContextInstances` and
/// `TypedModule.ContextProviders`).
type CallSource =
    /// What provides it where the call is: a def's signature, a `use`, inference, or the runtime.
    | Provided of ContextSource
    /// An instance the compiler builds from the definitions attached to the type, named as written: `Point.show`.
    | Attached of string list
    /// An instance the compiler derives from the parts of the type (see `StructuralInstances`).
    | Derived
    /// The compiler's own instance, of a built-in type.
    | BuiltIn

/// A context the call at the position requires, written as hover writes it, with where the call finds it, and the span
/// and documented symbols of what provides it, as `ScopedContext` has them. An instance the compiler builds has no span.
type CallContext = { Context: string; Source: CallSource; Span: SourceSpan; Documented: Symbol list }

/// What kind of scope around the position a `Scope` is.
type ScopeKind =
    /// A lambda that is not a def's value, nor a `memory` block, which is part of the scope it is in.
    | LambdaScope
    /// A def, top-level or local, with the lambda that is its value if it is one.
    | FunctionScope
    /// The module.
    | ModuleScope

/// A scope around the position. `Heading` names it: a lambda by its parameters (`{ message -> … }`), a def by its name
/// (`def greet`), the module by its declared name (`module library.lending`). `Span` is what the heading links to: the
/// lambda, the def's name, or the module's declaration (none if it has none). `Type` is a lambda's type, with the
/// contexts it carries, or a def's signature, as hover writes them, and `Declared` says whether the def writes its type;
/// `Symbol` is the def's, whose doc comment the info view shows. `Names` are the parameters, locals and pattern variables it declares, nearest the position first, and for
/// the module its top-level defs and imports, its own first, then by name, less intrinsics. `Contexts` are those it
/// provides: a `use` in it, a def's signature or what a def was inferred to require, or the module's runtime.
type Scope =
    { Kind: ScopeKind
      Heading: string
      Span: SourceSpan
      Type: string option
      Declared: bool
      Symbol: Symbol option
      Names: ScopedName list
      Contexts: ScopedContext list }

/// What the info view shows. `Expression` is what hover shows at the position, and `ExpressionSymbol` the name there
/// that hover shows the doc comment of; `Instantiation`, on a name that refers to a generic def, the def's own
/// signature and how this use fills in its type variables. `Scopes` are the scopes around the position, innermost
/// first and the module last, each with what it declares and the contexts it provides. `Call` holds the contexts the
/// innermost call or pipe around the position that requires any requires, each with where it finds it.
type Info =
    { Expression: Hover.HoverInfo option
      ExpressionSymbol: Symbol option
      Instantiation: Hover.Instantiation option
      Scopes: Scope list
      Call: CallContext list }

/// The value defs whose statements contain `offset`, innermost first, each as its name, its annotation, the span of its
/// statement and its value.
let internal defsAround (syntax: Module) (offset: int) : (Identifier * TypeExpr option * SourceSpan * Expression) list =
    let local =
        Hover.expressionsAt offset syntax
        |> List.collect (fun expr ->
            match expr.Kind with
            | Block statements ->
                statements
                |> List.choose (fun statement ->
                    match statement.Kind with
                    | DefStatement(_, name, annotation, value) when Hover.contains offset statement.Span ->
                        Some (name, annotation, statement.Span, value)
                    | _ -> None)
            | _ -> [])
    let rec topLevel (items: TopLevelItem list) =
        items
        |> List.collect (fun item ->
            match item.Kind with
            | Def (ValueDef(_, name, annotation, value)) when Hover.contains offset item.Span -> [ name, annotation, item.Span, value ]
            | ModuleBlock(_, inner) -> topLevel inner
            | _ -> [])
    local @ topLevel syntax

/// The symbol a def declares: the first value of its name declared in its statement, since a def's name comes before
/// its value.
let internal defSymbol (typed: TypedModule) (name: Identifier, _, statement: SourceSpan, _) : Symbol option =
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

/// The symbol `expr` refers to, if it is a name or a call of one.
let internal referenced (typed: TypedModule) (expr: Expression) : Symbol option =
    typed.References |> Map.tryFind expr.Id |> Option.bind (fun id -> typed.Symbols |> Map.tryFind id)

/// The symbol of the context `context` is, whose doc comment describes it.
let private contextSymbol (typed: TypedModule) (context: Ty) : Symbol option =
    match context with
    | TyApply(name, _)
    | TyAlias(name, _, _) ->
        typed.TypeDefs
        |> Map.tryFind name
        |> Option.filter (fun info -> info.IsContext)
        |> Option.bind (fun info -> info.Symbol)
        |> Option.bind (fun id -> typed.Symbols |> Map.tryFind id)
    | _ -> None

/// The contexts in scope at `offset`, nearest first, as types, each with the span of the scope it belongs to: those the
/// `use`s around it load, each with the def the `use` names or calls, belonging where the `use` is; then those each def
/// around it requires, innermost first, belonging to the def's value; then the runtime's, which belong to the module.
let private contextsAt (typed: TypedModule) (syntax: Module) (offset: int) : (Ty * ContextSource * SourceSpan * Symbol option * SourceSpan) list =
    let defs = defsAround syntax offset |> List.choose (fun def -> defSymbol typed def |> Option.map (fun symbol -> symbol, def))
    let fromUses =
        usesAround syntax offset
        |> List.choose (fun (value, span) ->
            typed.ExprTypes |> Map.tryFind value.Id |> Option.map (fun ty -> ty, Use, span, referenced typed value, span))
    let fromDefs =
        defs
        |> List.collect (fun (symbol, (_, annotation, _, value)) ->
            let inferred = typed.InferredContextTypes |> Map.tryFind symbol.Id |> Option.defaultValue []
            typed.ContextTypes
            |> Map.tryFind symbol.Id
            |> Option.defaultValue []
            |> Unifier.writtenContexts
            |> List.map (fun context ->
                match annotation with
                | Some annotation when not (List.contains context inferred) -> context, Signature, annotation.Span, None, value.Span
                | _ -> context, Inferred, symbol.Declaration, None, value.Span))
    let fromRuntime =
        typed.AmbientContexts |> List.map (fun name -> TyApply(name, []), Runtime, SourceSpan.none, None, SourceSpan.none)
    fromUses @ fromDefs @ fromRuntime

/// The lambdas around `offset` that are scopes of their own, innermost first: not a def's value, which is the def's
/// scope, nor a `memory` block, which is part of the scope it is in. A lambda the parser made has no span, and is left
/// out.
let private lambdasAround (syntax: Module) (offset: int) : Expression list =
    let around = Hover.expressionsAt offset syntax
    let defValues = defsAround syntax offset |> List.map (fun (_, _, _, value) -> value.Id) |> Set.ofList
    let memoryBlocks =
        around
        |> List.choose (fun expr ->
            match expr.Kind with
            | FunctionCall(name, _, [ block ]) when name = MemoryCells.memoryFunction -> Some block.Id
            | _ -> None)
        |> Set.ofList
    around
    |> List.filter (fun expr ->
        match expr.Kind with
        | Lambda _ -> not (SourceSpan.isNone expr.Span) && not (defValues.Contains expr.Id) && not (memoryBlocks.Contains expr.Id)
        | _ -> false)

/// The heading of the scope of `lambda`: its parameters, `{ message -> … }`, or `{ … }` if it has none.
let private lambdaHeading (lambda: Expression) : string =
    match lambda.Kind with
    | Lambda(parameters, _, _) when not parameters.IsEmpty -> $"""{{ {parameters |> List.map fst |> String.concat ", "} -> … }}"""
    | _ -> "{ … }"

/// The name of the context `context` is.
let internal contextName (context: Ty) : Identifier option =
    match context with
    | TyApply(name, _)
    | TyAlias(name, _, _) -> Some name
    | _ -> None

/// Where an instance of `ctxName` the compiler builds from `members` (each member's name and the definition it is)
/// comes from: the compiler's own, a definition derived from the type's parts, or definitions attached to the type,
/// which are named `Type.member`.
let private builtSource (ctxName: Identifier) (members: (string * string) list) : CallSource =
    let definitions = members |> List.map snd
    let isCompilersOwn (definition: string) =
        definition = InterpolationHoles.builtInFunction
        || WellKnownContexts.tryFind ctxName |> Option.exists (fun wellKnown -> wellKnown.BuiltInFunction = definition)
    if definitions |> List.forall isCompilersOwn then BuiltIn
    elif definitions |> List.forall (fun definition -> definition.Contains '.') then Attached definitions
    else Derived

/// The contexts `symbol`, called at `call`, requires, as its type declares them, at the types of the call.
let private contextsAtCall (typed: TypedModule) (symbol: Symbol) (call: Expression) : Ty list =
    match Hover.declaredType typed symbol, typed.CalleeTypes |> Map.tryFind call.Id with
    | Some (general, contexts), Some calleeTy ->
        match Unifier.unify [ general, calleeTy, Equal, SourceSpan.none ] with
        | Ok subst -> contexts |> List.map (Unifier.apply subst)
        | Error _ -> []
    | _ -> []

/// Whether `expr` is a call or a pipe, which may require contexts.
let internal isCall (expr: Expression) =
    match expr.Kind with
    | FunctionCall _
    | Pipe _ -> true
    | _ -> false

/// The contexts `call`, a call or pipe in `syntax`, requires, each with where it finds it, the span of what provides it
/// and the symbol of the def a `use` of it names: first those its callee's type declares, in order, at the types of the
/// call (see `TypedModule.ContextInstances`), then the others `TypedModule.ContextProviders` records for it, a member's
/// context or one a function value it runs requires. `valueAt` finds the value a `use` loads by its node.
let internal callContextsOf
    (typed: TypedModule)
    (syntax: Module)
    (valueAt: NodeId -> Expression option)
    (call: Expression)
    : (Ty * CallSource * SourceSpan * Symbol option) list =
    let provided (context: Ty) (provider: ContextProvider) =
        match provider with
        | FromSignature span -> context, Provided Signature, span, None
        | FromUse value ->
            match valueAt value with
            | Some value -> context, Provided Use, value.Span, referenced typed value
            | None -> context, Provided Use, SourceSpan.none, None
        | FromInference ->
            let def = defsAround syntax call.Span.Start |> List.tryPick (defSymbol typed)
            context, Provided Inferred, def |> Option.map _.Declaration |> Option.defaultValue SourceSpan.none, None
        | FromRuntime -> context, Provided Runtime, SourceSpan.none, None
    let providers = typed.ContextProviders |> Map.tryFind call.Id |> Option.defaultValue []
    let instances = typed.ContextInstances |> Map.tryFind call.Id |> Option.defaultValue []
    let declared =
        referenced typed call
        |> Option.map (fun symbol -> contextsAtCall typed symbol call)
        |> Option.defaultValue []
        |> List.mapi (fun index context ->
            match List.tryItem index instances |> Option.flatten, contextName context with
            | Some (InstanceInScope(_, provider)), _ -> Some (provided context provider)
            | Some (InstanceSynthesized members), Some ctxName -> Some (context, builtSource ctxName members, SourceSpan.none, None)
            | Some (InstanceDerived _), _ -> Some (context, Derived, SourceSpan.none, None)
            | Some InstanceBuiltIn, _ -> Some (context, BuiltIn, SourceSpan.none, None)
            | None, Some ctxName ->
                providers
                |> List.tryFind (fun p -> contextName p.Context = Some ctxName)
                |> Option.map (fun p -> provided context p.Provider)
            | _ -> None)
        |> List.choose id
    let covered = declared |> List.choose (fun (context, _, _, _) -> contextName context) |> Set.ofList
    let others =
        providers
        |> List.filter (fun p -> contextName p.Context |> Option.forall (covered.Contains >> not))
        |> List.map (fun p -> provided p.Context p.Provider)
    declared @ others

let private expressionsByIdOf =
    System.Runtime.CompilerServices.ConditionalWeakTable<Module, System.Collections.Generic.IReadOnlyDictionary<NodeId, Expression>>()

/// The expression of `syntax` at the node `node`, if any, to find a value a `use` loads by (see `callContextsOf`). The
/// table of every expression by its node is built once for each compile, when first asked for, since the info view asks
/// on every move of the cursor.
let internal expressionById (syntax: Module) (node: NodeId) : Expression option =
    let byId =
        expressionsByIdOf.GetValue(
            syntax,
            fun syntax ->
                let byId = System.Collections.Generic.Dictionary<NodeId, Expression>()
                for expr in Hover.allExpressions syntax do
                    byId[expr.Id] <- expr
                byId)
    match byId.TryGetValue node with
    | true, expr -> Some expr
    | false, _ -> None

/// The contexts the innermost call or pipe around `offset` that requires any requires (see `callContextsOf`).
let private callContextsAt (typed: TypedModule) (syntax: Module) (offset: int) : (Ty * CallSource * SourceSpan * Symbol option) list =
    Hover.expressionsAt offset syntax
    |> List.filter isCall
    |> List.tryPick (fun call ->
        match callContextsOf typed syntax (expressionById syntax) call with
        | [] -> None
        | found -> Some found)
    |> Option.defaultValue []

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
    Hover.declaredType typed symbol |> Option.map (fun (ty, contexts) -> Signatures.typeWithContexts names ty contexts)

/// `written` cut into runs of the lengths of `groups`.
let rec private splitAs (groups: 'a list list) (written: string list) : string list list =
    match groups with
    | [] -> []
    | group :: rest ->
        let here, after = List.splitAt group.Length written
        here :: splitAs rest after

/// The module's scope, with no names or contexts yet: headed by the name its `module` declaration gives it, if any.
let private moduleScope (syntax: Module) : Scope =
    let declared = syntax |> List.tryPick (fun item -> match item.Kind with ModuleDecl name -> Some (name, item.Span) | _ -> None)
    { Kind = ModuleScope
      Heading = match declared with Some (name, _) -> $"module {name}" | None -> "this module"
      Span = declared |> Option.map snd |> Option.defaultValue SourceSpan.none
      Type = None
      Declared = false
      Symbol = None
      Names = []
      Contexts = [] }

/// What the info view shows at `offset` in the compiled root module, compiled as `file` (empty for source compiled
/// directly), with made-up type variables named by `names`. The expression is what hover shows there. The locals' types,
/// the lambdas' and the contexts are named together, so an `α` in a parameter's type is the `α` of a context; a local
/// def that writes its contexts shows them before its type. A name or context belongs to the innermost lambda or def
/// whose value it is declared or provided in, and to the module if there is none. Empty when the module did not
/// type-check.
let at (names: TypeNotation) (file: string) (result: CompileResult) (offset: int) : Info =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        let expression, instantiation =
            match Definition.typeDefinitionAt names file result offset with
            | Some info -> Some info, None
            | None -> Hover.typeAndInstantiationAt names result offset
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
        let lambdas = lambdasAround syntax offset
        let defs = defsAround syntax offset |> List.choose (fun def -> defSymbol typed def |> Option.map (fun symbol -> symbol, def))
        let contexts = contextsAt typed syntax offset
        let callContexts = callContextsAt typed syntax offset
        let localGroups = localTypes |> List.map (fun (ty, required) -> Option.toList ty @ required)
        let lambdaGroups = lambdas |> List.map (fun lambda -> typed.ExprTypes |> Map.tryFind lambda.Id |> Option.toList)
        let contextGroups = contexts |> List.map (fun (ty, _, _, _, _) -> [ ty ])
        let callGroups = callContexts |> List.map (fun (ty, _, _, _) -> [ ty ])
        let groups = localGroups @ lambdaGroups @ contextGroups @ callGroups
        let written = splitAs groups (Unifier.tysToDisplayStrings names (List.concat groups))
        let localsWritten, written = List.splitAt locals.Length written
        let lambdasWritten, written = List.splitAt lambdas.Length written
        let contextsWritten, callWritten = List.splitAt contexts.Length written
        let scoped (symbol: Symbol) typeWritten =
            { Name = symbol.Name; Type = typeWritten; Kind = symbol.Kind; Declaration = symbol.Declaration; Symbol = symbol }
        let documented (ty: Ty) (instance: Symbol option) = Option.toList instance @ Option.toList (contextSymbol typed ty)
        let localNames =
            List.map2
                (fun (symbol, (ty: Ty option, _)) typeWritten ->
                    match ty, typeWritten with
                    | Some _, written :: required -> scoped symbol (Some (Signatures.withContexts required written))
                    | _ -> scoped symbol None)
                (List.zip locals localTypes)
                localsWritten
        let scopedContexts =
            List.map2
                (fun (ty, source, span, instance, owner) written ->
                    owner, ({ Context = List.exactlyOne written; Source = source; Span = span; Documented = documented ty instance }: ScopedContext))
                contexts
                contextsWritten
        // The lambdas and defs around the position, innermost first, each with the span its names and contexts are in:
        // a def's value, since its name is declared in the scope around it.
        let enclosing =
            (List.map2
                (fun (lambda: Expression) typeWritten ->
                    { Kind = LambdaScope
                      Heading = lambdaHeading lambda
                      Span = lambda.Span
                      Type = List.tryHead typeWritten
                      Declared = false
                      Symbol = None
                      Names = []
                      Contexts = [] },
                    lambda.Span)
                lambdas
                lambdasWritten)
            @ (defs
               |> List.map (fun (symbol, (name, annotation, _, value)) ->
                   { Kind = FunctionScope
                     Heading = $"def {name}"
                     Span = symbol.Declaration
                     Type = topLevelType names typed symbol
                     Declared = annotation.IsSome
                     Symbol = Some symbol
                     Names = []
                     Contexts = [] },
                   value.Span))
            |> List.sortBy (fun (_, covering) -> covering.End - covering.Start)
        let ownerOf (span: SourceSpan) = enclosing |> List.tryFindIndex (fun (_, covering) -> SourceSpan.contains covering span)
        let namesIn index = localNames |> List.filter (fun name -> ownerOf name.Declaration = index)
        let contextsIn index = scopedContexts |> List.filter (fun (owner, _) -> ownerOf owner = index) |> List.map snd
        let topLevel =
            inScope
            |> List.filter (fun symbol -> (symbol.Kind = TopLevelValue || symbol.Kind = ImportedModule) && not (ReservedNames.isHidden symbol))
            |> List.sortBy (fun symbol -> symbol.Declaration.File <> file, symbol.Name)
            |> List.map (fun symbol -> scoped symbol (topLevelType names typed symbol))
        { Expression = expression
          ExpressionSymbol = expression |> Option.bind (fun _ -> Definition.symbolAt file result offset)
          Instantiation = instantiation
          Scopes =
            (enclosing |> List.mapi (fun index (scope, _) -> { scope with Names = namesIn (Some index); Contexts = contextsIn (Some index) }))
            @ [ { moduleScope syntax with Names = namesIn None @ topLevel; Contexts = contextsIn None } ]
          Call =
            List.map2
                (fun (ty, source, span, instance) written ->
                    ({ Context = List.exactlyOne written; Source = source; Span = span; Documented = documented ty instance }: CallContext))
                callContexts
                callWritten }
    | _ -> { Expression = None; ExpressionSymbol = None; Instantiation = None; Scopes = []; Call = [] }

