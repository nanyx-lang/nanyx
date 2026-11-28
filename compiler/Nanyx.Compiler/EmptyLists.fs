/// `[]` where nothing says what its elements are is a `list(undefined)`, a list that can hold nothing (#3129): `def items
/// = []` is one, so `items \head` is `#emptyList`, and it can still be given where a list of any type is expected, as
/// `undefined` in a type's arguments joins with the other type (see `Unifier.holdsBottom`). Its element is left a
/// variable until the def it is in is generalised, as a number literal's type is (see `NumericLiterals.settle`), since
/// what comes after may say what it is: `mut buffer = []` and a later `buffer := buffer ++ [x]`. A variable in a
/// function's input is left generic: `{ ys -> ys ++ [] }` takes a list of any type.
module internal NyxCompiler.EmptyLists

open Parser.Program
open NyxCompiler.InferState

/// `state` with `element`, the element type of a `[]` just inferred, recorded.
let record (element: Ty) (state: InferState) : InferState =
    match element with
    | TyVar v -> { state with EmptyListElements = v :: state.EmptyListElements }
    | _ -> state

/// Whether the variable `id` is in `ty` where `undefined` doesn't join with another type (see `Unifier.holdsBottom`):
/// in a function's input, which would take nothing, or in a nominal type's argument, which is its own, so that
/// `MRef.new([])` holds a list of whatever is set into it later.
let rec private outsideCovariant (id: int) (ty: Ty) =
    match ty with
    | TyFunc(input, output, _) -> Unifier.occurs id input || outsideCovariant id output
    | TyNominal(_, args, _, _) -> args |> List.exists (Unifier.occurs id)
    | TyAlias(_, _, underlying) -> outsideCovariant id underlying
    | _ -> Ty.exists (outsideCovariant id) ty

/// `scheme`, the type a def that doesn't write its type was generalised to, with the element of each `[]` that is still
/// a variable of its own made `undefined`, and `state` with that variable solved to it, so that the types recorded for
/// the def's expressions say so too.
let close (state: InferState) (scheme: TypeScheme) : TypeScheme * InferState =
    let elements =
        state.EmptyListElements
        |> List.choose (fun element ->
            match resolveSoFar state (TyVar element) with
            | TyVar v when v.Name.IsNone && scheme.Quantified.Contains v.Id && not (outsideCovariant v.Id scheme.Type) -> Some v
            | _ -> None)
        |> List.distinctBy _.Id
    match elements with
    | [] -> scheme, state
    | _ ->
        let closing = elements |> List.map (fun v -> v.Id, Unifier.bottomType) |> Map.ofList
        { scheme with
            Type = Unifier.apply closing scheme.Type
            Quantified = elements |> List.fold (fun quantified v -> Set.remove v.Id quantified) scheme.Quantified },
        elements |> List.fold (fun state v -> addConstraint (TyVar v) Unifier.bottomType state) state

/// `scheme`, the type of a top-level value that is no function and doesn't write its type, with each variable the
/// compiler made up in it `undefined`, and that closing: nothing could make a value of such a variable, so `items
/// \head`, for the `items` above, is a `#some(undefined) | #emptyList`, which is `#emptyList`, as `undefined` binds no
/// variable it is given for. One that a context the value requires names is left alone, since an instance can make a
/// value of it, as `Default(a)` can; `requiresParameterised` says whether it requires one. Only the value's type, as
/// other defs read it, is closed, not the variables its own expressions are of: `list.head` there is still read at
/// its own variable, so the backend compiles it as it did, and the messages about the value name the variable.
let closeValue (requiresParameterised: bool) (state: InferState) (scheme: TypeScheme) : TypeScheme * Subst =
    let vars =
        match Unifier.unalias scheme.Type with
        | TyFunc _ -> []
        | _ when requiresParameterised -> []
        | ty ->
            let inInstances =
                state.PendingContextInstances
                |> List.collect (fun pending -> pending.ArgTys |> List.map (resolveSoFar state >> TypeEnv.freeTyVars))
                |> Set.unionMany
            TypePrinting.varsInPrintOrder ty
            |> List.filter (fun (v: TyVar) ->
                v.Name.IsNone && scheme.Quantified.Contains v.Id && not (inInstances.Contains v.Id) && not (outsideCovariant v.Id ty))
            |> List.distinctBy _.Id
    let closing = vars |> List.map (fun v -> v.Id, Unifier.bottomType) |> Map.ofList
    { scheme with
        Type = Unifier.apply closing scheme.Type
        Quantified = vars |> List.fold (fun quantified v -> Set.remove v.Id quantified) scheme.Quantified },
    closing
