/// The contexts a definition requires: reading them from its annotation, the members each one supplies, and whether
/// they are available where a definition is named.
module NyxCompiler.Contexts

open Parser.Program
open NyxCompiler
open NyxCompiler.InferState
open NyxCompiler.TypeResolution

let internal extractContextFromTypeExpr (typeExpr: TypeExpr option) : TypeExpr list option * TypeExpr option =
    match typeExpr with
    | Some (Kind (TypeWithContext(contexts, inner))) -> Some contexts, Some inner
    | Some (Kind (TypeContext contexts)) -> Some contexts, None
    | Some other -> None, Some other
    | None -> None, None

/// `extractContextFromTypeExpr` for the annotation `typeExpr` of a def whose value is `value`. A lambda runs in the
/// contexts the def writes. Any other value of a function type, such as a function named point-free (`def g: [Console]
/// int -> () = println`), runs in none: it is a function that requires them, as its type says. The backend reads a
/// def's contexts with this too.
let extractDefContexts (typeExpr: TypeExpr option) (value: Expression) : TypeExpr list option * TypeExpr option =
    match typeExpr, value.Kind with
    | _, Lambda _ -> extractContextFromTypeExpr typeExpr
    | Some (Kind (TypeWithContext(_, Kind (TypeFunc _)))), _ -> None, typeExpr
    | _ -> extractContextFromTypeExpr typeExpr

/// `typeOpt`, a def's annotation, with each operator it requires by its member read as the prelude's context that has
/// it (see `InlineContexts`), and the errors in how they are written, if any.
let internal readInlineContexts (state: InferState) (typeOpt: TypeExpr option) : TypeExpr option * Result<unit, Diagnostic list> =
    match typeOpt |> Option.map (InlineContexts.resolve typeExprToTy state) with
    | Some (Ok resolved) -> Some resolved, Ok ()
    | Some (Error errors) -> typeOpt, Error errors
    | None -> None, Ok ()

/// `env` with the members of the instance a `use` of a value of type `ty` loads, each bound by its name. When the
/// type names a plain context, as `Foo(bar = ...)`'s does, each member has the symbol the context's definition
/// declared, as in a def that declares the context, so a lambda that calls it requires the context (see
/// `LambdaContexts.contextOfMember`). Such a member's type variables are its own, so each call instantiates them
/// afresh, as in a def that declares the context. Each member is provided by `provider`, the `use`.
let internal extendEnvWithRecordFields (provider: ContextProvider) (state: InferState) (env: TypeEnv) (ty: Ty) =
    let context =
        let plain (info: TypeDefInfo) = info.IsContext && info.Parameters.IsEmpty
        match resolveSoFar state ty with
        | TyNominal(id, _, _) ->
            state.TypeDefs |> Map.tryPick (fun _ info -> if plain info && TypeDefInfo.nominalId info = id then Some info else None)
        | TyAlias(name, [], _)
        | TyApply(name, []) -> state.TypeDefs |> Map.tryFind name |> Option.filter plain
        | _ -> None
    let addFields fields =
        fields
        |> Map.fold (fun acc name fieldTy ->
            let symbol = context |> Option.bind (fun info -> info.Members |> Map.tryFind name)
            let quantified = if context.IsSome then TypeEnv.freeTyVars fieldTy else Set.empty
            TypeEnv.extend name { TypeEnv.mono fieldTy with Quantified = quantified; Symbol = symbol; ProvidedBy = Some provider } acc) env
    match Unifier.unalias ty with
    | TyRecord fields -> addFields fields
    | TyNominal(_, underlying, _) ->
        match Unifier.unalias underlying with
        | TyRecord fields -> addFields fields
        | _ -> env
    | _ -> env

