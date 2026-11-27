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
/// def's contexts with this too. So is a value of a type generic in contexts, `def lines: [FileSystem] Stream(string)`:
/// the contexts are the type's.
let extractDefContexts (typeExpr: TypeExpr option) (value: Expression) : TypeExpr list option * TypeExpr option =
    match typeExpr, value.Kind with
    | _, Lambda _ -> extractContextFromTypeExpr typeExpr
    | Some (Kind (TypeWithContext(_, Kind (TypeFunc _ | TypeName _ | TypeApply _)))), _ -> None, typeExpr
    | _ -> extractContextFromTypeExpr typeExpr

/// The errors for the operators `typeOpt`, a def's annotation, requires by their members rather than by the contexts
/// that have them (see `InlineContexts`), if any.
let internal checkMemberRequirements (state: InferState) (typeOpt: TypeExpr option) : Result<unit, Diagnostic list> =
    match typeOpt with
    | Some annotation -> InlineContexts.check typeExprToTy state annotation
    | None -> Ok ()

/// Whether `info` is a protected or private context with no members: no member says whether an instance of it is
/// there, so the environment holds it where one is (see `withSupplied`). A scoped one is the call's that supplies it
/// (see `ScopedCalls`), and `HostRuntime`, which the toolchain gives a host, is checked by `Hosts`.
let private isMemberless (info: TypeDefInfo) =
    info.IsContext && info.IsNominal && info.Members.IsEmpty && info.Parameters.IsEmpty && not info.IsScoped
    && info.Home <> StandardLibraryModule ModuleOrigin.hostRuntimeName

/// `env` where an instance of `info` is supplied, if it is a context with no members (#1479) or a scoped one, which
/// a `use` of its instance supplies, as `memory` does `Memory` (#2101): `TypeEnv.Supplied` holds it.
let private withSupplied (provider: ContextProvider) (info: TypeDefInfo) (env: TypeEnv) : TypeEnv =
    if isMemberless info || (info.IsContext && info.IsScoped) then { env with Supplied = env.Supplied.Add(TypeDefInfo.nominalId info, provider) } else env

/// The contexts with no members (see `isMemberless`) a signature supplies in `env`, as a lambda is given them (see
/// `ContextNames`).
let internal memberlessFromSignature (env: TypeEnv) (state: InferState) : TypeDefInfo list =
    env.Supplied
    |> Map.toList
    |> List.choose (fun (id, provider) ->
        match provider with
        | FromSignature _ -> tryFindNominalDef id state |> Option.filter isMemberless
        | _ -> None)

/// Whether the context `ctxName` is one with no members (see `isMemberless`) that the host provides, as `Async`: an
/// ambient context, as one whose members the host's prelude stands in for is, but known by its name alone.
let private isAmbientMemberless (state: InferState) (ctxName: Identifier) =
    state.HostContexts.Contains ctxName && tryFindTypeDef ctxName state |> Option.exists isMemberless

/// The module's ambient contexts: those whose members the host's prelude stands in for (see
/// `InferState.AmbientMembers`), and those with no members the host provides.
let internal ambientContexts (state: InferState) : Set<Identifier> =
    state.AmbientMembers
    |> Map.values
    |> Seq.map snd
    |> Set.ofSeq
    |> Set.union (state.HostContexts |> Set.filter (isAmbientMemberless state))

/// Whether something nearer than the host supplies the context `ctxName`, one with no members or a scoped one, in
/// `env`: a def that declares it, or a `use` (see `withSupplied`).
let private isSupplied (env: TypeEnv) (state: InferState) (ctxName: Identifier) =
    tryFindTypeDef ctxName state |> Option.exists (fun info -> env.Supplied.ContainsKey(TypeDefInfo.nominalId info))

/// `env` with the members of the instance a `use` of a value of type `ty` loads, each bound by its name. When the
/// type names a plain context, as `Foo(bar = ...)`'s does, each member has the symbol the context's definition
/// declared, as in a def that declares the context, so a lambda that calls it requires the context (see
/// `LambdaContexts.contextOfMember`). Such a member's type variables are its own, so each call instantiates them
/// afresh, as in a def that declares the context. Each member is provided by `provider`, the `use`.
let internal extendEnvWithRecordFields (provider: ContextProvider) (state: InferState) (env: TypeEnv) (ty: Ty) =
    let context =
        let plain (info: TypeDefInfo) = info.IsContext && info.Parameters.IsEmpty
        match resolveSoFar state ty with
        | TyNominal(id, _, _, _) ->
            tryFindNominalDef id state |> Option.filter plain
        | TyAlias(name, [], _)
        | TyApply(name, []) -> state.TypeDefs |> Map.tryFind name |> Option.filter plain
        | _ -> None
    let env = context |> Option.fold (fun acc info -> withSupplied provider info acc) env
    let addFields fields =
        fields
        |> Map.fold (fun acc name fieldTy ->
            let symbol = context |> Option.bind (fun info -> info.Members |> Map.tryFind name)
            let quantified = if context.IsSome then TypeEnv.freeTyVars fieldTy else Set.empty
            TypeEnv.extend name { TypeEnv.mono fieldTy with Quantified = quantified; Symbol = symbol; ProvidedBy = Some provider } acc) env
    match Unifier.unalias ty with
    | TyRecord(fields, _) -> addFields fields
    | RecursiveNominals.Nominal(_, _, underlying, _) ->
        match Unifier.unalias underlying with
        | TyRecord(fields, _) -> addFields fields
        | _ -> env
    | _ -> env

