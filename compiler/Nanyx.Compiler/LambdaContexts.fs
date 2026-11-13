/// The contexts a lambda uses, which its type carries: `{ n -> println(n) }` is `[Console] int -> ()`. A lambda
/// requires them where it is called, as a def requires its own, so passing it where a pure function is expected is an
/// error, and passing it to a higher-order function with a context variable makes the call require them too.
///
/// While a lambda's body is inferred, each context that something in it requires where it runs is recorded for the
/// innermost lambda around it: a call to a function that requires the context or to one of its members, a call to a
/// function value whose type requires it, and what a call's context variables stand for. A lambda made inside another
/// is not run by it, so what it uses is its own. A context a `use` inside the lambda supplies is not one it uses: it
/// runs with that instance. A def's own body is not such a lambda: what it uses are the def's
/// contexts, checked as they always are. A parameterised context is found where the lambda is made, as a call finds it,
/// and the lambda's type does not carry it.
///
/// The lambda is still checked where it is made, against the contexts there.
///
/// A local def that does not write its type and calls itself, as a `rec` def does, requires what its lambda uses. A call
/// to itself from that lambda uses nothing besides, and the context variable the call's type has stands for exactly
/// what the def requires.
module internal NyxCompiler.LambdaContexts

open Parser.Program
open NyxCompiler.InferState

/// `state` with `lambda` marked as a def's own body: its contexts are the def's, not its type's.
let markBody (lambda: Expression) (state: InferState) : InferState =
    match lambda.Kind with
    | Lambda _ -> { state with BodyLambdas = state.BodyLambdas.Add lambda.Id }
    | _ -> state

/// `state` as the body of `lambda` starts to be inferred: what it uses is recorded for it, unless it is a def's body.
let enter (lambda: Expression) (state: InferState) : InferState =
    if state.BodyLambdas.Contains lambda.Id then state
    else { state with LambdaContexts = [] :: state.LambdaContexts }

/// The contexts `lambda`'s body used, each once in the order first used, and `state` as it is once the body has been
/// inferred.
let leave (lambda: Expression) (state: InferState) : Ty list * InferState =
    match state.LambdaContexts with
    | used :: outer when not (state.BodyLambdas.Contains lambda.Id) ->
        List.rev used |> List.distinct, { state with LambdaContexts = outer }
    | _ -> [], state

/// `state` with `contexts` recorded as used by the innermost lambda being inferred, if any.
let record (contexts: Ty list) (state: InferState) : InferState =
    match state.LambdaContexts with
    | used :: outer when not contexts.IsEmpty -> { state with LambdaContexts = (List.rev contexts @ used) :: outer }
    | _ -> state

/// `state` with `value`, the instance a `use` loads, noted as loaded inside the lambdas being inferred now.
let enterUse (value: Expression) (state: InferState) : InferState =
    { state with UseDepths = state.UseDepths |> Map.add value.Id state.LambdaContexts.Length }

/// Whether a `use` inside the innermost lambda being inferred supplies the context `ctx` in `env`: it binds every
/// member of it there. The lambda runs with that instance, so it does not require the context.
let private suppliedWithin (env: TypeEnv) (state: InferState) (ctx: Ty) : bool =
    let depth = state.LambdaContexts.Length
    let loadedWithin memberName =
        match TypeEnv.tryFind memberName env |> Option.bind _.ProvidedBy with
        | Some (FromUse value) -> state.UseDepths |> Map.tryFind value |> Option.exists (fun loaded -> loaded >= depth)
        | _ -> false
    match ctx with
    | TyApply(name, []) ->
        match state.TypeDefs |> Map.tryFind name with
        | Some info when info.IsContext && not info.Members.IsEmpty -> info.Members |> Map.forall (fun memberName _ -> loadedWithin memberName)
        | _ -> false
    | _ -> false

/// `record` for the contexts among `contexts` that no `use` inside the innermost lambda supplies in `env`.
let recordUnsupplied (env: TypeEnv) (contexts: Ty list) (state: InferState) : InferState =
    record (contexts |> List.filter (suppliedWithin env state >> not)) state

