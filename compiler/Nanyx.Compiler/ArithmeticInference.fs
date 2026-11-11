namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// The contexts a top-level function that does not write its type requires for its arithmetic (see "Operator
/// contexts" in the docs' work in progress). An arithmetic operator on a type still a variable where it is written is
/// left pending (see `ArithmeticOperators.check`). When the function is generalised, an operator on one of the
/// function's own type variables makes it require the prelude's context the operator is read through (see
/// `OperatorContexts`), as if its type said so, and the operator calls the context's member: `def add = { x, y ->
/// x + y }` is `[Add(a)] a, a -> a`. Any other operator is on numbers, as it would have been where it is written.
///
/// A local function requires no contexts, so an operator on a type one would be generic in is on numbers.
module internal ArithmeticInference =
    /// The def about to be generalised, which says which pending operators are settled.
    type Generalising =
        /// A local def, which settles those on a type it would be generic in: they are on numbers.
        | LocalDef
        /// The top-level def of this name, which settles all of its own: one on a variable of its type stays pending,
        /// for the def to require a context of it (see `require`), and any other is on numbers.
        | TopLevelDef of Identifier

    /// `state` with the operands of `pending`, now of the type `ty`, numbers: `int`, unless they are already `float`.
    let private asNumbers (pending: PendingArithmetic) (ty: Ty) (state: InferState) : InferState =
        match Unifier.unalias ty with
        | TyPrimitive("int" | "float") -> state
        | _ -> addConstraintAt pending.At pending.OperandTy (TyPrimitive "int") state

    /// Whether the module writes the top-level def `name` as a value of its own, as `add` is in `fold(0, add)`: a
    /// function that requires a parameterised context cannot be a value yet (#599), so such a def requires none for
    /// its arithmetic. A def of a `module name =` block is written inside it without the block's name.
    let private namedAsValue (state: InferState) (name: Identifier) =
        state.NamedAsValues.Contains name
        || (name.Contains '.' && state.NamedAsValues.Contains (name.Substring(name.LastIndexOf '.' + 1)))

    /// The type of the def about to be generalised, by `generalizeDef`, once the arithmetic operators left pending that
    /// the def settles (see `Generalising`) are read. An operator found to be on numbers constrains its operands, so
    /// the def is generalised again with them.
    ///
    /// A top-level def requires the contexts of each of its type variables its operators are on. The operators of a
    /// def the module names as a value are all on numbers (see `namedAsValue`).
    let generalize (generalizeDef: InferState -> TypeScheme * InferState) (generalising: Generalising) (state: InferState) : TypeScheme * InferState =
        if state.PendingArithmetic.IsEmpty then generalizeDef state
        else
            let tentative, generalized = generalizeDef state
            let subst = fst (Unifier.solve state.Constraints)
            let isOwn (ty: Ty) =
                match ty with
                | TyVar v -> tentative.Quantified.Contains v.Id
                | _ -> false
            // An operator of a def that failed to check is no def's to settle.
            let own =
                match generalising with
                | LocalDef -> state.PendingArithmetic
                | TopLevelDef name -> state.PendingArithmetic |> List.filter (fun pending -> pending.InDef = name)
            let resolved = own |> List.map (fun pending -> pending, Unifier.apply subst pending.OperandTy)
            let stays =
                match generalising with
                | LocalDef -> fun (_: PendingArithmetic) (ty: Ty) -> not (isOwn ty)
                | TopLevelDef name ->
                    let onVariables =
                        resolved |> List.filter (fun (pending, ty) -> isOwn ty && (OperatorContexts.smallest state pending.MemberName).IsSome)
                    // A def whose type an annotation cannot write requires nothing (see `ContextInference.annotation`).
                    let writable () = (ContextInference.typeExprOf (ContextInference.varNames tentative.Type) tentative.Type).IsSome
                    let requires = not onVariables.IsEmpty && not (namedAsValue state name) && writable ()
                    fun pending ty -> requires && onVariables |> List.exists (fun (other, _) -> other.Operator = pending.Operator)
            let staying, numbers = resolved |> List.partition (fun (pending, ty) -> stays pending ty)
            let pending = staying |> List.map fst
            if numbers.IsEmpty then tentative, { generalized with PendingArithmetic = pending }
            else
                numbers
                |> List.fold (fun current (pending, ty) -> asNumbers pending ty current) { state with PendingArithmetic = pending }
                |> generalizeDef

    /// The arithmetic operators of the top-level def `name`, generalised as `scheme`, that are on its own type
    /// variables, and `state` with each a call that requires its context of the variable, as a call to a function
    /// that requires one is: `ContextInference.resolvePending` then makes the def require it. The operators on one
    /// variable need the contexts `OperatorContexts.covering` gives for the members they read.
    let require (state: InferState) (name: Identifier) (scheme: TypeScheme) : PendingArithmetic list * InferState =
        let own, others = state.PendingArithmetic |> List.partition (fun pending -> pending.InDef = name)
        let onVariables =
            own
            |> List.choose (fun pending ->
                match resolveSoFar state pending.OperandTy with
                | TyVar v when scheme.Quantified.Contains v.Id -> Some (pending, v)
                | _ -> None)
        let contextsOf (v: TyVar) =
            onVariables
            |> List.filter (fun (_, other) -> other.Id = v.Id)
            |> List.map (fun (pending, _) -> pending.MemberName)
            |> List.distinct
            |> OperatorContexts.covering state
            |> Option.defaultValue []
        let calls =
            onVariables
            |> List.choose (fun (pending, v) ->
                OperatorContexts.having state (contextsOf v) pending.MemberName
                |> Option.map (fun context ->
                    pending,
                    { Def = name; Call = pending.Operator; Callee = pending.MemberName; Index = 0; Count = 1
                      Context = context; ArgTys = [ pending.OperandTy ]; Span = pending.At; Errors = []; ChecksOnly = false }))
        // Generalising left only operators on the def's own variables, so any other is on a type solved since.
        let called = calls |> List.map (fun (pending, _) -> pending.Operator) |> Set.ofList
        let settled =
            own
            |> List.filter (fun pending -> not (called.Contains pending.Operator))
            |> List.fold (fun current pending -> asNumbers pending (resolveSoFar current pending.OperandTy) current) state
        // The operators come before the def's calls, which are the latest first, so the def requires their contexts first.
        List.map fst calls,
        { settled with
            PendingArithmetic = others
            PendingContextInstances = settled.PendingContextInstances @ List.map snd calls }

    /// `state` with each of `operators`, which `require` gave, that found the instance its def requires calling that
    /// instance's member, through the instance, as an operator on a type variable does (see `OperatorCalls`).
    /// `inputOf` is the input type of a function that takes two values of a type; prefix `-` takes one.
    let callMembers (inputOf: Ty -> Ty) (operators: PendingArithmetic list) (state: InferState) : InferState =
        operators
        |> List.fold (fun (current: InferState) pending ->
            match current.ContextInstances |> Map.tryFind pending.Operator with
            | Some [ Some (InstanceInScope written) ] ->
                let ty = pending.OperandTy
                let input = if pending.MemberName = ArithmeticOperators.negate then ty else inputOf ty
                { current with
                    ContextInstances = current.ContextInstances |> Map.remove pending.Operator
                    CalleeTypes = current.CalleeTypes |> Map.add pending.Operator (TyFunc(input, ty))
                    OperatorCalls = current.OperatorCalls |> Map.add pending.Operator pending.MemberName
                    OperatorInstances = current.OperatorInstances |> Map.add pending.Operator written }
            | _ -> current) state