/// `tys` with each of their type variables but those in `keep` made a fresh one, the same one wherever it is. A fresh
/// variable is never one being replaced, since `Unifier.apply` follows a variable to what it is replaced by.
let private withFreshVars (keep: Set<int>) (state: InferState) (tys: Ty list) : Ty list * InferState =
    let replaced = Set.difference (tys |> List.map TypeEnv.freeTyVars |> Set.unionMany) keep
    let rec fresh current =
        match freshVar current with
        | TyVar var, next when replaced.Contains var.Id -> fresh next
        | made -> made
    let substitution, state =
        replaced
        |> Seq.fold (fun (substitution, current) id ->
            let var, next = fresh current
            substitution |> Map.add id var, next) (Map.empty, state)
    tys |> List.map (Unifier.apply substitution), state

/// The members of the context `ctxExpr` names, each with its type and its symbol, if the context declares one.
let rec private collectContextMembers (state: InferState) (ctxExpr: TypeExpr) : (Identifier * Ty * SymbolId option) list * InferState =
    match ctxExpr.Kind with
    | TypeRecord fields ->
        let mutable current = state
        let members =
            fields
            |> List.choose (function
                | TypeField(name, _, typeOpt, _) -> Some (name, typeOpt)
                | TypeFieldAttribute _ -> None)
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
        // imported context's own variables are another module's, and mean nothing in this one. So are the variables of
        // a member's own (`a` and `b` in `try: [c] (Box(a), ([c] a -> Box(b))) -> Box(b)`), which are numbered by the
        // module that declares the context and may stand for anything here, so each is made a fresh one.
        let appliedMembers (info: TypeDefInfo) =
            match Unifier.unalias info.Underlying with
            | TyRecord(fields, _) ->
                let argTys, state =
                    match ctxExpr.Kind with
                    | TypeApply(_, args) when args.Length = info.Parameters.Length -> args |> List.mapFold typeExprToTy state
                    | _ -> [], state
                let names, tys = fields |> Map.toList |> List.map (fun (memberName, ty) -> memberName, Unifier.applyToParameters info.Parameters argTys ty) |> List.unzip
                let tys, state = withFreshVars (argTys |> List.map TypeEnv.freeTyVars |> Set.unionMany) state tys
                Some (List.map2 (fun memberName ty -> memberName, ty, None) names tys |> withSymbols, state)
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
            | TyRecord(fields, _) ->
                fields
                |> Map.tryFind memberName
                |> Option.map (fun ty ->
                    let argTys, next = args |> List.mapFold typeExprToTy state
                    Unifier.applyToParameters info.Parameters argTys ty, next)
            | _ -> None
        | _ -> None
    | _ -> None

/// The parameterised context `ctxExpr` is, applied to its types, as a function type requires it: `ToText(int)`.
let internal contextTypeOf (state: InferState) (ctxExpr: TypeExpr) : (Ty * InferState) option =
    match ctxExpr.Kind with
    | TypeApply(name, args) ->
        match tryFindTypeDef name state with
        | Some info when info.IsContext && args.Length = info.Parameters.Length ->
            let argTys, next = args |> List.mapFold typeExprToTy state
            Some (TyApply(WrittenContexts.canonical state name, argTys), next)
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
        let supplied =
            match ctx.Kind with
            | TypeName name -> tryFindTypeDef name current |> Option.fold (fun acc info -> withSupplied (provider ctx) info acc) env'
            | _ -> env'
        env' <-
            members
            |> List.fold (fun acc (name, ty, symbol) ->
                let acc = TypeEnv.declare name symbol (memberScheme ty) acc
                match qualifier with
                | Some qualifier -> TypeEnv.declare $"{qualifier}.{name}" symbol (memberScheme ty) acc
                | None -> acc) supplied
    env', current

/// `env` with the members of each of `contexts`, the contexts a def's type declares, each provided by the signature.
let internal extendEnvWithContexts (env: TypeEnv) (state: InferState) (contexts: TypeExpr list) =
    extendEnvWithContextsFrom (fun ctx -> FromSignature ctx.Span) env state contexts