/// `state` as the value of an unannotated local def starts to be inferred, `selfTy` being the variable the def is bound
/// at meanwhile. The first lambda inferred within the value is the def's own: `{ ... }` in `rec loop = { ... }`.
let enterSelf (selfTy: Ty) (state: InferState) : InferState =
    match selfTy with
    | TyVar v -> { state with SelfTypes = state.SelfTypes |> Map.add v.Id { Depth = state.LambdaContexts.Length + 1; Calls = [] } }
    | _ -> state

/// Whether a call to a function value of type `calleeTy` is a local def calling itself from its own lambda, not from one
/// made within it, which uses what the call requires as it would for any other call.
let isSelfCall (calleeTy: Ty) (state: InferState) : bool =
    match Unifier.unalias calleeTy with
    | TyVar v -> state.SelfTypes |> Map.tryFind v.Id |> Option.exists (fun self -> self.Depth = state.LambdaContexts.Length)
    | _ -> false

/// `state` with `contexts`, those of the type of a call to a function value of type `calleeTy`, noted as a call of a
/// local def to itself if it is one.
let recordSelfCall (calleeTy: Ty) (contexts: Ty list) (state: InferState) : InferState =
    match Unifier.unalias calleeTy with
    | TyVar v ->
        match state.SelfTypes |> Map.tryFind v.Id with
        | Some self -> { state with SelfTypes = state.SelfTypes |> Map.add v.Id { self with Calls = self.Calls @ contexts } }
        | None -> state
    | _ -> state

/// `state` once the value of the local def bound at `selfTy` (see `enterSelf`) has been inferred as `valueTy`: each of
/// its calls to itself requires the contexts the def does, no more.
let leaveSelf (selfTy: Ty) (valueTy: Ty) (state: InferState) : InferState =
    match selfTy with
    | TyVar v ->
        let calls = state.SelfTypes |> Map.tryFind v.Id |> Option.map _.Calls |> Option.defaultValue []
        let state = { state with SelfTypes = state.SelfTypes.Remove v.Id }
        match Unifier.unalias valueTy with
        | TyFunc(_, _, own) -> calls |> List.fold (fun current call -> addConstraint call (TyUnion own) current) state
        | _ -> state
    | _ -> state

/// `state` with the contexts a lambda of type `lambdaTy` requires recorded as used: a lambda run where it is written,
/// as a `for` loop's body is.
let recordRunHere (lambdaTy: Ty) (state: InferState) : InferState =
    match Unifier.unalias lambdaTy with
    | TyFunc(_, _, contexts) -> record contexts state
    | _ -> state

/// `state` with the plain contexts among `written`, contexts as a def's type writes them, recorded as used (see
/// `recordUnsupplied`). A parameterised context, or one written by its members, is found where the lambda is made.
let recordWritten (env: TypeEnv) (written: TypeExpr list) (state: InferState) : InferState =
    let plain = written |> List.choose (fun ctx -> match ctx.Kind with TypeName name -> Some (TyApply(name, [])) | _ -> None)
    recordUnsupplied env plain state

/// The context whose member `name`, as `env` binds it, is: of the ambient instance, of one a def around declares, or of
/// one a `use` of a value of the context's type loads (`use Foo(bar = ...)`). `None` for any other name, including a
/// member of a record written out that a `use` loads, and for a member of a parameterised context.
let contextOfMember (env: TypeEnv) (state: InferState) (name: Identifier) : Identifier option =
    match TypeEnv.tryFind name env |> Option.bind _.Symbol with
    | None -> None
    | Some symbol ->
        match state.AmbientMembers |> Map.tryFind name with
        | Some (ambient, ctxName) when ambient = symbol -> Some ctxName
        | _ ->
            state.TypeDefs
            |> Map.tryPick (fun ctxName info ->
                if info.IsContext && info.Parameters.IsEmpty && info.Members |> Map.exists (fun _ id -> id = symbol) then Some ctxName
                else None)
