/// The contexts written on a function type, as in `([Console] int -> int) -> int`, read into the function type that
/// carries them (see `TyFunc`).
module internal NyxCompiler.WrittenContexts

open Parser.Program
open NyxCompiler.InferState

/// The context `context` names, as a function type carries it: `Console` is `TyApply("Console", [])`, `Show(a)` is
/// `TyApply("Show", [a])` and a context variable `c` is the type variable of that name. `None` for one written by its
/// members, which only a def's own contexts can be.
let private read (typeExprToTy: InferState -> TypeExpr -> Ty * InferState) (state: InferState) (context: TypeExpr) =
    match context.Kind with
    | TypeVar _ ->
        let var, next = typeExprToTy state context
        Some var, next
    | TypeName name -> Some (TyApply(name, [])), state
    | TypeApply(name, args) ->
        let argTys, next = args |> List.mapFold typeExprToTy state
        Some (TyApply(name, argTys)), next
    | _ -> None, state

/// `inner`, the type `[contexts] inner` is written with, requiring `contexts` too when it is a function type. On any
/// other type the contexts are left out.
let withContexts (typeExprToTy: InferState -> TypeExpr -> Ty * InferState) (state: InferState) (contexts: TypeExpr list) (inner: Ty) : Ty * InferState =
    match Unifier.unalias inner with
    | TyFunc(arg, ret, own) ->
        let read, next = contexts |> List.mapFold (read typeExprToTy) state
        TyFunc(arg, ret, (read |> List.choose id) @ own |> List.distinct), next
    | _ -> inner, state