/// `env` with the instance of the parameterised context `info` at `argTys` in scope, provided by `provider`, as
/// `written`: its members, at those types, each by its name, and the instance, which a call of a function that
/// requires the context finds (see `ContextInstances`). A member's own type variables, those `argTys` do not fix, are
/// quantified, as in `extendEnvWithContextsFrom`.
let private extendEnvWithInstance (provider: ContextProvider) (env: TypeEnv) (name: Identifier) (info: TypeDefInfo) (argTys: Ty list) (written: TypeExpr) : TypeEnv =
    let fixedVars = argTys |> List.map TypeEnv.freeTyVars |> Set.unionMany
    let members =
        match Unifier.unalias info.Underlying with
        | TyRecord(fields, _) -> fields |> Map.toList |> List.map (fun (name, ty) -> name, Unifier.applyToParameters info.Parameters argTys ty)
        | _ -> []
    let env = TypeEnv.withInstance { Context = TyApply(name, argTys); Written = written; Provider = provider } env
    members
    |> List.fold (fun acc (memberName, ty) ->
        let scheme = { TypeEnv.mono ty with Quantified = Set.difference (TypeEnv.freeTyVars ty) fixedVars; ProvidedBy = Some provider }
        TypeEnv.declare memberName (info.Members |> Map.tryFind memberName) scheme acc) env

/// The contexts a lambda passed as a parameter of the type `paramTy` is given, as far as `state` says: each context the
/// type requires, by its name, with its definition and the types it is applied to, but for a context variable and a
/// scoped context (see `extendEnvForLambdaArgument`).
let private suppliedToLambda (state: InferState) (paramTy: Ty option) : (Identifier * TypeDefInfo * Ty list) list =
    match paramTy |> Option.map (resolveSoFar state >> Unifier.unalias) with
    | Some (TyFunc(_, _, contexts)) ->
        contexts
        |> List.choose (function
            | TyApply(name, args) ->
                match state.TypeDefs |> Map.tryFind name with
                | Some info when info.IsContext && info.Parameters.Length = args.Length && not (WrittenContexts.isScoped state name) ->
                    Some (name, info, args |> List.map (resolveSoFar state))
                | _ -> None
            | _ -> None)
    | _ -> []

/// The instances of parameterised contexts a lambda passed as a parameter of the type `paramTy` is given (see
/// `extendEnvForLambdaArgument`). The lambda's type requires them, as a def's requires the contexts it declares, so the
/// function it is passed to passes their members to it where it calls it.
let internal instancesSuppliedToLambda (state: InferState) (lambda: Expression) (paramTy: Ty option) : Ty list =
    if state.ScriptEntry = Some lambda.Id then []
    else suppliedToLambda state paramTy |> List.filter (fun (_, _, args) -> not args.IsEmpty) |> List.map (fun (name, _, args) -> TyApply(name, args))

/// `typedLambda`, the lambda `lambda` inferred, with `instances`, those it is given (see `instancesSuppliedToLambda`), in
/// its type, so that what it is passed to passes their members to it.
let internal withSuppliedInstances (instances: Ty list) (lambda: Expression) (typedLambda: TypedExpr, state: InferState) : TypedExpr * InferState =
    match instances, Unifier.unalias typedLambda.Type with
    | _ :: _, TyFunc(input, output, used) ->
        let ty = TyFunc(input, output, used @ instances |> List.distinct)
        { typedLambda with Type = ty }, { state with ExprTypes = state.ExprTypes |> Map.add lambda.Id ty }
    | _ -> typedLambda, state

/// `env` for the body of `lambda`, passed as a parameter of the type `paramTy`: with the members of each context that
/// type requires, as far as `state` says, so the body can call them and what requires them. The function the lambda is
/// passed to supplies them, as `withLog: [Console, c] ([Log, c] () -> ()) -> ()` supplies `Log` to its block, so the
/// lambda's type requires them and the code around it does not (see `LambdaContexts`). A parameterised context is
/// supplied at the types the parameter's type applies it to, as an instance a call in the body finds:
/// `withM: ([Monoid(int)] () -> b) -> b` gives its block `Monoid(int)`, as a handler. Each is provided by the type the
/// lambda is checked against, at the lambda. A script's statements (`InferState.ScriptEntry`) are not given them: they
/// are the top level, whose names they see, as the module's own defs, and whose ambient contexts they run in. A context
/// variable and a scoped context are not bound: a scoped context is the call's (see `ScopedCalls`). The contexts are
/// not written in the source, so nothing refers to them: `state` keeps only the variables reading their members made.
let internal extendEnvForLambdaArgument (env: TypeEnv) (state: InferState) (lambda: Expression) (paramTy: Ty option) : TypeEnv * InferState =
    let contexts = suppliedToLambda state paramTy
    match contexts with
    | [] -> env, state
    | _ when state.ScriptEntry = Some lambda.Id -> env, state
    | _ ->
        let provider = FromSignature lambda.Span
        let plain = contexts |> List.filter (fun (_, _, args) -> args.IsEmpty) |> List.map (fun (name, _, _) -> TypeExpr.synthetic (TypeName name))
        let env, next = extendEnvWithContextsFrom (fun _ -> provider) env state plain
        let env =
            contexts
            |> List.fold (fun env (name, info, args) ->
                let context = TyApply(name, args)
                match args, ContextInference.typeExprOf (ContextInference.varNames context) context with
                | _ :: _, Some written -> extendEnvWithInstance provider env name info args written
                | _ -> env) env
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
            let message = $"The signature of '{name}' names it, but it is not in scope" + StandardLibraryImports.hint state.StandardLibraryExports.Value ctxName
            Diagnostics.at DiagnosticCodes.UnknownContext (at |> Option.defaultValue span) message |> Diagnostics.about ctxName)
        |> Error

