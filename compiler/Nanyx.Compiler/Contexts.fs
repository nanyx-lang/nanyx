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
/// `LambdaContexts.contextOfMember`).
let internal extendEnvWithRecordFields (state: InferState) (env: TypeEnv) (ty: Ty) =
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
            TypeEnv.extend name { TypeEnv.mono fieldTy with Symbol = symbol } acc) env
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
                | TypeField(name, _, _, typeOpt, _) -> Some (name, typeOpt)
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
        match state.ContextDefs |> Map.tryFind name with
        | Some ctxDef ->
            let members, next = collectContextMembers state ctxDef
            withSymbols members, next
        | None ->
            // Also check TypeDefs for non-context types used as contexts
            match infoOpt with
            | Some info ->
                match Unifier.unalias info.Underlying with
                | TyRecord fields ->
                    // The context's parameters are the types it is applied to here: an imported context's own
                    // variables are another module's, and mean nothing in this one.
                    let argTys, state =
                        match ctxExpr.Kind with
                        | TypeApply(_, args) when args.Length = info.Parameters.Length -> args |> List.mapFold typeExprToTy state
                        | _ -> [], state
                    fields |> Map.toList |> List.map (fun (memberName, ty) -> memberName, Unifier.applyToParameters info.Parameters argTys ty, None) |> withSymbols, state
                | _ -> [], state
            | None -> [], state
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

let internal extendEnvWithContexts (env: TypeEnv) (state: InferState) (contexts: TypeExpr list) =
    let mutable current = state
    let mutable env' = env
    for ctx in contexts do
        let members, next = collectContextMembers current ctx
        current <- next
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
                let acc = TypeEnv.declare name symbol (TypeEnv.mono ty) acc
                match qualifier with
                | Some qualifier -> TypeEnv.declare $"{qualifier}.{name}" symbol (TypeEnv.mono ty) acc
                | None -> acc) env'
    env', current

let rec private unknownContextNames (state: InferState) (ctxExpr: TypeExpr) : Identifier list =
    match ctxExpr.Kind with
    | TypeContext contexts -> contexts |> List.collect (unknownContextNames state)
    | TypeName name
    | TypeApply(name, _) ->
        if state.ContextDefs.ContainsKey name || (tryFindTypeDef name state).IsSome then [] else [ name ]
    | _ -> []

/// Whether every context in the signature of `name` exists; `span` is where the definition's name is written.
let internal ensureContextsExist (state: InferState) (name: Identifier) (span: SourceSpan) (contexts: TypeExpr list) : Result<unit, Diagnostic list> =
    match contexts |> List.collect (unknownContextNames state) |> List.distinct with
    | [] -> Ok ()
    | unknown ->
        unknown
        |> List.map (fun ctxName -> Diagnostics.at DiagnosticCodes.UnknownContext span $"Unknown context '{ctxName}' in the signature of '{name}'")
        |> Error

/// Whether `name` is bound in `env` to the ambient instance's member, not to something nearer that shadows it: a
/// member of a context the def declares, or of an instance a `use` loads.
let private isAmbientMember (env: TypeEnv) (state: InferState) (name: Identifier) =
    match state.AmbientMembers |> Map.tryFind name, TypeEnv.tryFind name env with
    | Some (symbol, _), Some scheme -> scheme.Symbol = Some symbol
    | _ -> false

/// The ambient contexts that code in `env` runs in: every one at the top level, else those something nearer
/// supplies every member of.
let private availableAmbientContexts (env: TypeEnv) (state: InferState) : Set<Identifier> =
    let membersByContext =
        state.AmbientMembers
        |> Map.toList
        |> List.groupBy (fun (_, (_, ctxName)) -> ctxName)
    let fromMembers =
        membersByContext
        |> List.filter (fun (_, members) ->
            match state.ContextScope with
            | AmbientScope -> true
            | DefScope _ ->
                members |> List.forall (fun (memberName, _) -> TypeEnv.tryFind memberName env |> Option.isSome && not (isAmbientMember env state memberName)))
        |> List.map fst
        |> Set.ofList
    match state.ContextScope with
    | AmbientScope -> Set.union fromMembers state.CarriedAmbientContexts
    | DefScope _ -> fromMembers

