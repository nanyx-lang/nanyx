/// `context l` before a lambda's parameters: a name for the instance of a context the lambda is given, which only
/// `use l` can do anything with (see docs/src/content/advanced/contexts.md#naming-the-instance-a-lambda-is-given).
module internal NyxCompiler.ContextNames

open Parser.Program
open NyxCompiler.InferState

/// A context a lambda's body is given: its name, as a type names it, its definition, the type a name for it has, and
/// what `use` of the name puts back (see `ContextName`).
type private Given =
    { Name: Identifier
      Info: TypeDefInfo
      Ty: Ty
      Scope: Ty option
      Instance: InstanceBinding option
      Supplied: (NominalId * ContextProvider) option }

/// The contexts a lambda's body is given where `env` is its environment, each as a signature (the def's, or the type
/// the lambda is checked against) gives it: each plain context with members whose members are all bound there as its
/// own, each instance of a parameterised context, each context with no members, and `Memory` in a `memory` block. One
/// a `use` loads is the code around the lambda's, not the lambda's.
let private given (env: TypeEnv) (state: InferState) : Given list =
    let isGiven (info: TypeDefInfo) =
        info.IsContext
        && info.Parameters.IsEmpty
        && not info.Members.IsEmpty
        && info.Members
           |> Map.forall (fun memberName symbol ->
               match TypeEnv.tryFind memberName env with
               | Some { Symbol = Some bound; ProvidedBy = Some (FromSignature _) } -> bound = symbol
               | _ -> false)
    let plain =
        state.TypeDefs
        |> Map.toList
        |> List.filter (snd >> isGiven)
        // A context imported under an alias is in `TypeDefs` by both names: the one it is declared under is its own.
        |> List.groupBy (fun (_, info) -> info.Symbol)
        |> List.map (fun (_, named) ->
            let name, info = named |> List.tryFind (fun (name, info) -> name = info.Name) |> Option.defaultValue named.Head
            { Name = name; Info = info; Ty = TyApply(name, []); Scope = None; Instance = None; Supplied = None })
    // Each instance of a parameterised context the signature gives, `Show(int)`, in the order it writes them: the
    // environment has them nearest first.
    let instances =
        env.Instances
        |> List.rev
        |> List.filter (fun instance -> match instance.Provider with FromSignature _ -> true | _ -> false)
        |> List.distinctBy _.Context
        |> List.choose (fun instance ->
            match instance.Context with
            | TyApply(name, args) ->
                state.TypeDefs
                |> Map.tryFind name
                |> Option.map (fun info -> { Name = name; Info = info; Ty = TyApply(name, args); Scope = None; Instance = Some instance; Supplied = None })
            | _ -> None)
    let memberless =
        Contexts.memberlessFromSignature env state
        |> List.map (fun info ->
            let supplied = TypeDefInfo.nominalId info
            { Name = info.Name; Info = info; Ty = TyApply(info.Name, []); Scope = None; Instance = None; Supplied = env.Supplied |> Map.tryFind supplied |> Option.map (fun provider -> supplied, provider) })
    let memory =
        match MemoryCells.blockScope env, state.TypeDefs.TryFind MemoryCells.context with
        | Some scope, Some info ->
            [ { Name = MemoryCells.context; Info = info; Ty = FunctionContexts.scoped MemoryCells.context scope; Scope = Some scope; Instance = None; Supplied = None } ]
        | _ -> []
    plain @ instances @ memberless @ memory

