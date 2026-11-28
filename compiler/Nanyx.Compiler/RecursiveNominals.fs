namespace NyxCompiler

/// A nominal type whose definition names it: `type Node = protected (value: int, rest: #some(Node) | #none)`,
/// `type Stream(a) = protected (next: () -> #next(a, Stream(a)) | #done)`.
///
/// A nominal type's type holds what it is made from (see `TyNominal`), so one made from itself would never end, as a
/// recursive alias would not (see `RecursiveAliases`). It holds its definition closed instead: `TyRecursive(body,
/// values)`. In `body` each variable of the definition (its parameters, then the scope of a type in a scoped context's
/// scope, then the context variable of a type generic in contexts) is a `TyParam`, and each place the type names itself
/// is a `TySelf` of the arguments it is written with there. `values` are what those variables are here: the contexts a
/// type generic in contexts carries are held as its argument holds them, a function type's contexts, so that a pass
/// that reads variables reads them as context variables. `underlying` unfolds it one level, where what a value is made
/// from is looked at; a pass that walks a type finds the variables in `values` and ends.
module RecursiveNominals =
    /// What the variable of a nominal type's definition that `argument`, one of the type's arguments, is for stands for:
    /// the scope of a scoped context, or the argument itself, the contexts carried included.
    let private valueOf (argument: Ty) : Ty =
        match argument with
        | FunctionContexts.ScopedContext(_, scope) -> scope
        | _ -> argument

    /// What the nominal type whose definition is made from `underlying`, applied to `args`, is made from, when the type
    /// names itself: its closed definition at those arguments.
    let instantiate (underlying: Ty) (args: Ty list) : Ty option =
        match underlying with
        | TyRecursive(TyGroup _ as body, _) -> Some (TyRecursive(body, args))
        | TyRecursive(body, _) -> Some (TyRecursive(body, List.map valueOf args))
        | _ -> None

    /// `ty` with each part `replace` gives a replacement for replaced, and the others walked in turn. The closed
    /// definition of a recursive type inside it is left as it is: its parameters are its own.
    let rec private mapTy (replace: Ty -> Ty option) (ty: Ty) : Ty =
        let recur = mapTy replace
        match replace ty with
        | Some replaced -> replaced
        | None ->
            match ty with
            | TyRecursive(body, args) -> TyRecursive(body, List.map recur args)
            // A context variable replaced by the contexts a type carries adds them.
            | TyFunc(input, output, contexts) -> TyFunc(recur input, recur output, mapContexts replace contexts)
            | TyContexts contexts -> TyContexts(mapContexts replace contexts)
            | _ -> Ty.map recur ty

    /// `contexts`, a function type's or those a type carries, walked by `mapTy`.
    and private mapContexts (replace: Ty -> Ty option) (contexts: Ty list) : Ty list =
        contexts
        |> List.map (mapTy replace)
        |> List.collect (function FunctionContexts.Carried carried -> carried | context -> [ context ])
        |> FunctionContexts.flatten

    /// What the nominal type `ty` is made from: its underlying type, unfolded one level when the type names itself, so
    /// that each place it does is the type at the arguments it is written with there. `ty` itself when it isn't nominal.
    let underlying (ty: Ty) : Ty =
        match ty with
        // A member of a group holds its arguments as they are, for what its definition's variables stand for.
        | TyNominal(_, _, TyRecursive(TyGroup(selected, members), args), _) -> RecursiveAliases.unfoldMember valueOf selected members args
        | TyNominal(id, _, TyRecursive(body, values), isPrivate) ->
            let rec replace ty =
                match ty with
                | TyParam index when index < values.Length -> Some values.[index]
                | TySelf args ->
                    let args = List.map (mapTy replace) args
                    Some (TyNominal(id, args, TyRecursive(body, List.map valueOf args), isPrivate))
                | _ -> None
            mapTy replace body
        | TyNominal(_, _, underlying, _) -> underlying
        | _ -> ty

    /// A nominal type: its identity, its arguments, what it is made from (see `underlying`), and whether that is private.
    let (|Nominal|_|) (ty: Ty) =
        match ty with
        | TyNominal(id, args, _, facts) -> Some (id, args, underlying ty, facts.IsPrivate)
        | _ -> None

    /// What a reference to the nominal type being defined is made from while its definition is read: a marker, closed
    /// over once the definition is read (see `close`).
    let selfMarker : Ty = TySelf []

    /// The arguments of `ty` if it is a reference, read with `selfMarker`, to the nominal type `id` being defined.
    let private (|SelfReference|_|) (id: NominalId) (ty: Ty) =
        match ty with
        | TyNominal(nominal, args, TySelf [], _) when nominal = id -> Some args
        | _ -> None

    /// Whether `ty`, the definition of the nominal type `id` read with `selfMarker`, names the type.
    let rec refersToItself (id: NominalId) (ty: Ty) : bool =
        match ty with
        | SelfReference id _ -> true
        | TyRecursive(_, args) -> args |> List.exists (refersToItself id)
        | _ -> Ty.children ty |> List.exists (refersToItself id)

    /// What the nominal type `id`, whose definition `ty` was read with `selfMarker` and names it, is made from: the closed
    /// definition at the type's own `variables` (see the module). A type generic in contexts that names itself without
    /// contexts, `Stream(a)` in `type Stream(a) = [k] protected (...)`, carries its own there, `contextVariable`: the
    /// rest of a stream requires what the stream does.
    let close (id: NominalId) (variables: TyVar list) (contextVariable: TyVar option) (ty: Ty) : Ty =
        let positions = variables |> List.mapi (fun index variable -> variable.Id, index) |> Map.ofList
        let ownContexts (args: Ty list) =
            match contextVariable, args with
            | Some variable, FunctionContexts.CarryingArguments(own, []) -> own @ [ FunctionContexts.carried [ TyVar variable ] ]
            | _ -> args
        let rec replace ty =
            match ty with
            | TyVar v when positions.ContainsKey v.Id -> Some (TyParam positions.[v.Id])
            | SelfReference id args -> Some (TySelf(ownContexts args |> List.map (mapTy replace)))
            | _ -> None
        TyRecursive(mapTy replace ty, variables |> List.map TyVar)