/// The members of the context `ctxExpr` names, each with its type and its symbol, if the context declares one.
let rec private collectContextMembers (state: InferState) (ctxExpr: TypeExpr) : (Identifier * Ty * SymbolId option) list * InferState =
    match ctxExpr.Kind with
    | TypeRecord fields ->
        let mutable current = state
        let members =
            fields
            |> List.choose (function
                | TypeField(name, _, typeOpt, _) -> Some (name, typeOpt)
                | TypeMember(name, typeOpt) -> Some (name, typeOpt))
            |> List.map (fun (name, typeOpt) ->
                let ty, next =
                    match typeOpt with
                    | Some t -> typeExprToTy current t
                    | None -> freshVar current
                current <- next
                name, ty, None)
        members, current
    | TypeContext contexts ->
        let mutable current = state
        let members =
            contexts
            |> List.collect (fun item ->
                let itemMembers, next = collectContextMembers current item
                current <- next
                itemMembers)
        members, current
    | TypeName name
    | TypeApply(name, _) ->
        let infoOpt = state.TypeDefs |> Map.tryFind name
        let state =
            match infoOpt with
            | Some info -> recordTypeReference ctxExpr info state
            | None -> state
        // Each member's symbol is the one the context's definition declared, in whichever module that is.
        let withSymbols (members: (Identifier * Ty * SymbolId option) list) =
            match infoOpt with
            | Some info -> members |> List.map (fun (memberName, ty, _) -> memberName, ty, info.Members |> Map.tryFind memberName)
            | None -> members
        // The members of the type `info` defines, with the context's parameters the types it is applied to here: an
        // imported context's own variables are another module's, and mean nothing in this one.
        let appliedMembers (info: TypeDefInfo) =
            match Unifier.unalias info.Underlying with
            | TyRecord fields ->
                let argTys, state =
                    match ctxExpr.Kind with
                    | TypeApply(_, args) when args.Length = info.Parameters.Length -> args |> List.mapFold typeExprToTy state
                    | _ -> [], state
                Some (fields |> Map.toList |> List.map (fun (memberName, ty) -> memberName, Unifier.applyToParameters info.Parameters argTys ty, None) |> withSymbols, state)
            | _ -> None
        // `Ask(int)`'s `ask` is the definition's `() -> a` with `a` the `int` it is applied to.
        let applied =
            match ctxExpr.Kind, infoOpt with
            | TypeApply(_, args), Some info when not info.Parameters.IsEmpty && args.Length = info.Parameters.Length -> appliedMembers info
            | _ -> None
        match state.ContextDefs |> Map.tryFind name, applied, infoOpt with
        | _, Some members, _ -> members
        | Some ctxDef, None, _ ->
            let members, next = collectContextMembers state ctxDef
            withSymbols members, next
        // Also check TypeDefs for non-context types used as contexts
        | None, None, Some info -> appliedMembers info |> Option.defaultValue ([], state)
        | None, None, None -> [], state
    | _ -> [], state

/// The type of the member `memberName` of the context `ctxExpr` names, `C(T)` as `C(T).member` writes it: the member's
/// type in the context's definition, with the context's parameters the types it is applied to. None if the context
/// has no such member.
let internal memberType (state: InferState) (ctxExpr: TypeExpr) (memberName: Identifier) : (Ty * InferState) option =
    match ctxExpr.Kind with
    | TypeApply(name, args) ->
        match tryFindTypeDef name state with
        | Some info when info.IsContext && args.Length = info.Parameters.Length ->
            match Unifier.unalias info.Underlying with
            | TyRecord fields ->
                fields
                |> Map.tryFind memberName
                |> Option.map (fun ty ->
                    let argTys, next = args |> List.mapFold typeExprToTy state
                    Unifier.applyToParameters info.Parameters argTys ty, next)
            | _ -> None
        | _ -> None
    | _ -> None

/// The type variables of the members of `ctx` that its instance fixes: those of the types a parameterised context is
/// applied to (`a` in `Eq(a)`). None when every one of them is fixed, as for a parameterised context written without
/// its arguments.
let private instanceFixedVars (state: InferState) (ctx: TypeExpr) : Set<int> option =
    match ctx.Kind with
    | TypeApply(_, args) -> args |> List.mapFold typeExprToTy state |> fst |> List.map TypeEnv.freeTyVars |> Set.unionMany |> Some
    | TypeName name ->
        match tryFindTypeDef name state with
        | Some info when not info.Parameters.IsEmpty -> None
        | _ -> Some Set.empty
    | _ -> Some Set.empty

