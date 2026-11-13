/// Context variables. A single lowercase letter in a context list, `c` in `[c] list(a), ([c] a -> b) -> list(b)`,
/// stands for whatever contexts the function given for it requires. A def's own context list binds it, and a call binds
/// it to the contexts of the argument passed for it, which the call then requires, as it would if the callee wrote them.
module internal NyxCompiler.ContextVariables

open Parser.Program
open NyxCompiler.InferState

/// The context variables the context list `contexts` names, each with where it is written.
let private namedIn (contexts: TypeExpr list) =
    contexts |> List.choose (fun ctx -> match ctx.Kind with TypeVar name -> Some (name, ctx.Span) | _ -> None)

/// The context variables the context list `contexts` names.
let names (contexts: TypeExpr list) : Set<Identifier> =
    namedIn contexts |> List.map fst |> Set.ofList

/// Each context variable written on a function type within `typeExpr` that nothing binds, with where it is written. A
/// function type's own context list binds its variables within it, unless it is a parameter or the result of another
/// function type, when they must be bound around it (`inFunction`): a def's own list binds those of its parameters, and a
/// record field's function type `try: [c] (a, ([c] a -> b)) -> b` binds its own.
let rec private unboundIn (bound: Set<Identifier>) (inFunction: bool) (typeExpr: TypeExpr) : (Identifier * SourceSpan) list =
    let within = unboundIn bound inFunction
    match typeExpr.Kind with
    | TypeWithContext(contexts, inner) when inFunction ->
        (namedIn contexts |> List.filter (fst >> bound.Contains >> not)) @ unboundIn bound false inner
    | TypeWithContext(contexts, inner) -> unboundIn (Set.union bound (names contexts)) false inner
    | TypeFunc(arg, ret) -> unboundIn bound true arg @ unboundIn bound true ret
    | TypeTuple items
    | TypeUnion items
    | TypeIntersection items -> items |> List.collect within
    | TypeApply(_, items) -> items |> List.collect (unboundIn bound false)
    | TypeRecord fields
    | TypeOpenRecord(fields, _) ->
        fields
        |> List.collect (function
            | TypeField(_, _, Some t, _)
            | TypeMember(_, Some t) -> unboundIn bound false t
            | _ -> [])
    | TypeTag(_, Some inner)
    | TypeConstraint(_, inner, _)
    | TypeWhere(inner, _) -> within inner
    | _ -> []

/// Whether each context variable written on a function type in `typeOpt`, the annotation of the def `name`, is one
/// something binds: the def's own context list, as `[c]` in `[c] list(a), ([c] a -> b) -> list(b)`, or a function
/// type of its own (see `unboundIn`).
let ensureBound (name: Identifier) (typeOpt: TypeExpr option) : Result<unit, Diagnostic list> =
    let bound, signature =
        match typeOpt with
        | Some (Kind (TypeWithContext(contexts, inner))) -> names contexts, Some inner
        | other -> Set.empty, other
    match signature |> Option.map (unboundIn bound false) |> Option.defaultValue [] |> List.distinctBy fst with
    | [] -> Ok ()
    | unbound ->
        unbound
        |> List.map (fun (var, span) ->
            Diagnostics.at DiagnosticCodes.UnboundContextVariable span
                $"The context variable '{var}' is not bound: add it to the contexts of '{name}', as in '[{var}] ...'")
        |> Error
/// The type at each context variable of `general`'s function types that `isVariable` picks, paired with what stands at
/// the same place in `specific`, a type of the same shape: a callee's type as it is declared, against the same type
/// instantiated at a call.
let rec private pairs (isVariable: TyVar -> bool) (general: Ty) (specific: Ty) : (TyVar * Ty) list =
    let all (gs: Ty list) (ss: Ty list) =
        if gs.Length = ss.Length then List.map2 (pairs isVariable) gs ss |> List.concat else []
    match general, specific with
    | TyFunc(ga, gr, gc), TyFunc(sa, sr, sc) ->
        let own =
            if gc.Length = sc.Length then
                List.zip gc sc |> List.choose (function TyVar v, s when isVariable v -> Some (v, s) | _ -> None)
            else []
        own @ pairs isVariable ga sa @ pairs isVariable gr sr
    | TyApply(_, gs), TyApply(_, ss)
    | TyAlias(_, gs, _), TyAlias(_, ss, _)
    | TyUnion gs, TyUnion ss -> all gs ss
    | TyRecord gf, TyRecord sf when Set.ofSeq gf.Keys = Set.ofSeq sf.Keys -> all (List.ofSeq gf.Values) (List.ofSeq sf.Values)
    | TyTag(_, Some g), TyTag(_, Some s) -> pairs isVariable g s
    | _ -> []