/// Whether each type variable a context in `typeOpt`, the annotation of `name`, is applied to is written in its type
/// too, as `a` in `[ToString(a)] a -> ()`. No call can say what a variable written only in the contexts stands for, so
/// none could find the context's instance. An annotation of only contexts is not checked: its type is inferred. A
/// context's associated types are not checked: its instance fixes them (see `AssociatedTypes`).
let internal ensureContextTypesDetermined (state: InferState) (name: Identifier) (typeOpt: TypeExpr option) : Result<unit, Diagnostic list> =
    match typeOpt with
    | Some (Kind (TypeWithContext(contexts, inner))) ->
        let associatedOf ctxName = tryFindTypeDef ctxName state |> Option.map _.AssociatedTypes.Length |> Option.defaultValue 0
        // An associated type of a context the type determines is determined too: its instance fixes it, as `elem` in
        // `[Iterable(c), ToString(elem)] c -> ()`.
        let written =
            let inType = annotationVarNames inner |> Set.ofList
            contexts
            |> List.collect (fun ctx ->
                match ctx.Kind with
                | TypeApply(ctxName, args) when associatedOf ctxName > 0 ->
                    let chosenBy = List.truncate (args.Length - associatedOf ctxName) args |> List.collect annotationVarNames
                    if chosenBy |> List.forall inType.Contains then List.skip (args.Length - associatedOf ctxName) args |> List.collect annotationVarNames
                    else []
                | _ -> [])
            |> Set.ofList
            |> Set.union inType
        let contextText ctxName (args: TypeExpr list) =
            let simple = args |> List.choose (fun arg -> match arg.Kind with TypeVar n | TypeName n -> Some n | _ -> None)
            if simple.Length = args.Length then $"""{ctxName}({String.concat ", " simple})""" else ctxName
        let errors =
            contexts
            |> List.collect (fun ctx ->
                match ctx.Kind with
                | TypeApply(ctxName, args) ->
                    let args = List.truncate (args.Length - associatedOf ctxName) args
                    args
                    |> List.collect annotationVarNames
                    |> List.distinct
                    |> List.filter (written.Contains >> not)
                    |> List.map (fun var ->
                        Diagnostics.at DiagnosticCodes.UndeterminedContextType ctx.Span
                            $"'{var}' in '{contextText ctxName args}' is not the type of any parameter of '{name}' or of its result, so no call can supply '{contextText ctxName args}'"
                        |> Diagnostics.about ctxName)
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
    let memberless =
        state.HostContexts
        |> Set.filter (fun ctxName ->
            isAmbientMemberless state ctxName && (state.ContextScope = AmbientScope || isSupplied env state ctxName))
    let fromMembers = Set.union fromMembers memberless
    match state.ContextScope with
    | AmbientScope -> Set.union fromMembers state.CarriedAmbientContexts
    | DefScope _
    | ValueScope _
    | ScriptValueScope _ -> fromMembers

/// Whether the context `ctxName` is available to code in `env`: something nearer than the host supplies it, a def that
/// declares it or a `use` of it, which binds its members or, for one with none, supplies it (see `withSupplied`); or
/// it is an ambient context, which the host provides.
let internal isAvailable (env: TypeEnv) (state: InferState) (ctxName: Identifier) =
    match tryFindTypeDef ctxName state with
    | Some info when info.IsContext ->
        isSupplied env state ctxName
        || ambientContexts state |> Set.contains ctxName
        || info.Members
           |> Map.exists (fun memberName symbol ->
               TypeEnv.tryFind memberName env |> Option.exists (fun scheme -> scheme.Symbol = Some symbol))
    | _ -> false

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
        |> List.append (state.HostContexts |> Set.filter (fun ctxName -> isAmbientMemberless state ctxName && not (isSupplied env state ctxName)) |> Set.toList)
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
/// declare it. A def whose type says it requires none, with `[]`, said so on purpose, so it isn't told to add one. A custom
/// pattern can't require one at all.
let internal undeclaredContext (state: InferState) (defName: Identifier) (ctxName: Identifier) (what: string) (span: SourceSpan) =
    match state.WrittenTypes |> Map.tryFind defName with
    | _ when PatternPurity.isPattern defName -> PatternPurity.requires what ctxName span
    | Some { Kind = TypeWithContext([], _) } ->
        Diagnostics.at DiagnosticCodes.UndeclaredContext span
            $"{what} the context '{ctxName}', but the type of '{defName}' says with [] that it requires no contexts: load an instance with `use`, or call something that requires none"
        |> Diagnostics.about ctxName
    | _ ->
        Diagnostics.at DiagnosticCodes.UndeclaredContext span
            $"{what} the context '{ctxName}', which '{defName}' does not declare: add [{ctxName}] to the type of '{defName}', or load an instance with `use`"
        |> Diagnostics.about ctxName

/// `what` (which ends "requires", say) the context `ctxName` at `span`, in the value `valueName` of a named module,
/// which cannot have one.
let internal contextInValue (valueName: Identifier) (ctxName: Identifier) (what: string) (span: SourceSpan) =
    Diagnostics.at DiagnosticCodes.UndeclaredContext span $"{what} the context '{ctxName}', which '{valueName}' cannot have: a named module's top-level values are built without contexts, so make '{valueName}' a function"
    |> Diagnostics.about ctxName

/// The error for naming `valueName` at `span`, a value the script computes as it runs (see
/// `InferState.ScriptEntryValues`), where it is not computed yet: in a function (`inFunction`), or before its def.
let internal scriptRunValue (valueName: Identifier) (inFunction: bool) (span: SourceSpan) =
    let message =
        if inFunction then $"'{valueName}' is computed when the script runs, so a function can't name it: pass it as an argument"
        else $"'{valueName}' is computed when the script runs, so it can't be read before its def: move the def up"
    Diagnostics.at DiagnosticCodes.ScriptRunValue span message

/// The error for the value `valueName` of a module without a `module` line, declared at `span`, that needs the context
/// `ctxName` to be computed, where the module has no statements for it to run among: nothing at the top level supplies
/// a context, only the host, to a script's statements (see `Scripts`).
let internal valueWithoutEntry (valueName: Identifier) (ctxName: Identifier) (span: SourceSpan) =
    Diagnostics.at DiagnosticCodes.UndeclaredContext span
        $"'{valueName}' needs the context '{ctxName}' to be computed, which only a script's statements run in: compute it in a statement, or make '{valueName}' a function"
    |> Diagnostics.about ctxName

/// `infer` run on `state`, but at the top level's ambient contexts where `state` is in a top-level value, a named
/// module's or a script's: for a lambda written in the value, which building the value does not run, so the contexts
/// it requires are its function type's, not the value's (#1059). One passed to a call is not inferred this way, since
/// the call may run it.
let internal outsideValue (infer: InferState -> Result<'T * InferState, Diagnostic list>) (state: InferState) =
    match state.ContextScope with
    | ValueScope _
    | ScriptValueScope _ as valueScope ->
        infer { state with ContextScope = AmbientScope } |> Result.map (fun (typed, next) -> typed, { next with ContextScope = valueScope })
    | _ -> infer state

/// `infer` run on `state` for a lambda given to a constructor as the field of type `fieldTy`, which the constructor does
/// not run: outside a top-level value, as `outsideValue` does, and in a function's body too where the field's type
/// requires contexts, as a field of a type generic in contexts does (`get: [k] () -> a`). Such a lambda requires what
/// it uses where the field is called, and the value made carries it, not the function that makes it (#1676).
let internal asConstructorField (fieldTy: Ty) (infer: InferState -> Result<'T * InferState, Diagnostic list>) (state: InferState) =
    match state.ContextScope, Unifier.unalias (resolveSoFar state fieldTy) with
    | DefScope _ as defScope, TyFunc(_, _, _ :: _) ->
        infer { state with ContextScope = AmbientScope } |> Result.map (fun (typed, next) -> typed, { next with ContextScope = defScope })
    | _ -> outsideValue infer state

/// `infer` run on `state` for a lambda given to a call for the parameter of type `paramTy`, where the call's result is
/// `resultTy` and `ownContexts` are what the callee's own context variables stand for at the call. Where the
/// parameter's contexts are only variables that the result carries and the callee does not declare, as `d` in
/// `seq.map: ([c] seq(a)), ([d] a -> b) -> [c, d] seq(b)`, the call does not run the lambda: the value it gives carries
/// what the lambda requires, as a constructor's field does (see `asConstructorField`), so the lambda is inferred
/// outside the def or value around the call (#1738).
let internal asCarriedArgument (ownContexts: Ty list) (resultTy: Ty) (paramTy: Ty) (infer: InferState -> Result<'T * InferState, Diagnostic list>) (state: InferState) =
    let carried = TypeEnv.freeTyVars (resolveSoFar state resultTy)
    let own = ownContexts |> List.map TypeEnv.freeTyVars |> Set.unionMany
    let isCarried context =
        match context with
        | TyVar v -> carried.Contains v.Id && not (own.Contains v.Id)
        | _ -> false
    match state.ContextScope, Unifier.unalias (resolveSoFar state paramTy) with
    | (DefScope _ | ValueScope _ | ScriptValueScope _ as scope), TyFunc(_, _, (_ :: _ as contexts)) when contexts |> List.forall isCarried ->
        infer { state with ContextScope = AmbientScope } |> Result.map (fun (typed, next) -> typed, { next with ContextScope = scope })
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

/// Whether `name` is, where `env` is the environment, the member of a context an instance provides: a def's declared
/// context's, or a `use`'s.
let private isProvidedMember (env: TypeEnv) (name: Identifier) =
    TypeEnv.tryFind name env |> Option.exists (fun scheme -> scheme.ProvidedBy.IsSome)

/// The contexts the function `name` names where `env` is the environment requires, as written or inferred. A local
/// def's are its own, by its symbol, and a parameter, a pattern's variable or a context's member has none: none takes
/// those of a top-level or imported def it shadows, which `ContextRequirements` holds under the same name.
let internal contextRequirementsOf (env: TypeEnv) (state: InferState) (name: Identifier) : TypeExpr list option =
    match symbolNamed env state name with
    | Some symbol when symbol.Kind = LocalValue -> state.LocalContextRequirements |> Map.tryFind symbol.Id
    | Some symbol when symbol.Kind = Parameter || symbol.Kind = PatternVariable -> None
    | _ when isProvidedMember env name -> None
    | _ -> state.ContextRequirements |> Map.tryFind (canonicalName env state name)

/// The type the function `name` names where `env` is the environment declares without its contexts, where it writes
/// one, read as `contextRequirementsOf` reads its contexts.
let internal contextSignatureOf (env: TypeEnv) (state: InferState) (name: Identifier) : TypeExpr option =
    match symbolNamed env state name with
    | Some symbol when symbol.Kind = LocalValue -> state.LocalContextSignatures |> Map.tryFind symbol.Id
    | Some symbol when symbol.Kind = Parameter || symbol.Kind = PatternVariable -> None
    | _ when isProvidedMember env name -> None
    | _ -> state.ContextSignatures |> Map.tryFind (canonicalName env state name)

/// The ambient contexts the unannotated top-level function `name`, as `env` binds it, was inferred to require, once its
/// body has been inferred: those it uses itself, and those of the unannotated functions it calls, as far as they have
/// been inferred too. Defs are inferred in the order they use each other (see `DefinitionOrder`), so only a function
/// in a cycle with the one naming it is not yet. `None` where `name` is not such a function, or is not inferred yet.
///
/// A function's contexts are its own and those of each function it calls, less those available where it calls it. They
/// are found once for each function `name` reaches, however many paths reach it, by going over the functions until
/// none gains a context: a function in a cycle gains its callers' through it (#1458).
let internal inferredAmbientContexts (env: TypeEnv) (state: InferState) (name: Identifier) : Set<Identifier> option =
    match state.UnannotatedDefs |> Map.tryFind name, TypeEnv.tryFind name env with
    | Some symbol, Some scheme when scheme.Symbol = Some symbol && (match scheme.Type with TyVar _ -> false | _ -> true) ->
        let usesBy =
            state.DeferredContextUses
            |> List.choose (fun used ->
                match used.Caller with
                | DefScope(caller, true) -> Some (caller, used)
                | _ -> None)
            |> List.groupBy fst
            |> List.map (fun (caller, uses) -> caller, List.map snd uses)
            |> Map.ofList
        let usesOf (defName: Identifier) = usesBy |> Map.tryFind defName |> Option.defaultValue []
        // The functions `name` reaches, each after those it calls, but in a cycle.
        let reached = System.Collections.Generic.List<Identifier>()
        let seen = System.Collections.Generic.HashSet<Identifier>()
        let rec visit (defName: Identifier) =
            if seen.Add defName then
                for used in usesOf defName do
                    visit used.Callee
                reached.Add defName
        visit name
        let contexts = System.Collections.Generic.Dictionary<Identifier, Set<Identifier>>()
        for defName in reached do
            contexts[defName] <- state.InferredContexts |> Map.tryFind defName |> Option.defaultValue Set.empty
        let mutable changed = true
        while changed do
            changed <- false
            for defName in reached do
                let found =
                    usesOf defName
                    |> List.fold (fun acc used -> Set.union acc (Set.difference contexts[used.Callee] used.Available)) contexts[defName]
                if found <> contexts[defName] then
                    contexts[defName] <- found
                    changed <- true
        Some contexts[name]
    | _ -> None

/// The contexts a call of the function `name`, as `env` binds it, requires where it runs, as its type writes them: those
/// its type writes, and, for an unannotated top-level function already inferred, the ambient contexts it was inferred to
/// require (see `inferredAmbientContexts`). A lambda that calls it uses them as it uses those of a function that writes
/// its type, so it is typed alike either way, and a lambda that calls a function that waits has a type that waits.
let internal contextsRequiredByCall (env: TypeEnv) (state: InferState) (name: Identifier) : TypeExpr list =
    let written = contextRequirementsOf env state name |> Option.defaultValue []
    let inferred =
        inferredAmbientContexts env state (canonicalName env state name)
        |> Option.map (Set.toList >> List.map (TypeName >> TypeExpr.synthetic))
        |> Option.defaultValue []
    written @ inferred

/// The context variable a lambda uses for its call of `name` where `name` is an unannotated top-level def not inferred
/// yet, as it is in a cycle with the def the lambda is in, and `state` with it noted, to bind once the module is
/// inferred (`InferState.PendingCallContexts`), where a conflict is reported at the call's `span`. `None` for any other call, and outside a lambda, where the def around
/// the call requires the callee's contexts already (see `DeferredContextUse`).
let internal pendingCallContexts (env: TypeEnv) (state: InferState) (name: Identifier) (span: SourceSpan) : (Ty * InferState) option =
    let canonical = canonicalName env state name
    match state.UnannotatedDefs |> Map.tryFind canonical, TypeEnv.tryFind name env with
    | Some symbol, Some scheme when scheme.Symbol = Some symbol && LambdaContexts.withinLambda state && (inferredAmbientContexts env state canonical).IsNone ->
        let var, next = freshVar state
        Some (var, { next with PendingCallContexts = (canonical, var, span) :: next.PendingCallContexts })
    | _ -> None

/// Checks the context use of naming `name` at `span`: a member of the ambient instance needs its context, and a use
/// of an unannotated top-level def, or of a script's value, is kept to check once its contexts are known. Where the use
/// is the call `call`, what provides the context of a member it calls is recorded, as is, once known, what provides
/// each context of the def.
let private checkMemberContextUse (call: NodeId option) (env: TypeEnv) (state: InferState) (name: Identifier) (span: SourceSpan) : Result<InferState, Diagnostic list> =
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

/// The private context whose member `name`, as `env` binds it, is, by the name `state` knows the context by, where code
/// at `span` is outside the context's home module: only that module can call a private context's members (#1449).
let internal privateContextOutsideHome (env: TypeEnv) (state: InferState) (name: Identifier) (span: SourceSpan) : Identifier option =
    match TypeEnv.tryFind name env |> Option.bind _.Symbol with
    | Some symbol ->
        state.TypeDefs
        |> Map.tryPick (fun ctxName info ->
            if info.IsContext && info.IsPrivate && info.Members |> Map.exists (fun _ id -> id = symbol) then Some (ctxName, info)
            else None)
        |> Option.filter (fun (_, info) -> not (Visibility.inNominalHome state (TypeDefInfo.nominalId info) span))
        |> Option.map fst
    | None -> None

/// Checks the context use of naming `name` at `span` (see `checkMemberContextUse`), and that `name` is not a member of
/// a private context named outside its home module.
let internal checkContextUse (call: NodeId option) (env: TypeEnv) (state: InferState) (name: Identifier) (span: SourceSpan) : Result<InferState, Diagnostic list> =
    match privateContextOutsideHome env state name span with
    | Some ctxName ->
        let message =
            $"'{name}' is a member of the private context '{ctxName}', which only its own module can call: call a function that module exports instead"
        Error [ Diagnostics.at DiagnosticCodes.PrivateContextMember span message |> Diagnostics.about name ]
    | None -> checkMemberContextUse call env state name span

/// Checks that `contexts`, which `name` requires, are available at `span`. A context with a member not in scope at all,
/// or a scoped context nothing supplies, is an error only when `reportMissing`. Where they are required by the call `call`, what provides each that is not
/// parameterised is recorded. `ctxText` writes a parameterised context with the types the call has it at, as
/// `ToString(Time(int))`, for an error to name it by.
let internal ensureContextAvailable (call: NodeId option) (reportMissing: bool) (env: TypeEnv) (state: InferState) (name: Identifier) (contexts: TypeExpr list) (ctxText: string option) (span: SourceSpan) : Result<InferState, Diagnostic list> =
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
    // A context with no members the host provides is needed from the host wherever nothing nearer supplies it.
    let fromHost (ctx: TypeExpr) =
        match ctx.Kind with
        | TypeName ctxName -> isAmbientMemberless state ctxName && not (isSupplied env state ctxName)
        | _ -> false
    let recordProviders (current: InferState) =
        match call with
        | Some call ->
            requiredMembers
            |> List.fold (fun current (ctx, members) ->
                match ctx.Kind, providerOf env current (fromAmbient ctx members || fromHost ctx) members with
                | TypeName ctxName, Some provider -> recordProvider call ctxName provider current
                | _ -> current) current
        | None -> current
    // A scoped context is available where something supplies it, whether or not its members are in scope (see
    // `unsupplied` below).
    let isScopedContext (ctx: TypeExpr) =
        match ctx.Kind with
        | TypeName ctxName -> WrittenContexts.isScoped state ctxName
        | _ -> false
    // The members of `ctx`, a context the module declares itself, that are in scope only as the host's ambient
    // instance's: those of a prelude context the module's own shadows, of the same name and members. The host provides
    // the prelude's context, never the module's own, so they don't supply it (#2168).
    let ambientOfAnother (ctx: TypeExpr) =
        match ctx.Kind with
        | TypeName ctxName
        | TypeApply(ctxName, _) when tryFindTypeDef ctxName state |> Option.exists (fun info -> info.Home = state.Home) ->
            let members, _ = collectContextMembers state ctx
            members |> List.map (fun (memberName, _, _) -> memberName) |> List.filter (isAmbientMember env state)
        | _ -> []
    // Each context with a member not in scope at all, or in scope only as another context's ambient member, with those
    // members, and each with no members that nothing supplies (see `withSupplied`).
    let missing =
        requiredMembers
        |> List.filter (fun (ctx, _) -> not (isCarried ctx) && not (fromHost ctx) && not (isScopedContext ctx))
        |> List.choose (fun (ctx, members) ->
            let unsupplied =
                match ctx.Kind with
                | TypeName ctxName ->
                    tryFindTypeDef ctxName state
                    |> Option.exists (fun info -> isMemberless info && not (env.Supplied.ContainsKey(TypeDefInfo.nominalId info)))
                | _ -> false
            let others = ambientOfAnother ctx
            match members |> List.filter (fun memberName -> TypeEnv.tryFind memberName env |> Option.isNone || List.contains memberName others) with
            | [] when unsupplied -> Some (ctx, [])
            | [] -> None
            | absent -> Some (ctx, absent))
    // A protected or private context matches only itself: each of its members in scope must be its own, not another
    // context's of the same name (#1443). A plain one matches any context with its members.
    let forged =
        contexts
        |> List.choose (fun ctx ->
            match ctx.Kind with
            | TypeName ctxName ->
                match state.TypeDefs |> Map.tryFind ctxName with
                | Some info when info.IsContext && info.IsNominal ->
                    let members, _ = collectContextMembers state ctx
                    let isOther (memberName, _, symbol: SymbolId option) =
                        match TypeEnv.tryFind (inScopeAs ctx memberName) env with
                        | Some scheme -> symbol.IsSome && scheme.Symbol <> symbol
                        | None -> false
                    if members |> List.exists isOther then Some (ctxName, info) else None
                | _ -> None
            | _ -> None)
    // A scoped context has no members to find: it is available where something supplies it (see `ScopedCalls`), or
    // where a `use` loads an instance of it.
    let unsupplied =
        contexts
        |> List.choose (fun ctx ->
            match ctx.Kind with
            | TypeName ctxName when WrittenContexts.isScoped state ctxName && not (isSupplied env state ctxName) ->
                ScopedCalls.unsupplied state name ctxName span
            | _ -> None)
    // The contexts only the host could provide, which it does not (see `InferState.UnprovidedContexts`).
    let unprovided =
        contexts
        |> List.choose (fun ctx ->
            match ctx.Kind with
            | TypeName ctxName
            | TypeApply(ctxName, _) -> state.UnprovidedContexts |> Map.tryFind ctxName |> Option.map (fun hostImport -> ctxName, hostImport)
            | _ -> None)
        |> List.distinct
    if reportMissing && not unprovided.IsEmpty then
        let hostImport = snd unprovided.Head
        let contextsText =
            match unprovided |> List.map (fun (ctxName, _) -> $"'{ctxName}'") with
            | [ single ] -> $"the context {single}"
            | names -> $"the contexts {Diagnostics.listed names}"
        let diagnostic = Diagnostics.at DiagnosticCodes.MissingContextMembers span $"'{name}' requires {contextsText}, which the host '{hostImport}' does not provide"
        match unprovided with
        | [ ctxName, _ ] -> Error [ diagnostic |> Diagnostics.about ctxName ]
        | _ -> Error [ diagnostic ]
    elif reportMissing && not unsupplied.IsEmpty then
        Error [ unsupplied.Head ]
    elif reportMissing && not forged.IsEmpty then
        let ctxName, info = forged.Head
        let kind = if info.IsPrivate then "private" else "protected"
        let message =
            $"'{name}' requires the context '{ctxName}', which is not available here: '{ctxName}' is {kind}, so only an instance its own module makes serves for it, not another context with the same members"
        Error [ Diagnostics.at DiagnosticCodes.MissingContextMembers span message |> Diagnostics.about ctxName ]
    elif missing.IsEmpty || not reportMissing then
        // Every member is in scope, but one the ambient instance supplies is only available where its context is.
        requiredMembers
        |> List.fold (fun result (ctx, members) ->
            result
            |> Result.bind (fun current ->
                match ctx.Kind, members |> List.exists (isAmbientMember env current) with
                | (TypeName ctxName | TypeApply(ctxName, _)), _ when isCarried ctx && not (suppliedNearer env current members) ->
                    needAmbientContext current ctxName $"'{name}' requires" span
                | TypeName ctxName, _ when fromHost ctx -> needAmbientContext current ctxName $"'{name}' requires" span
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
        let namedContexts =
            named
            |> List.choose (fun (ctx, _) ->
                match ctx.Kind, ctxText with
                | TypeApply _, Some written -> Some written
                | (TypeName ctxName | TypeApply(ctxName, _)), _ -> Some ctxName
                | _ -> None)
            |> List.distinct
        let contextNames = quoted namedContexts
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
        match namedContexts, memberNames with
        | [ single ], [] -> Error [ diagnostic |> Diagnostics.about single ]
        | _ -> Error [ diagnostic ]

/// What it takes for the operator `expr` to call `callee`, the definition attached to its operand's type: the
/// contexts its type requires, `Memory` for `` MList.`+=` ``, are required where the operator is written, as a call to it
/// would require them.
let internal requireOperatorContexts (env: TypeEnv) (state: InferState) (expr: Expression) (callee: Identifier) (span: SourceSpan) : Result<InferState, Diagnostic list> =
    match state.CalleeTypes |> Map.tryFind expr.Id |> Option.map Unifier.unalias with
    | Some (TyFunc(_, _, contexts)) when not contexts.IsEmpty ->
        let used = ContextVariables.resolved (resolveSoFar state) contexts
        let requirePlain current ctx = ensureContextAvailable (Some expr.Id) true env current callee [ ctx ] None span
        ContextVariables.require requirePlain callee span used (LambdaContexts.recordUnsupplied env used state)
    | _ -> Ok state
