/// The contexts written on a function type, as in `([Console] int -> int) -> int`, read into the function type that
/// carries them (see `TyFunc`).
module internal NyxCompiler.WrittenContexts

open Parser.Program
open NyxCompiler.InferState

/// Whether `name` is a scoped context (`scoped context $Memory = ()`).
let isScoped (state: InferState) (name: Identifier) =
    state.TypeDefs |> Map.tryFind name |> Option.exists _.IsScoped

/// The context `context` names, as a function type carries it: `Console` is `TyApply("Console", [])`, `Show(a)` is
/// `TyApply("Show", [a])` and a context variable `c` is the type variable of that name. A scoped context is of a scope
/// that every scoped context of its name written in the same signature shares, a variable no name written in source
/// can be (see `FunctionContexts.scoped`). `None` for one written by its members, which only a def's own contexts can be.
let private read (typeExprToTy: InferState -> TypeExpr -> Ty * InferState) (state: InferState) (context: TypeExpr) =
    match context.Kind with
    | TypeVar _ ->
        let var, next = typeExprToTy state context
        Some var, next
    | TypeName name when isScoped state name ->
        let scope, next = typeExprToTy state (TypeExpr.synthetic (TypeVar (FunctionContexts.scopeVariableName name)))
        Some (FunctionContexts.scoped name scope), next
    | TypeName name -> Some (TyApply(name, [])), state
    | TypeApply(name, args) ->
        let argTys, next = args |> List.mapFold typeExprToTy state
        Some (TyApply(name, argTys)), next
    | _ -> None, state

/// The scope of the scoped context `name` somewhere in `ty`, if `ty` has one.
let rec private scopeIn (name: Identifier) (ty: Ty) : Ty option =
    let within = List.tryPick (scopeIn name)
    match ty with
    | FunctionContexts.ScopedContext(found, scope) when found = name -> Some scope
    | TyFunc(input, output, contexts) -> within (contexts @ [ input; output ])
    | TyNominal(_, underlying, _) -> scopeIn name underlying
    | TyAlias(_, args, underlying) -> within (args @ [ underlying ])
    | TyRecord fields
    | TyOpenRecord(fields, _) -> within (List.ofSeq fields.Values)
    | TyTag(_, payload) -> payload |> Option.bind (scopeIn name)
    | TyApply(_, items)
    | TyUnion items -> within items
    | TyVar _
    | TyPrimitive _ -> None

/// The contexts among `written`, a def's own as its type writes them, that take no arguments, as a function type
/// carries them. A scoped one is of the scope `ty`, the def's type where it is named, has for it, or of a scope of its
/// own.
let plain (state: InferState) (ty: Ty) (written: TypeExpr list) : Ty list * InferState =
    written
    |> List.choose (fun ctx -> match ctx.Kind with TypeName name -> Some name | _ -> None)
    |> List.mapFold (fun current name ->
        if isScoped current name then
            match scopeIn name ty with
            | Some scope -> FunctionContexts.scoped name scope, current
            | None ->
                let scope, next = freshVar current
                FunctionContexts.scoped name scope, next
        else TyApply(name, []), current) state

/// `inner`, the type `[contexts] inner` is written with, requiring `contexts` too when it is a function type. On any
/// other type the contexts are left out.
let withContexts (typeExprToTy: InferState -> TypeExpr -> Ty * InferState) (state: InferState) (contexts: TypeExpr list) (inner: Ty) : Ty * InferState =
    match Unifier.unalias inner with
    | TyFunc(arg, ret, own) ->
        let read, next = contexts |> List.mapFold (read typeExprToTy) state
        TyFunc(arg, ret, (read |> List.choose id) @ own |> List.distinct), next
    | _ -> inner, state