/// `contexts`, a function type's, resolved by `resolve` as far as the types so far go: a context variable is what it
/// is bound to, if anything yet. A variable kept only for room (see `FunctionContexts.fit`) stands for none.
let resolved (resolve: Ty -> Ty) (contexts: Ty list) : Ty list =
    contexts
    |> List.collect (fun context -> FunctionContexts.flatten [ resolve context ])
    |> Unifier.writtenContexts
    |> List.distinct

/// What the context variables `declared` (a def's own) are in `instantiated`, the def's type where it is named: found
/// in `general`, its type as declared, at the same places.
let private instantiatedIn (declared: Set<Identifier>) (general: Ty) (instantiated: Ty) : Ty list =
    pairs (fun v -> v.Name |> Option.exists declared.Contains) general instantiated
    |> List.distinctBy (fst >> _.Id)
    |> List.map snd

/// The contexts the context variables `declared` (a callee's own) stand for at a call: found in `general`, the callee's
/// type, at the same places in `instantiated`, its type at the call, and resolved by `resolve` as far as the types so
/// far go.
let boundAt (resolve: Ty -> Ty) (declared: Set<Identifier>) (general: Ty) (instantiated: Ty) : Ty list =
    instantiatedIn declared general instantiated |> resolved resolve

/// The type of a def named as a value rather than called, `ty` being its type there and `general` its type as
/// declared: a function that requires `written`, the def's own contexts, where it is called. A parameterised context is
/// found where the def is named, as a call finds it, and is not carried (see `WrittenContexts.plain`).
let ofNamedValue (written: TypeExpr list) (general: Ty) (ty: Ty) (state: InferState) : Ty * InferState =
    match Unifier.unalias ty with
    | TyFunc(input, output, own) ->
        let plain, next = WrittenContexts.plain state ty written
        match plain @ instantiatedIn (names written) general ty with
        | [] -> ty, next
        | required -> TyFunc(input, output, own @ required |> List.distinct), next
    | _ -> ty, state

/// What it takes for a call to `callee` at `span` to require `contexts` (see `boundAt`) where it is, in the def or
/// at the top level `state.ContextScope` says: `requirePlain` checks a context such as `Console`, as if the callee
/// declared it; a context variable must be one the def around the call declares, or one an unannotated def infers
/// (see `InferState.InferredContextVars`). A variable not bound yet in a def that writes its type stands for whatever
/// the def's own types make it. A parameterised context at a concrete type is settled where the function given for it
/// is made, so it is not required again.
let require
    (requirePlain: InferState -> TypeExpr -> Result<InferState, Diagnostic list>)
    (callee: Identifier)
    (span: SourceSpan)
    (contexts: Ty list)
    (state: InferState)
    : Result<InferState, Diagnostic list> =
    contexts
    |> List.fold (fun result context ->
        result
        |> Result.bind (fun current ->
            match context, current.ContextScope with
            | FunctionContexts.PlainContext name, _ -> requirePlain current (TypeExpr.synthetic (TypeName name))
            | TyVar { Name = Some var }, DefScope(defName, false) ->
                let declared = current.ContextRequirements |> Map.tryFind defName |> Option.map names |> Option.defaultValue Set.empty
                if declared.Contains var then Ok current
                else
                    Error [ Diagnostics.at DiagnosticCodes.UndeclaredContext span
                                $"'{callee}' requires the contexts '{var}', which '{defName}' does not declare: add [{var}] to the type of '{defName}'"
                            |> ContextFixes.withAddToType current defName var ]
            | TyVar _, DefScope(defName, true) ->
                let inferred = current.InferredContextVars |> Map.tryFind defName |> Option.defaultValue []
                Ok { current with InferredContextVars = current.InferredContextVars |> Map.add defName (inferred @ [ context ]) }
            | _ -> Ok current)) (Ok state)
