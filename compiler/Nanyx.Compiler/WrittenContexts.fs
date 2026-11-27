/// The contexts written on a function type, as in `([Console] int -> int) -> int`, read into the function type that
/// carries them (see `TyFunc`).
module internal NyxCompiler.WrittenContexts

open Parser.Program
open NyxCompiler.InferState

/// The associated types of the context `ctxName` at `argTys`, each by its name with the argument it is at.
let associatedArguments (state: InferState) (ctxName: Identifier) (argTys: Ty list) : (string * Ty) list =
    match state.TypeDefs |> Map.tryFind ctxName with
    | Some info when not info.AssociatedTypes.IsEmpty && argTys.Length = info.Parameters.Length ->
        List.zip info.AssociatedTypes (List.skip (argTys.Length - info.AssociatedTypes.Length) argTys)
    | _ -> []

/// The instance of `ctxName` at `argTys` as a message writes it: `ToString(Time(int))`, `Ord(a)`, and an associated
/// type something fixes by name, `Iter(Bag, elem = string)`. A context at no arguments is only its name.
let instanceText (state: InferState) (ctxName: Identifier) (argTys: Ty list) =
    let associated = associatedArguments state ctxName argTys
    let positional = argTys |> List.truncate (argTys.Length - associated.Length) |> List.map (resolveSoFar state)
    let named =
        associated
        |> List.map (fun (name, ty) -> name, resolveSoFar state ty)
        |> List.filter (snd >> TypeEnv.freeTyVars >> Set.isEmpty)
    // Its arguments are named together, as a message's types are.
    Unifier.messageText state.TypeDefs (fun write ->
        match (positional |> List.map write) @ (named |> List.map (fun (name, ty) -> $"{name} = {write ty}")) with
        | [] -> ctxName
        | written -> $"""{ctxName}({String.concat ", " written})""")

/// The context `ctxName` a call requires at `argTys`, as an error names it: written with the arguments (see
/// `instanceText`), unless one still holds a type variable nothing has named yet, which would mean nothing to the
/// reader, as `ToString(t2)`; then only its name.
let missingText (state: InferState) (ctxName: Identifier) (argTys: Ty list) =
    let unnamed = argTys |> List.exists (resolveSoFar state >> TypePrinting.varsInPrintOrder >> List.exists (fun var -> var.Name.IsNone))
    if unnamed then ctxName else instanceText state ctxName argTys

/// Whether `name` is a scoped context (`scoped context Memory = ()`).
let isScoped (state: InferState) (name: Identifier) =
    state.TypeDefs |> Map.tryFind name |> Option.exists _.IsScoped

/// The name the context `name` is declared under: a block import's name (`Tx`) stands for the qualified one (`db.Tx`),
/// which the block's own types write, so that the two are one context where function types meet (#1486).
let canonical (state: InferState) (name: Identifier) : Identifier =
    match state.BlockAliases |> Map.tryFind name with
    | Some qualified ->
        match state.TypeDefs |> Map.tryFind name, state.TypeDefs |> Map.tryFind qualified with
        | Some short, Some declared when short.Symbol = declared.Symbol -> qualified
        | _ -> name
    | None -> name

/// The name the scoped context `name` is declared under: one written through an import's alias, `t.Tx` after
/// `import x as t`, is the imported module's `Tx`, which its functions require and its types' scope is of (see
/// `TypeDefInfo.ScopedBy`), so that a signature's `[t.Tx] t.Cursor` is one scope (#1871). A block import's name is
/// left to `canonical`.
let declaredScoped (state: InferState) (name: Identifier) : Identifier =
    match name.LastIndexOf '.' with
    | dot when dot > 0 && not (state.BlockAliases.ContainsKey (name.Substring(dot + 1))) ->
        let bare = name.Substring(dot + 1)
        match state.TypeDefs |> Map.tryFind name, state.TypeDefs |> Map.tryFind bare with
        | Some aliased, Some declared when aliased.Symbol = declared.Symbol -> bare
        | _ -> name
    | _ -> name

/// The context `context` names, as a function type carries it: `Console` is `TyApply("Console", [])`, `Show(a)` is
/// `TyApply("Show", [a])` and a context variable `c` is the type variable of that name. A scoped context is of a scope
/// that every scoped context of its name written in the same signature shares (see `signatureScope`). `None` for one
/// written by its members, which only a def's own contexts can be.
let private read (typeExprToTy: InferState -> TypeExpr -> Ty * InferState) (state: InferState) (context: TypeExpr) =
    match context.Kind with
    | TypeVar _ ->
        let var, next = typeExprToTy state context
        Some var, next
    | TypeName name when isScoped state name ->
        let name = declaredScoped state name
        let scope, next = signatureScope state name
        Some (FunctionContexts.scoped (canonical state name) scope), next
    | TypeName name -> Some (TyApply(canonical state name, [])), state
    | TypeApply(name, args) ->
        let argTys, next = args |> List.mapFold typeExprToTy state
        Some (TyApply(canonical state name, argTys)), next
    | _ -> None, state

/// The scope of the scoped context `name` somewhere in `ty`, if `ty` has one: a cell's type has `Memory`'s.
let rec private scopeIn (name: Identifier) (ty: Ty) : Ty option =
    let within = List.tryPick (scopeIn name)
    match ty with
    | FunctionContexts.ScopedContext(found, scope) when found = name -> Some scope
    | FunctionContexts.CellType(_, scope) when name = FunctionContexts.cellContext -> Some scope
    | TyFunc(input, output, contexts) -> within (contexts @ [ input; output ])
    | TyOpenRecord(fields, _) -> within (List.ofSeq fields.Values)
    | _ -> within (Ty.children ty)

/// The contexts among `written`, a def's own as its type writes them, that take no arguments, as a function type
/// carries them. A scoped one is of the scope `ty`, the def's type where it is named, has for it, or of a scope of its
/// own.
let plain (state: InferState) (ty: Ty) (written: TypeExpr list) : Ty list * InferState =
    written
    |> List.choose (fun ctx -> match ctx.Kind with TypeName name -> Some name | _ -> None)
    |> List.mapFold (fun current name ->
        let name = if isScoped current name then declaredScoped current name else name
        let declared = canonical current name
        if isScoped current name then
            match scopeIn declared ty |> Option.orElse (scopeIn name ty) with
            | Some scope -> FunctionContexts.scoped declared scope, current
            | None ->
                let scope, next = freshVar current
                FunctionContexts.scoped declared scope, next
        else TyApply(declared, []), current) state

/// `inner`, the type `[contexts] inner` is written with, requiring `contexts` too when it is a function type. On any
/// other type the contexts are left out.
let withContexts (typeExprToTy: InferState -> TypeExpr -> Ty * InferState) (state: InferState) (contexts: TypeExpr list) (inner: Ty) : Ty * InferState =
    match Unifier.unalias inner with
    | TyFunc(arg, ret, own) ->
        let read, next = contexts |> List.mapFold (read typeExprToTy) state
        TyFunc(arg, ret, (read |> List.choose id) @ own |> List.distinct), next
    | _ -> inner, state