/// `env` with the members of each of `contexts`, each provided by what `provider` says provides it. A member's own type
/// variables, those the instance does not fix, are quantified, so each call instantiates them afresh, as a call of a
/// generic top-level function does: `bind` in `Proc` can be called at a `string` payload and then at an `int` one.
let private extendEnvWithContextsFrom (provider: TypeExpr -> ContextProvider) (env: TypeEnv) (state: InferState) (contexts: TypeExpr list) =
    let mutable current = state
    let mutable env' = env
    for ctx in contexts do
        let members, next = collectContextMembers current ctx
        current <- next
        let fixedVars = instanceFixedVars current ctx
        let memberScheme ty =
            let quantified =
                match fixedVars with
                | Some fixedVars -> Set.difference (TypeEnv.freeTyVars ty) fixedVars
                | None -> Set.empty
            { TypeEnv.mono ty with Quantified = quantified; ProvidedBy = Some (provider ctx) }
        // A context named through an import's alias (`c.Console`) also supplies its members under the alias
        // (`c.println`), which would otherwise still be the ambient instance's.
        let qualifier =
            match ctx.Kind with
            | TypeName name
            | TypeApply(name, _) when name.Contains '.' -> Some (name.Substring(0, name.LastIndexOf '.'))
            | _ -> None
        env' <-
            members
            |> List.fold (fun acc (name, ty, symbol) ->
                let acc = TypeEnv.declare name symbol (memberScheme ty) acc
                match qualifier with
                | Some qualifier -> TypeEnv.declare $"{qualifier}.{name}" symbol (memberScheme ty) acc
                | None -> acc) env'
    env', current

/// `env` with the members of each of `contexts`, the contexts a def's type declares, each provided by the signature.
let internal extendEnvWithContexts (env: TypeEnv) (state: InferState) (contexts: TypeExpr list) =
    extendEnvWithContextsFrom (fun ctx -> FromSignature ctx.Span) env state contexts

/// `env` for the body of `lambda`, passed as a parameter of the type `paramTy`: with the members of each plain context
/// that type requires, as far as `state` says, so the body can call them and what requires them. The function the
/// lambda is passed to supplies them, as `withLog: [Console & c] ([Log & c] () -> ()) -> ()` supplies `Log` to its
/// block, so the lambda's type requires them and the code around it does not (see `LambdaContexts`). Each is provided
/// by the type the lambda is checked against, at the lambda. A script's statements (`InferState.ScriptEntry`) are not
/// given them: they are the top level, whose names they see, as the module's own defs, and whose ambient contexts they
/// run in. A context variable, a parameterised context and a scoped
/// one are not bound: a scoped context is the call's (see `ScopedCalls`). The contexts are not written in the source,
/// so nothing refers to them: `state` keeps only the variables reading their members made.
let internal extendEnvForLambdaArgument (env: TypeEnv) (state: InferState) (lambda: Expression) (paramTy: Ty option) : TypeEnv * InferState =
    let required =
        match paramTy |> Option.map (resolveSoFar state >> Unifier.unalias) with
        | Some (TyFunc(_, _, contexts)) ->
            contexts
            |> List.choose (function
                | TyApply(name, []) ->
                    match state.TypeDefs |> Map.tryFind name with
                    | Some info when info.IsContext && info.Parameters.IsEmpty && not (WrittenContexts.isScoped state name) ->
                        Some (TypeExpr.synthetic (TypeName name))
                    | _ -> None
                | _ -> None)
        | _ -> []
    match required with
    | [] -> env, state
    | _ when state.ScriptEntry = Some lambda.Id -> env, state
    | _ ->
        let env, next = extendEnvWithContextsFrom (fun _ -> FromSignature lambda.Span) env state required
        env, { state with NextId = next.NextId }

/// The contexts `ctxExpr` names that do not exist, each with where its name is written, if the parser recorded it.
let rec private unknownContextNames (state: InferState) (ctxExpr: TypeExpr) : (Identifier * SourceSpan option) list =
    match ctxExpr.Kind with
    | TypeContext contexts -> contexts |> List.collect (unknownContextNames state)
    | TypeName name
    | TypeApply(name, _) ->
        if state.ContextDefs.ContainsKey name || (tryFindTypeDef name state).IsSome then []
        elif SourceSpan.isNone ctxExpr.Span then [ name, None ]
        else [ name, Some { ctxExpr.Span with End = ctxExpr.Span.Start + name.Length } ]
    | _ -> []