/// `env` and `state` for the body of `lambda`, `body`, with each of `names` (from `context l` or `context l: Log`)
/// bound to the instance of a context the lambda is given. A name binds the members of its context as `env` has them,
/// to load with `use`. A name for a context the lambda isn't given, or one that could name more than one, is an error.
let bind (env: TypeEnv) (state: InferState) (lambda: Expression) (body: Expression) (names: (Identifier * TypeExpr option * SourceSpan) list) : Result<TypeEnv * InferState, Diagnostic list> =
    let contexts = given env state
    let shown (context: Given) = Unifier.typeText state.TypeDefs context.Ty
    let quoted = contexts |> List.map (fun context -> $"'{shown context}'")
    // The given contexts `written` names: by the context's name, and for a parameterised one, the types it is for.
    let namedBy (written: TypeExpr) =
        let isContext (ctxName: Identifier) (context: Given) =
            state.TypeDefs |> Map.tryFind ctxName |> Option.exists (fun named -> named.Symbol = context.Info.Symbol)
        match written.Kind with
        | TypeName ctxName -> Some (ctxName, contexts |> List.filter (isContext ctxName))
        | TypeApply(ctxName, args) ->
            let argTys, _ = args |> List.mapFold TypeResolution.typeExprToTy state
            Some (ctxName, contexts |> List.filter (fun context -> isContext ctxName context && context.Ty = TyApply(context.Name, argTys)))
        | _ -> None
    let named (name: Identifier) (written: TypeExpr option) (span: SourceSpan) =
        match written |> Option.map (fun written -> written, namedBy written) with
        | Some (_, Some (_, [ found ])) -> Ok found
        | Some (written, Some (_, found :: _ :: _)) ->
            Error [ Diagnostics.at DiagnosticCodes.UnknownContextName written.Span
                        $"'{name}' could name any of the instances of '{found.Name}' this lambda is given: say which, as in `context {name}: {shown found}`" |> Diagnostics.about name ]
        | Some (_, Some (ctxName, [])) when contexts.IsEmpty ->
            Error [ Diagnostics.at DiagnosticCodes.UnknownContextName span $"'{name}' names the context '{ctxName}', but this lambda is given no context to name" |> Diagnostics.about name ]
        | Some (_, Some (ctxName, [])) ->
            Error [ Diagnostics.at DiagnosticCodes.UnknownContextName span $"'{name}' names the context '{ctxName}', which this lambda isn't given: it is given {Diagnostics.listed quoted}" |> Diagnostics.about name ]
        | Some (written, None) ->
            Error [ Diagnostics.at DiagnosticCodes.UnknownContextName written.Span $"'{name}' can name only a context, written by its name, as in `context {name}: Log`" |> Diagnostics.about name ]
        | None ->
            match contexts with
            | [ found ] -> Ok found
            | [] -> Error [ Diagnostics.at DiagnosticCodes.UnknownContextName span $"'{name}' names no context: this lambda is given none to name" |> Diagnostics.about name ]
            | first :: _ ->
                Error [ Diagnostics.at DiagnosticCodes.UnknownContextName span
                            $"'{name}' could name any of {Diagnostics.listed quoted}, which this lambda is given: say which, as in `context {name}: {shown first}`" |> Diagnostics.about name ]
    names
    |> List.fold (fun result (name, written, span) ->
        result
        |> Result.bind (fun (env', current) ->
            named name written span
            |> Result.map (fun context ->
                let members =
                    context.Info.Members
                    |> Map.toList
                    |> List.choose (fun (memberName, _) -> TypeEnv.tryFind memberName env |> Option.map (fun scheme -> memberName, scheme))
                let symbol, next = declareSymbol LambdaParameters lambda.Id name Parameter false (Some lambda.Span.End) current
                let next = next |> recordSymbolType symbol context.Ty
                let next =
                    match symbol with
                    | Some symbol ->
                        let instance =
                            { Context = context.Name
                              Members = members
                              Body = body.Id
                              Scope = context.Scope
                              Instance = context.Instance
                              Supplied = context.Supplied }
                        { next with ContextNames = next.ContextNames |> Map.add symbol instance }
                    | None -> next
                TypeEnv.declare name symbol (TypeEnv.mono context.Ty) env', next))) (Ok (env, state))

/// The instance `value` loads, where it is a name a lambda binds with `context l` in `env`: the name's symbol, its
/// type, and its context's members to bind again. None for any other value.
let loaded (env: TypeEnv) (state: InferState) (value: Expression) : (TypeScheme * ContextName) option =
    match value.Kind with
    | IdentifierExpr name ->
        TypeEnv.tryFind name env
        |> Option.bind (fun scheme ->
            scheme.Symbol |> Option.bind (fun symbol -> state.ContextNames |> Map.tryFind symbol) |> Option.map (fun instance -> scheme, instance))
    | _ -> None

/// `state` with `value`, the name a `use` loads, recorded as a reference to it (`scheme`'s), of its type. A lambda that
/// loads a `memory` block's `Memory` requires it, as one over the block's cells does, so it can't leave the block.
let recordLoad (value: Expression) (scheme: TypeScheme) (instance: ContextName) (state: InferState) : InferState =
    let state = recordReference value scheme state
    let state =
        match instance.Scope with
        | Some scope -> LambdaContexts.record [ FunctionContexts.scoped instance.Context scope ] state
        | None -> state
    let state =
        // A parameterised context's instance is one the `use` loads, as `use Show(int)(...)` does, so the backend finds
        // its members by the instance's key (see `TypedModule.UseContexts`).
        match instance.Instance with
        | Some binding ->
            { state with
                UseContexts = state.UseContexts |> Map.add value.Id binding.Written
                UseContextTypes = state.UseContextTypes |> Map.add value.Id binding.Context }
        | None -> state
    { state with ExprTypes = state.ExprTypes |> Map.add value.Id scheme.Type }

/// `env` with the members of `instance` bound again as they were where its lambda's body starts, and a `memory`
/// block's scope the one a `mut` takes, a parameterised context's instance the nearest, and a context with no members
/// supplied: what `use l` does.
let load (env: TypeEnv) (instance: ContextName) : TypeEnv =
    let env = instance.Scope |> Option.fold (fun acc scope -> MemoryCells.withBlockScope scope acc) env
    let env = instance.Instance |> Option.fold (fun acc binding -> TypeEnv.withInstance binding acc) env
    let env = instance.Supplied |> Option.fold (fun (acc: TypeEnv) (id, provider) -> { acc with Supplied = acc.Supplied.Add(id, provider) }) env
    instance.Members |> List.fold (fun acc (memberName, scheme) -> TypeEnv.extend memberName scheme acc) env
