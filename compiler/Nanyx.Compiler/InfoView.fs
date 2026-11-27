/// What the info view shows at a position in the compiled root module: the expression selected or at the cursor, with
/// its type, and each scope around it, from the innermost lambda out to the module, with the names it declares and the
/// contexts it provides. Positions are character offsets into the source as the parser saw it, as for `Hover`.
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
/// `Name` is the context's name as `Context` starts with it, which the info view links to what provides it.
type ScopedContext = { Context: string; Name: string; Source: ContextSource; Span: SourceSpan; Documented: Symbol list }

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

/// What kind of scope around the position a `Scope` is.
type ScopeKind =
    /// A lambda that is not a def's value, nor a `memory` block, which is part of the scope it is in.
    | LambdaScope
    /// A def, top-level or local, with the lambda that is its value if it is one.
    | FunctionScope
    /// The module.
    | ModuleScope

/// How the info view heads a scope: `Code`, which it writes as code, after `Label`, which says what kind of code it is
/// where the code doesn't: `lambda { message -> … }`, `expression x + 1`, but `def greet`, `for xs | x -> …`.
type Heading = { Label: string option; Code: string }

/// A scope around the position. `Heading` names it: a lambda by its parameters (`{ message -> … }`), or as it is
/// written where its parameters are made up, as a shorthand lambda's are (`{ * 2 }`); a `for` loop's lambda as the
/// loop is written (`for xs | x -> …`); a def by its name (`def greet`), a custom pattern by its tags
/// (`pattern #Even | #Odd`), the module by its declared name (`module library.lending`). `Span` is what the heading
/// links to: the lambda, the `for` loop, the def's name, or the module's declaration (none if it has none). `Type` is a lambda's type, with the
/// contexts it carries, or a def's signature, as hover writes them, and `Declared` says whether the def writes its type;
/// `Symbol` is the def's, whose doc comment the info view shows. `Names` are the parameters, locals and pattern variables it declares, nearest the position first, and for
/// the module its top-level defs and imports, its own first, then by name, less intrinsics. `Contexts` are those it
/// provides: a `use` in it, a def's signature or what a def was inferred to require, or the module's runtime.
type Scope =
    { Kind: ScopeKind
      Heading: Heading
      Span: SourceSpan
      Type: string option
      Declared: bool
      Symbol: Symbol option
      Names: ScopedName list
      Contexts: ScopedContext list }

/// What the info view shows. `Scopes` are the scopes around the position, innermost first and the module last, each
/// with what it declares and the contexts it provides. `TypeNames` are the types and contexts that have a definition,
/// each by the name the view writes it by, with the definition's symbol, for the view to link where it writes them (see
/// `Signatures.typeNamesAt`).
type Info = { Scopes: Scope list; TypeNames: (string * Symbol) list }

/// The expression the info view describes at the top, as hover writes it: `Name` is the name it is, if it is one, as
/// hover writes it before the type, and else its text; `Type` is its type there, after the contexts a def requires,
/// named as the code there can name them. `Heading` heads its scope with its text, `expression x + 1`, cut as a
/// heading's code is (see `headingCode`). `Span` is where it is written, and what the heading links to, and `Symbol`
/// what a name refers to or declares, whose doc comment the info view shows.
type Described = { Name: string; Type: string; Heading: Heading; Span: SourceSpan; Symbol: Symbol option }

/// The most of a heading's code the info view writes; longer code is cut (see `headingCode`).
let private headingLength = 40

/// `written`, code as a heading writes it: on one line, with its runs of white space made one space, and cut with `…`
/// past `headingLength` characters, or where `more` says code follows that the heading leaves out.
let internal headingCode (more: bool) (written: string) : string =
    let oneLine = System.Text.RegularExpressions.Regex.Replace(written.Trim(), @"\s+", " ")
    if oneLine.Length > headingLength then oneLine.Substring(0, headingLength - 1).TrimEnd() + " …"
    elif more then oneLine + " …"
    else oneLine

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
    WrittenReferences.symbolOf typed expr.Id

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

/// The heading of a `for` loop, `call`, written in `source`, whose lambda is `loop` and its body `body`: the loop as
/// written up to the body of its arm, `for xs | x -> …`, up to its arms where they have several bodies, `for xs | …`,
/// or up to the body after its arrow, `for 1..10 -> …`.
let private forHeading (source: string) (call: Expression) (loop: Expression) (body: Expression) : string =
    let upTo (finish: int) = SourceSpan.textIn source { call.Span with End = finish }
    match body.Kind with
    | Match(_, arms) when body.Span = loop.Span ->
        match arms |> List.map snd |> List.distinctBy _.Id with
        | [ armBody ] -> headingCode true (upTo armBody.Span.Start)
        | _ -> headingCode true (upTo loop.Span.Start + " |")
    | _ -> headingCode true (upTo body.Span.Start)