/// `what` (which ends "requires", say) the context `ctxName` at `span`, in the def `defName`, whose type does not
/// declare it.
let internal undeclaredContext (defName: Identifier) (ctxName: Identifier) (what: string) (span: SourceSpan) =
    Diagnostics.at DiagnosticCodes.UndeclaredContext span $"{what} the context '{ctxName}', which '{defName}' does not declare: add [{ctxName}] to the type of '{defName}', or load an instance with `use`"

/// `what` needs the ambient context `ctxName` where nothing nearer supplies it: fine at the top level, inferred for
/// an unannotated def, and an error in a def whose type does not declare it.
let private needAmbientContext (state: InferState) (ctxName: Identifier) (what: string) (span: SourceSpan) : Result<InferState, Diagnostic list> =
    match state.ContextScope with
    | AmbientScope -> Ok state
    | DefScope(defName, true) ->
        let contexts = state.InferredContexts |> Map.tryFind defName |> Option.defaultValue Set.empty
        Ok { state with InferredContexts = state.InferredContexts |> Map.add defName (contexts.Add ctxName) }
    | DefScope(defName, false) -> Error [ undeclaredContext defName ctxName what span |> ContextFixes.withAddToType state defName ctxName ]

/// The name the definition `name` names is declared under: a block import's name (`zero`) stands for the qualified
/// one (`money.zero`), unless something nearer binds it.
let internal canonicalName (env: TypeEnv) (state: InferState) (name: Identifier) =
    match state.BlockAliases |> Map.tryFind name with
    | Some qualified when (TypeEnv.tryFind name env |> Option.bind _.Symbol) = (TypeEnv.tryFind qualified env |> Option.bind _.Symbol) -> qualified
    | _ -> name

/// Checks the context use of naming `name` at `span`: a member of the ambient instance needs its context, and a use
/// of an unannotated top-level def is kept to check once its contexts are known.
let internal checkContextUse (env: TypeEnv) (state: InferState) (name: Identifier) (span: SourceSpan) : Result<InferState, Diagnostic list> =
    if isAmbientMember env state name then
        let _, ctxName = state.AmbientMembers.[name]
        needAmbientContext state ctxName $"'{name}' is a member of" span
    else
        match state.UnannotatedDefs |> Map.tryFind name, TypeEnv.tryFind name env with
        | Some symbol, Some scheme when scheme.Symbol = Some symbol && not (state.AmbientMembers.IsEmpty && state.CarriedAmbientContexts.IsEmpty) ->
            let deferred = { Caller = state.ContextScope; Callee = name; Span = span; Available = availableAmbientContexts env state }
            Ok { state with DeferredContextUses = deferred :: state.DeferredContextUses }
        | _ -> Ok state

/// Checks that `contexts`, which `name` requires, are available at `span`. A context with a member not in scope at all
/// is an error only when `reportMissing`.
let internal ensureContextAvailable (reportMissing: bool) (env: TypeEnv) (state: InferState) (name: Identifier) (contexts: TypeExpr list) (span: SourceSpan) : Result<InferState, Diagnostic list> =
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
    let suppliedNearer members =
        members |> List.forall (fun memberName -> TypeEnv.tryFind memberName env |> Option.isSome && not (isAmbientMember env state memberName))
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
                | (TypeName ctxName | TypeApply(ctxName, _)), _ when isCarried ctx && not (suppliedNearer members) ->
                    needAmbientContext current ctxName $"'{name}' requires" span
                | (TypeName ctxName | TypeApply(ctxName, _)), true -> needAmbientContext current ctxName $"'{name}' requires" span
                | _ -> Ok current)) (Ok state)
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
