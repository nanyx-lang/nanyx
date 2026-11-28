namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// The contexts a top-level function that does not write its type requires for its arithmetic (see "Operator
/// contexts" in the docs' work in progress). An arithmetic operator on a type still a variable where it is written is
/// left pending (see `ArithmeticOperators.check`). When the function is generalised, an operator on one of the
/// function's own type variables makes it require the prelude's context the operator is read through (see
/// `OperatorContexts`), as if its type said so, and the operator calls the context's member: `def add = { x, y ->
/// x + y }` is `[Add(a)] a, a -> a`. Any other operator is on numbers, as it would have been where it is written.
/// `++` is read the same way, through `Append`, except that where it requires no context its operands are left as
/// they are.
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

    /// `state` with the operands of `pending`, now of the type `ty`, numbers: `int`, unless they are already `float` or
    /// a sized number. The operands of `++` are left as they are: what they are joined as is up to whatever else
    /// settles their type.
    let private asNumbers (pending: PendingArithmetic) (ty: Ty) (state: InferState) : InferState =
        match Unifier.unalias ty with
        | _ when pending.MemberName = AppendOperator.operator -> state
        | TyPrimitive("int" | "float")
        | SizedNumberType _ -> state
        | _ -> addConstraintAt pending.At pending.OperandTy (TyPrimitive "int") state

    /// `state` with the number literals the def about to be generalised settles made their default types, where nothing
    /// else has settled them (see `NumericLiterals`): every one so far for a top-level def, and for a local def those of
    /// a variable it would be generalised over, since a literal is not of every type. The def's operators are then
    /// read on what the literals are.
    let private settleLiterals (generalizeDef: InferState -> TypeScheme * InferState) (generalising: Generalising) (state: InferState) =
        match generalising with
        | _ when state.UnsettledLiterals.IsEmpty && state.BottomGiven.IsEmpty -> state
        | TopLevelDef _ -> Fractions.settleLiterals (fun _ -> true) state |> LowerBounds.settle (fun _ -> true)
        | LocalDef ->
            let tentative, _ = generalizeDef state
            let isOwn = function TyVar v -> tentative.Quantified.Contains v.Id | _ -> false
            Fractions.settleLiterals isOwn state |> LowerBounds.settle isOwn

    /// The type of the def about to be generalised, by `generalizeDef`, once the arithmetic operators left pending that
    /// the def settles (see `Generalising`) are read. An operator found to be on numbers constrains its operands, so
    /// the def is generalised again with them.
    ///
    /// A top-level def requires the contexts of each of its type variables its operators are on. Each `/` whose operands
    /// have turned out to be of a number type gives that type's fraction (see `Fractions`).
    let generalize (generalizeDef: InferState -> TypeScheme * InferState) (generalising: Generalising) (state: InferState) : TypeScheme * InferState =
        let generalizeDef = Fractions.settle >> generalizeDef
        let state = settleLiterals generalizeDef generalising state
        if state.PendingArithmetic.IsEmpty then generalizeDef state
        else
            let tentative, generalized = generalizeDef state
            match generalising with
            // A local def generic in nothing settles none of them: each is on a variable it is not generic in.
            | LocalDef when tentative.Quantified.IsEmpty -> tentative, generalized
            | _ ->
                let subst = fst (Solving.solve state.Constraints)
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
                        let requires = not onVariables.IsEmpty && writable ()
                        let operators = onVariables |> List.map (fun (pending, _) -> pending.Operator) |> Set.ofList
                        fun pending _ -> requires && operators.Contains pending.Operator
                let staying, numbers = resolved |> List.partition (fun (pending, ty) -> stays pending ty)
                let pending = staying |> List.map fst
                if numbers.IsEmpty then tentative, { generalized with PendingArithmetic = pending }
                else
                    numbers
                    |> List.fold (fun current (pending, ty) -> asNumbers pending ty current) { state with PendingArithmetic = pending }
                    |> generalizeDef

    /// The arguments for the associated types of `context` (see `AssociatedTypes`), which the def requires of its
    /// variable `v`, whose operators are `onV`, and `state` with them. `Num`'s `fraction` is what the `/`s among them give,
    /// which are made one type and are no longer pending (see `Fractions`); any other is a variable of its own.
    let private associatedArguments (state: InferState) (v: TyVar) (context: Identifier) (onV: PendingArithmetic list) : Ty list * InferState =
        let names = TypeResolution.tryFindTypeDef context state |> Option.map _.AssociatedTypes |> Option.defaultValue []
        names
        |> List.mapFold (fun (current: InferState) associatedName ->
            let divisions = onV |> List.filter (fun pending -> pending.MemberName = ArithmeticOperators.divide)
            match divisions with
            | first :: others when context = Fractions.context && associatedName = Fractions.associatedType ->
                let results = divisions |> List.map (fun pending -> resolveSoFar current pending.ResultTy) |> Set.ofList
                let made = others |> List.fold (fun next pending -> addConstraintAt pending.At pending.ResultTy first.ResultTy next) current
                first.ResultTy,
                { made with PendingFractions = made.PendingFractions |> List.filter (fun pending -> not (results.Contains (resolveSoFar current pending.ResultTy))) }
            | _ -> freshVar current) state

    /// The arithmetic operators of the top-level def `name`, generalised as `scheme`, that are on its own type
    /// variables, and `state` with each a call that requires its context of the variable, as a call to a function
    /// that requires one is: `ContextInference.resolvePending` then makes the def require it. The operators on one
    /// variable need the contexts `OperatorContexts.covering` gives for the members they read. A context with associated
    /// types is required with an argument for each (see `associatedArguments`).
    let require (state: InferState) (name: Identifier) (scheme: TypeScheme) : PendingArithmetic list * InferState =
        let own, others = state.PendingArithmetic |> List.partition (fun pending -> pending.InDef = name)
        let onVariables =
            own
            |> List.choose (fun pending ->
                match resolveSoFar state pending.OperandTy with
                | TyVar v when scheme.Quantified.Contains v.Id -> Some (pending, v)
                | _ -> None)
        // The contexts of each variable, for the members its operators read, worked out once for the variable.
        let contexts =
            onVariables
            |> List.groupBy (fun (_, v) -> v.Id)
            |> List.map (fun (id, operators) ->
                id,
                operators
                |> List.map (fun (pending, _) -> pending.MemberName)
                |> List.distinct
                |> OperatorContexts.covering state
                |> Option.defaultValue [])
            |> Map.ofList
        let contextsOf (v: TyVar) = contexts[v.Id]
        let associated, state =
            onVariables
            |> List.map snd
            |> List.distinctBy _.Id
            |> List.collect (fun v -> contextsOf v |> List.map (fun context -> v, context))
            |> List.mapFold (fun current (v, context) ->
                let arguments, next = associatedArguments current v context (own |> List.filter (fun pending -> resolveSoFar current pending.OperandTy = TyVar v))
                ((v.Id, context), arguments), next) state
        let associated = Map.ofList associated
        let calls =
            onVariables
            |> List.choose (fun (pending, v) ->
                OperatorContexts.having state (contextsOf v) pending.MemberName
                |> Option.map (fun context ->
                    pending,
                    { Def = name; Call = pending.Operator; Callee = pending.MemberName; Index = 0; Count = 1
                      Context = context; ArgTys = pending.OperandTy :: associated[(v.Id, context)]; Span = pending.At; Errors = []; ChecksOnly = false }))
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
            | Some [ Some (InstanceInScope(written, _)) ] ->
                let ty = pending.OperandTy
                let input = if pending.MemberName = ArithmeticOperators.negate then ty else inputOf ty
                { current with
                    ContextInstances = current.ContextInstances |> Map.remove pending.Operator
                    CalleeTypes = current.CalleeTypes |> Map.add pending.Operator (TyFunc(input, pending.ResultTy, []))
                    OperatorCalls = current.OperatorCalls |> Map.add pending.Operator pending.MemberName
                    OperatorInstances = current.OperatorInstances |> Map.add pending.Operator written }
            | _ -> current) state