/// Whether every context in the signature of `name` exists. The error for one that does not is written at the
/// context's name, or at `span`, where the definition's name is written, when the context's is not known. It says
/// which import brings the context in when a standard-library module exports it.
let internal ensureContextsExist (state: InferState) (name: Identifier) (span: SourceSpan) (contexts: TypeExpr list) : Result<unit, Diagnostic list> =
    match contexts |> List.collect (unknownContextNames state) |> List.distinctBy fst with
    | [] -> Ok ()
    | unknown ->
        unknown
        |> List.map (fun (ctxName, at) ->
            let message = $"Unknown context '{ctxName}' in the signature of '{name}'" + StandardLibraryImports.hint state.StandardLibraryExports.Value ctxName
            Diagnostics.at DiagnosticCodes.UnknownContext (at |> Option.defaultValue span) message)
        |> Error

/// Whether each type variable a context in `typeOpt`, the annotation of `name`, is applied to is written in its type
/// too, as `a` in `[ToString(a)] a -> ()`. No call can say what a variable written only in the contexts stands for, so
/// none could find the context's instance. An annotation of only contexts is not checked: its type is inferred.
let internal ensureContextTypesDetermined (name: Identifier) (typeOpt: TypeExpr option) : Result<unit, Diagnostic list> =
    match typeOpt with
    | Some (Kind (TypeWithContext(contexts, inner))) ->
        let written = annotationVarNames inner |> Set.ofList
        let contextText ctxName (args: TypeExpr list) =
            let simple = args |> List.choose (fun arg -> match arg.Kind with TypeVar n | TypeName n -> Some n | _ -> None)
            if simple.Length = args.Length then $"""{ctxName}({String.concat ", " simple})""" else ctxName
        let errors =
            contexts
            |> List.collect (fun ctx ->
                match ctx.Kind with
                | TypeApply(ctxName, args) ->
                    args
                    |> List.collect annotationVarNames
                    |> List.distinct
                    |> List.filter (written.Contains >> not)
                    |> List.map (fun var ->
                        Diagnostics.at DiagnosticCodes.UndeterminedContextType ctx.Span
                            $"'{var}' in '{contextText ctxName args}' is not the type of any parameter of '{name}' or of its result, so no call can supply '{contextText ctxName args}'")
                | _ -> [])
        if errors.IsEmpty then Ok () else Error errors
    | _ -> Ok ()

/// Whether `name` is bound in `env` to the ambient instance's member, not to something nearer that shadows it: a
/// member of a context the def declares, or of an instance a `use` loads.
let private isAmbientMember (env: TypeEnv) (state: InferState) (name: Identifier) =
    match state.AmbientMembers |> Map.tryFind name, TypeEnv.tryFind name env with
    | Some (symbol, _), Some scheme -> scheme.Symbol = Some symbol
    | _ -> false

/// Whether something nearer than the ambient instance supplies the members `members` in `env`: each is in scope, and
/// not as the instance's.
let private suppliedNearer (env: TypeEnv) (state: InferState) (members: Identifier list) =
    members |> List.forall (fun memberName -> TypeEnv.tryFind memberName env |> Option.isSome && not (isAmbientMember env state memberName))

/// The ambient contexts, each with the names of its members.
let private ambientContextMembers (state: InferState) : (Identifier * Identifier list) list =
    state.AmbientMembers
    |> Map.toList
    |> List.groupBy (fun (_, (_, ctxName)) -> ctxName)
    |> List.map (fun (ctxName, members) -> ctxName, List.map fst members)

/// The ambient contexts that code in `env` runs in: every one at the top level, else those something nearer
/// supplies every member of.
let private availableAmbientContexts (env: TypeEnv) (state: InferState) : Set<Identifier> =
    let fromMembers =
        ambientContextMembers state
        |> List.filter (fun (_, members) ->
            match state.ContextScope with
            | AmbientScope -> true
            | DefScope _
            | ValueScope _
            | ScriptValueScope _ -> suppliedNearer env state members)
        |> List.map fst
        |> Set.ofList
    match state.ContextScope with
    | AmbientScope -> Set.union fromMembers state.CarriedAmbientContexts
    | DefScope _
    | ValueScope _
    | ScriptValueScope _ -> fromMembers

/// What provides an ambient context to code in `scope` where the ambient instance supplies it: the runtime at the top
/// level, and inference in a def that does not write its type. None in a def that writes its type, or in a named
/// module's value, where that is an error.
let internal ambientProvider (scope: ContextScope) : ContextProvider option =
    match scope with
    | AmbientScope
    | ScriptValueScope _ -> Some FromRuntime
    | DefScope(_, true) -> Some FromInference
    | DefScope(_, false)
    | ValueScope _ -> None

