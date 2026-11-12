/// The contexts a lambda uses, which its type carries: `{ n -> println(n) }` is `[Console] int -> ()`. A lambda
/// requires them where it is called, as a def requires its own, so passing it where a pure function is expected is an
/// error, and passing it to a higher-order function with a context variable makes the call require them too.
///
/// While a lambda's body is inferred, each context that something in it requires where it runs is recorded for the
/// innermost lambda around it: a call to a function that requires the context or to one of its members, a call to a
/// function value whose type requires it, and what a call's context variables stand for. A lambda made inside another
/// is not run by it, so what it uses is its own. A def's own body is not such a lambda: what it uses are the def's
/// contexts, checked as they always are. A parameterised context is found where the lambda is made, as a call finds it,
/// and the lambda's type does not carry it.
///
/// The lambda is still checked where it is made, against the contexts there.
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

/// `state` with the contexts a lambda of type `lambdaTy` requires recorded as used: a lambda run where it is written,
/// as a `for` loop's body is.
let recordRunHere (lambdaTy: Ty) (state: InferState) : InferState =
    match Unifier.unalias lambdaTy with
    | TyFunc(_, _, contexts) -> record contexts state
    | _ -> state

/// `state` with the plain contexts among `written`, contexts as a def's type writes them, recorded as used (see
/// `record`). A parameterised context, or one written by its members, is found where the lambda is made.
let recordWritten (written: TypeExpr list) (state: InferState) : InferState =
    let plain = written |> List.choose (fun ctx -> match ctx.Kind with TypeName name -> Some (TyApply(name, [])) | _ -> None)
    record plain state

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