/// Whether `name`, the `index`th parameter of a lambda (counted from 0), is one the parser made up: a shorthand
/// lambda's, or the one in place of a pattern (see `ImplicitNames`).
let private isMadeUp (index: int) (name: Identifier) =
    name = implicitArgument || name = implicitSecondArgument || name = patternArgument (index + 1)

/// The heading of the scope of `lambda`, written in `source`, and the span it links to. The lambda of a `for` loop
/// among `around` is headed as the loop is written (see `forHeading`) and links to the loop. Any other is a lambda:
/// written as it is where the parser made up a parameter, `{ * 2 }`, and else by its parameters, `{ message -> … }`,
/// or `{ … }` if it has none.
let private lambdaHeading (source: string) (around: Expression list) (lambda: Expression) : Heading * SourceSpan =
    let loop =
        around
        |> List.tryPick (fun expr ->
            match expr.Kind with
            | ForSyntax.Call(_, loop, _, body) when loop.Id = lambda.Id -> Some (expr, body)
            | _ -> None)
    let asLambda code = { Label = Some "lambda"; Code = code }, lambda.Span
    match loop, lambda.Kind with
    | Some (call, body), _ -> { Label = None; Code = forHeading source call lambda body }, call.Span
    | None, Lambda(parameters, _, _) when parameters |> List.indexed |> List.exists (fun (index, (name, _)) -> isMadeUp index name) ->
        asLambda (headingCode false (SourceSpan.textIn source lambda.Span))
    | None, Lambda(parameters, _, _) when not parameters.IsEmpty -> asLambda $"""{{ {parameters |> List.map fst |> String.concat ", "} -> … }}"""
    | _ -> asLambda "{ … }"

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
        match Solving.unify [ general, calleeTy, Equal, SourceSpan.none ] with
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
            match List.tryItem index instances |> Option.flatten, FunctionContexts.contextName context with
            | Some (InstanceInScope(_, provider)), _ -> Some (provided context provider)
            | Some (InstanceSynthesized members), Some ctxName -> Some (context, builtSource ctxName members, SourceSpan.none, None)
            | Some (InstanceDerived _), _ -> Some (context, Derived, SourceSpan.none, None)
            | Some InstanceBuiltIn, _ -> Some (context, BuiltIn, SourceSpan.none, None)
            | None, Some ctxName ->
                providers
                |> List.tryFind (fun p -> FunctionContexts.contextName p.Context = Some ctxName)
                |> Option.map (fun p -> provided context p.Provider)
            | _ -> None)
        |> List.choose id
    let covered = declared |> List.choose (fun (context, _, _, _) -> FunctionContexts.contextName context) |> Set.ofList
    let others =
        providers
        |> List.filter (fun p -> FunctionContexts.contextName p.Context |> Option.forall (covered.Contains >> not))
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

let private isLocal (symbol: Symbol) =
    match symbol.Kind with
    | LocalValue
    | Parameter
    | PatternVariable -> true
    | TopLevelValue
    | ImportedModule
    | TypeDefinition
    | ContextMember
    | UnitOfMeasure -> false

/// The type of a top-level def or import in scope, after the contexts it requires, as hover writes it, with the types
/// and contexts it names named by `rename`.
let private topLevelType (names: TypeNotation) (rename: Ty -> Ty) (typed: TypedModule) (symbol: Symbol) : string option =
    Hover.declaredType typed symbol
    |> Option.map (fun (ty, contexts) -> Signatures.typeWithContexts typed.TypeDefs names (rename ty) (List.map rename contexts))