/// What provides, to code in `env`, a context whose members are bound there by the names `members`: the ambient
/// instance where it supplies them (`ambient`), else the `use` or the signature that binds them. None where nothing
/// that provides contexts binds them.
let private providerOf (env: TypeEnv) (state: InferState) (ambient: bool) (members: Identifier list) : ContextProvider option =
    if ambient then ambientProvider state.ContextScope
    else members |> List.tryPick (fun memberName -> TypeEnv.tryFind memberName env |> Option.bind _.ProvidedBy)

/// `state` with `provider` recorded as what provides the context `ctxName` to the call at `call` (see
/// `TypedModule.ContextProviders`), in place of what inferring the call before recorded for it.
let internal recordProvider (call: NodeId) (ctxName: Identifier) (provider: ContextProvider) (state: InferState) =
    let provided = { Context = TyApply(ctxName, []); Provider = provider }
    let recorded = state.ContextProviders |> Map.tryFind call |> Option.defaultValue []
    let updated =
        if recorded |> List.exists (fun earlier -> earlier.Context = provided.Context) then
            recorded |> List.map (fun earlier -> if earlier.Context = provided.Context then provided else earlier)
        else recorded @ [ provided ]
    { state with ContextProviders = state.ContextProviders |> Map.add call updated }

/// `state` with what provides each ambient context to the calls of unannotated top-level defs among `uses` recorded,
/// now that `required` says the contexts each such def requires.
let internal recordDeferredProviders (required: Map<Identifier, Set<Identifier>>) (uses: DeferredContextUse list) (state: InferState) =
    uses
    |> List.fold (fun current used ->
        match used.Call, required |> Map.tryFind used.Callee with
        | Some call, Some contexts ->
            contexts
            |> Set.fold (fun current ctxName ->
                match used.Providers |> Map.tryFind ctxName |> Option.orElse (ambientProvider used.Caller) with
                | Some provider -> recordProvider call ctxName provider current
                | None -> current) current
        | _ -> current) state

/// What provides each ambient context to code in `env` (see `providerOf`).
let private ambientProviders (env: TypeEnv) (state: InferState) : Map<Identifier, ContextProvider> =
    let ofMembers =
        ambientContextMembers state
        |> List.choose (fun (ctxName, members) ->
            providerOf env state (not (suppliedNearer env state members)) members |> Option.map (fun provider -> ctxName, provider))
    let carried =
        state.CarriedAmbientContexts
        |> Set.toList
        |> List.choose (fun ctxName -> ambientProvider state.ContextScope |> Option.map (fun provider -> ctxName, provider))
    Map.ofList (carried @ ofMembers)

/// `state` with what provides its context recorded for `call`, a call of `name`, where `name` is a context's member as
/// `env` binds it.
let private recordMemberCall (call: NodeId option) (env: TypeEnv) (name: Identifier) (state: InferState) =
    match call, LambdaContexts.contextOfMember env state name with
    | Some call, Some ctxName ->
        match providerOf env state (isAmbientMember env state name) [ name ] with
        | Some provider -> recordProvider call ctxName provider state
        | None -> state
    | _ -> state

/// `what` (which ends "requires", say) the context `ctxName` at `span`, in the def `defName`, whose type does not
/// declare it. A def whose type says it requires none, with `[]`, said so on purpose, so it isn't told to add one.
let internal undeclaredContext (state: InferState) (defName: Identifier) (ctxName: Identifier) (what: string) (span: SourceSpan) =
    let message =
        match state.WrittenTypes |> Map.tryFind defName with
        | Some { Kind = TypeWithContext([], _) } ->
            $"{what} the context '{ctxName}', but the type of '{defName}' says with [] that it requires no contexts: load an instance with `use`, or call something that requires none"
        | _ ->
            $"{what} the context '{ctxName}', which '{defName}' does not declare: add [{ctxName}] to the type of '{defName}', or load an instance with `use`"
    Diagnostics.at DiagnosticCodes.UndeclaredContext span message

/// `what` (which ends "requires", say) the context `ctxName` at `span`, in the value `valueName` of a named module,
/// which cannot have one.
let internal contextInValue (valueName: Identifier) (ctxName: Identifier) (what: string) (span: SourceSpan) =
    Diagnostics.at DiagnosticCodes.UndeclaredContext span $"{what} the context '{ctxName}', which '{valueName}' cannot have: a named module's top-level values are built without contexts, so make '{valueName}' a function"

/// The error for naming `valueName` at `span`, a value the script computes as it runs (see
/// `InferState.ScriptEntryValues`), where it is not computed yet: in a function (`inFunction`), or before its def.
let internal scriptRunValue (valueName: Identifier) (inFunction: bool) (span: SourceSpan) =
    let message =
        if inFunction then $"'{valueName}' is computed when the script runs, so a function can't name it: pass it as an argument"
        else $"'{valueName}' is computed when the script runs, so it can't be read before its def: move the def up"
    Diagnostics.at DiagnosticCodes.ScriptRunValue span message

/// `infer` run on `state`, but at the top level's ambient contexts where `state` is in a named module's value: for a
/// lambda written in the value, which building the value does not run. One passed to a call is not inferred this
/// way, since the call may run it.
let internal outsideValue (infer: InferState -> Result<'T * InferState, Diagnostic list>) (state: InferState) =
    match state.ContextScope with
    | ValueScope _ as valueScope ->
        infer { state with ContextScope = AmbientScope } |> Result.map (fun (typed, next) -> typed, { next with ContextScope = valueScope })
    | _ -> infer state

/// `what` needs the ambient context `ctxName` where nothing nearer supplies it: fine at the top level, inferred for
/// an unannotated def or a script's value, and an error in a def whose type does not declare it or in a named module's
/// value.
let private needAmbientContext (state: InferState) (ctxName: Identifier) (what: string) (span: SourceSpan) : Result<InferState, Diagnostic list> =
    match state.ContextScope with
    | AmbientScope -> Ok state
    | DefScope(defName, true)
    | ScriptValueScope defName ->
        let contexts = state.InferredContexts |> Map.tryFind defName |> Option.defaultValue Set.empty
        Ok { state with InferredContexts = state.InferredContexts |> Map.add defName (contexts.Add ctxName) }
    | DefScope(defName, false) -> Error [ undeclaredContext state defName ctxName what span |> ContextFixes.withAddToType state defName ctxName ]
    | ValueScope valueName -> Error [ contextInValue valueName ctxName what span ]

/// The name the definition `name` names is declared under: a block import's name (`zero`) stands for the qualified
/// one (`money.zero`), unless something nearer binds it.
let internal canonicalName (env: TypeEnv) (state: InferState) (name: Identifier) =
    match state.BlockAliases |> Map.tryFind name with
    | Some qualified when (TypeEnv.tryFind name env |> Option.bind _.Symbol) = (TypeEnv.tryFind qualified env |> Option.bind _.Symbol) -> qualified
    | _ -> name

/// The symbol `name` names where `env` is the environment, if it is one this module knows.
let private symbolNamed (env: TypeEnv) (state: InferState) (name: Identifier) =
    TypeEnv.tryFind name env |> Option.bind _.Symbol |> Option.bind (fun id -> state.Symbols |> Map.tryFind id)

/// The contexts the function `name` names where `env` is the environment requires, as written or inferred. A local
/// def's are its own, by its symbol, and a parameter or a pattern's variable has none: neither takes those of a
/// top-level def it shadows, which `ContextRequirements` holds under the same name.
let internal contextRequirementsOf (env: TypeEnv) (state: InferState) (name: Identifier) : TypeExpr list option =
    match symbolNamed env state name with
    | Some symbol when symbol.Kind = LocalValue -> state.LocalContextRequirements |> Map.tryFind symbol.Id
    | Some symbol when symbol.Kind = Parameter || symbol.Kind = PatternVariable -> None
    | _ -> state.ContextRequirements |> Map.tryFind (canonicalName env state name)

/// The type the function `name` names where `env` is the environment declares without its contexts, where it writes
/// one, read as `contextRequirementsOf` reads its contexts.
let internal contextSignatureOf (env: TypeEnv) (state: InferState) (name: Identifier) : TypeExpr option =
    match symbolNamed env state name with
    | Some symbol when symbol.Kind = LocalValue -> state.LocalContextSignatures |> Map.tryFind symbol.Id
    | Some symbol when symbol.Kind = Parameter || symbol.Kind = PatternVariable -> None
    | _ -> state.ContextSignatures |> Map.tryFind (canonicalName env state name)

/// The ambient contexts the unannotated top-level function `name`, as `env` binds it, was inferred to require, once its
/// body has been inferred: those it uses itself, and those of the unannotated functions it calls, as far as they have
/// been inferred too. Defs are inferred in the order they use each other (see `DefinitionOrder`), so only a function
/// in a cycle with the one naming it is not yet. `None` where `name` is not such a function, or is not inferred yet.
let internal inferredAmbientContexts (env: TypeEnv) (state: InferState) (name: Identifier) : Set<Identifier> option =
    let rec contextsOf (visited: Set<Identifier>) (defName: Identifier) =
        state.DeferredContextUses
        |> List.filter (fun used -> used.Caller = DefScope(defName, true) && not (visited.Contains used.Callee))
        |> List.fold
            (fun acc used -> Set.union acc (Set.difference (contextsOf (visited.Add used.Callee) used.Callee) used.Available))
            (state.InferredContexts |> Map.tryFind defName |> Option.defaultValue Set.empty)
    match state.UnannotatedDefs |> Map.tryFind name, TypeEnv.tryFind name env with
    | Some symbol, Some scheme when scheme.Symbol = Some symbol && (match scheme.Type with TyVar _ -> false | _ -> true) ->
        Some (contextsOf (Set.singleton name) name)
    | _ -> None

/// Checks the context use of naming `name` at `span`: a member of the ambient instance needs its context, and a use
/// of an unannotated top-level def, or of a script's value, is kept to check once its contexts are known. Where the use
/// is the call `call`, what provides the context of a member it calls is recorded, as is, once known, what provides
/// each context of the def.
let internal checkContextUse (call: NodeId option) (env: TypeEnv) (state: InferState) (name: Identifier) (span: SourceSpan) : Result<InferState, Diagnostic list> =
    if isAmbientMember env state name then
        let _, ctxName = state.AmbientMembers.[name]
        needAmbientContext state ctxName $"'{name}' is a member of" span
        |> Result.map (recordMemberCall call env name)
    else
        let deferredSymbol =
            match state.UnannotatedDefs |> Map.tryFind name with
            | Some symbol when not (state.AmbientMembers.IsEmpty && state.CarriedAmbientContexts.IsEmpty) -> Some symbol
            | _ -> state.ScriptValues |> Map.tryFind name
        match deferredSymbol, TypeEnv.tryFind name env with
        | Some symbol, Some scheme when scheme.Symbol = Some symbol ->
            let deferred =
                { Caller = state.ContextScope; Callee = name; Span = span; Available = availableAmbientContexts env state
                  Call = call; Providers = if call.IsSome then ambientProviders env state else Map.empty }
            Ok { state with DeferredContextUses = deferred :: state.DeferredContextUses }
        | _ -> Ok (recordMemberCall call env name state)

/// Checks that `contexts`, which `name` requires, are available at `span`. A context with a member not in scope at all
/// is an error only when `reportMissing`. Where they are required by the call `call`, what provides each that is not
/// parameterised is recorded.
let internal ensureContextAvailable (call: NodeId option) (reportMissing: bool) (env: TypeEnv) (state: InferState) (name: Identifier) (contexts: TypeExpr list) (span: SourceSpan) : Result<InferState, Diagnostic list> =
    // A function imported under an alias (`c.println`), or a context named under one (`c.Console`), finds the
    // context's members under the alias too (`c.write`), as the ambient instance has them, when they are not in
    // scope by their own names.
    let qualifier (qualifiedName: Identifier) =
        match qualifiedName.LastIndexOf '.' with
        | dot when dot > 0 -> Some (qualifiedName.Substring(0, dot))
        | _ -> None
    let inScopeAs (ctx: TypeExpr) (memberName: Identifier) =
        let ctxQualifier =
            match ctx.Kind with
            | TypeName ctxName
            | TypeApply(ctxName, _) -> qualifier ctxName
            | _ -> None
        if (TypeEnv.tryFind memberName env).IsSome then
            memberName
        else
            [ qualifier name; ctxQualifier ]
            |> List.choose id
            |> List.map (fun prefix -> $"{prefix}.{memberName}")
            |> List.tryFind (fun qualified -> (TypeEnv.tryFind qualified env).IsSome)
            |> Option.defaultValue memberName
    let requiredMembers =
        contexts
        |> List.map (fun ctx ->
            let members, _ = collectContextMembers state ctx
            ctx, members |> List.map (fun (memberName, _, _) -> inScopeAs ctx memberName))
    let isCarried (ctx: TypeExpr) =
        match ctx.Kind with
        | TypeName ctxName
        | TypeApply(ctxName, _) -> state.CarriedAmbientContexts.Contains ctxName
        | _ -> false
    // A carried ambient context is supplied by something nearer only if its members are in scope and not the
    // instance's; otherwise it is needed from the ambient instance, as a context this module can name is.
    let fromAmbient (ctx: TypeExpr) members =
        (isCarried ctx && not (suppliedNearer env state members)) || members |> List.exists (isAmbientMember env state)
    let recordProviders (current: InferState) =
        match call with
        | Some call ->
            requiredMembers
            |> List.fold (fun current (ctx, members) ->
                match ctx.Kind, providerOf env current (fromAmbient ctx members) members with
                | TypeName ctxName, Some provider -> recordProvider call ctxName provider current
                | _ -> current) current
        | None -> current
    // Each context with a member not in scope at all, with those members.
    let missing =
        requiredMembers
        |> List.filter (fst >> isCarried >> not)
        |> List.choose (fun (ctx, members) ->
            match members |> List.filter (fun memberName -> TypeEnv.tryFind memberName env |> Option.isNone) with
            | [] -> None
            | absent -> Some (ctx, absent))
    if missing.IsEmpty || not reportMissing then
        // Every member is in scope, but one the ambient instance supplies is only available where its context is.
        requiredMembers
        |> List.fold (fun result (ctx, members) ->
            result
            |> Result.bind (fun current ->
                match ctx.Kind, members |> List.exists (isAmbientMember env current) with
                | (TypeName ctxName | TypeApply(ctxName, _)), _ when isCarried ctx && not (suppliedNearer env current members) ->
                    needAmbientContext current ctxName $"'{name}' requires" span
                | (TypeName ctxName | TypeApply(ctxName, _)), true -> needAmbientContext current ctxName $"'{name}' requires" span
                | _ -> Ok current)) (Ok state)
        |> Result.map recordProviders
    else
        // A context is named as it is written, as a type alias is; only one written out as a record has nothing to
        // name it by but its members.
        let named, unnamed =
            missing
            |> List.partition (fun (ctx, _) -> match ctx.Kind with TypeName _ | TypeApply _ -> true | _ -> false)
        let quoted names = names |> List.distinct |> List.map (fun name -> $"'{name}'")
        let contextNames =
            named |> List.choose (fun (ctx, _) -> match ctx.Kind with TypeName ctxName | TypeApply(ctxName, _) -> Some ctxName | _ -> None) |> quoted
        let memberNames = unnamed |> List.collect snd |> quoted
        let message =
            match contextNames, memberNames with
            | [ single ], [] -> $"'{name}' requires the context {single}, which is not available here"
            | _, [] -> $"'{name}' requires the contexts {Diagnostics.listed contextNames}, which are not available here"
            | [], _ -> $"'{name}' requires the context members {Diagnostics.listed memberNames}, which are not available here"
            | _ ->
                let contextsText = if contextNames.Length = 1 then "the context" else "the contexts"
                $"'{name}' requires {contextsText} {Diagnostics.listed contextNames} and the context members {Diagnostics.listed memberNames}, which are not available here"
        // In a def that writes its type, each context named on its own can be added to it; a parameterised one is
        // written over the callee's type variables, not the def's.
        let addable =
            match state.ContextScope with
            | DefScope(defName, false) ->
                named |> List.choose (fun (ctx, _) -> match ctx.Kind with TypeName ctxName -> Some (defName, ctxName) | _ -> None)
            | _ -> []
        let diagnostic =
            addable
            |> List.fold (fun diagnostic (defName, ctxName) -> ContextFixes.withAddToType state defName ctxName diagnostic)
                (Diagnostics.at DiagnosticCodes.MissingContextMembers span message)
        Error [ diagnostic ]