/// `written` cut into runs of the lengths of `groups`.
let rec private splitAs (groups: 'a list list) (written: string list) : string list list =
    match groups with
    | [] -> []
    | group :: rest ->
        let here, after = List.splitAt group.Length written
        here :: splitAs rest after

/// The module's scope, with no names or contexts yet: headed by the name its `module` declaration gives it, if any.
let private moduleScope (syntax: Module) : Scope =
    let declared = syntax |> List.tryPick (fun item -> match item.Kind with ModuleDecl(_, name) -> Some (name, item.Span) | _ -> None)
    { Kind = ModuleScope
      Heading = { Label = None; Code = match declared with Some (name, _) -> $"module {name}" | None -> "this module" }
      Span = declared |> Option.map snd |> Option.defaultValue SourceSpan.none
      Type = None
      Declared = false
      Symbol = None
      Names = []
      Contexts = [] }

/// What the info view shows at `offset` in the compiled root module, compiled as `file` (empty for source compiled
/// directly) from `source`, with made-up type variables named by `names`. The locals' types, the lambdas' and the contexts are named
/// together, so an `α` in a parameter's type is the `α` of a context; a local
/// def that writes its contexts shows them before its type. Every type and context is named as the code at `offset`
/// can name it (see `Signatures.relativeAt`): `Point` inside `module Points`. A name or context belongs to the innermost lambda or def
/// whose value it is declared or provided in, and to the module if there is none. Empty when the module did not
/// type-check.
let at (names: TypeNotation) (file: string) (source: string) (result: CompileResult) (offset: int) : Info =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        let rename = Signatures.relativeAt typed.TypeDefs syntax offset
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
        let localGroups = localTypes |> List.map (fun (ty, required) -> Option.toList ty @ required)
        let lambdaGroups = lambdas |> List.map (fun lambda -> typed.ExprTypes |> Map.tryFind lambda.Id |> Option.toList)
        let contextGroups = contexts |> List.map (fun (ty, _, _, _, _) -> [ ty ])
        let groups = localGroups @ lambdaGroups @ contextGroups
        let written = splitAs groups (Unifier.tysToDisplayStringsIn typed.TypeDefs names (List.concat groups |> List.map rename))
        let localsWritten, written = List.splitAt locals.Length written
        let lambdasWritten, contextsWritten = List.splitAt lambdas.Length written
        let scoped (symbol: Symbol) typeWritten =
            { Name = symbol.Name; Type = typeWritten; Kind = symbol.Kind; Declaration = symbol.Declaration; Symbol = symbol }
        let writtenName (ty: Ty) = FunctionContexts.contextName (rename ty) |> Option.defaultValue ""
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
                    owner, ({ Context = List.exactlyOne written; Name = writtenName ty; Source = source; Span = span; Documented = documented ty instance }: ScopedContext))
                contexts
                contextsWritten
        // The lambdas and defs around the position, innermost first, each with the span its names and contexts are in:
        // a def's value, since its name is declared in the scope around it.
        let around = Hover.expressionsAt offset syntax
        let enclosing =
            (List.map2
                (fun (lambda: Expression) typeWritten ->
                    let heading, headed = lambdaHeading source around lambda
                    { Kind = LambdaScope
                      Heading = heading
                      Span = headed
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
                     Heading =
                       { Label = None
                         Code =
                           match Signatures.patternName name with
                           | Some tags -> $"{PatternSyntax.keyword} {tags}"
                           | None -> $"def {name}" }
                     Span = symbol.Declaration
                     Type = topLevelType names rename typed symbol
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
            |> List.map (fun symbol -> scoped symbol (topLevelType names rename typed symbol))
        { Scopes =
            (enclosing |> List.mapi (fun index (scope, _) -> { scope with Names = namesIn (Some index); Contexts = contextsIn (Some index) }))
            @ [ { moduleScope syntax with Names = namesIn None @ topLevel; Contexts = contextsIn None } ]
          TypeNames = Signatures.typeNamesAt typed.TypeDefs typed.Symbols syntax offset }
    | _ -> { Scopes = []; TypeNames = [] }

/// The expression the info view describes at `selection` in the compiled root module, parsed from `source`, with
/// made-up type variables named by `names`. For a selection, the expression written exactly at it, less the spaces
/// around it, the outermost if several are (see `SelectedExpression.at`); for an empty one, what hover describes where
/// it is (see `Hover.typeAt`): the innermost expression there, or the name a declaration declares. None where the
/// selection is not a single expression, nothing typed is there, or the module did not type-check.
let expressionAt (names: TypeNotation) (source: string) (result: CompileResult) (selection: SourceSpan) : Described option =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        let found =
            if selection.End > selection.Start then
                SelectedExpression.at source syntax selection
                |> Option.bind (fun (expr, _, _) ->
                    let name = match expr.Kind with IdentifierExpr _ -> referenced typed expr | _ -> None
                    typed.ExprTypes |> Map.tryFind expr.Id |> Option.map (fun ty -> ty, name, expr.Span))
            else Hover.hoveredAt syntax typed selection.Start
        found
        |> Option.map (fun (ty, symbol, span) ->
            let rename = Signatures.relativeAt typed.TypeDefs syntax span.Start
            { Name = symbol |> Option.map _.Name |> Option.defaultValue (SourceSpan.textIn source span)
              Type = Hover.typeWrittenAt names rename typed symbol ty
              Heading = { Label = Some "expression"; Code = headingCode false (SourceSpan.textIn source span) }
              Span = span
              Symbol = symbol })
    | _ -> None

