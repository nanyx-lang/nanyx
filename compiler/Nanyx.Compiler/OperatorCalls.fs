namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// Operators that call a definition. A type gets an operator by attaching it, `` def Vector.`+` = ... ``, and then
/// `v + w` on a `Vector` calls it. Where an operator's name is bound, as `` `+` `` is in a function that requires
/// `[Sum(a)]`, `x + y` calls that definition with its two operands, so generic code can add any type that has a
/// `+`. On a built-in type the operator is still the built-in one, whatever is in scope, so `count + 1` in that
/// same function adds ints.
///
/// `!=` has no definition of its own: it is `not (a == b)`, so it calls what `==` is bound to. Either calls it only
/// for operands the definition takes: in a function that requires `[Eq(a)]`, `==` on two `a`s calls the context's
/// member, and on two `list(a)`s is the built-in one (see `EqualityOperators`).
module internal OperatorCalls =
    /// Whether `scheme`, what `==` is bound to, compares values of the type `operandTy` (resolved as far as it can
    /// be), given as `input` (as a call's arguments are typed). The member of a context required at a type variable,
    /// `Eq(a)`, compares values of that variable, not of a type built from it, which unifying the two would not
    /// tell: an annotation's variables are checked to have stayed variables only once the def is inferred. Any other
    /// definition compares what a call to it with `input` would check for, as far as the constraints gathered so far
    /// tell.
    let private compares (state: InferState) (operandTy: Ty) (input: Ty) (scheme: TypeScheme) =
        let parameter =
            match Unifier.unalias (resolveSoFar state scheme.Type) with
            | TyFunc(TyRecord(fields, _), _, _) -> fields |> Map.tryFind "0"
            | _ -> None
        match parameter, operandTy with
        | Some (TyVar v), TyVar _ when not (scheme.Quantified.Contains v.Id) -> true
        | Some (TyVar v), _ when not (scheme.Quantified.Contains v.Id) -> false
        | _ ->
            let funcTy, next = instantiate state scheme
            let resultTy, next = freshVar next
            // An error earlier in the module is set aside (see `Solving.agreeing`).
            Solving.unify ((funcTy, TyFunc(input, resultTy, []), Equal, SourceSpan.none) :: Solving.agreeing next.Constraints) |> Result.isOk

    /// The definition the operator `expr` calls with its operands (`input`, as a call's arguments are typed), if it
    /// calls one, and the name it calls it by. On a built-in type, that of its left operand (`leftTy`) as far as it is
    /// known, the operator is the built-in one, unless it is a spare one (`^`, `<>`, ...), which has none, and so is `++` on a list. Otherwise it is the definition attached under the operator's name to
    /// that type, when `attached` finds one: `` Vector.`+` `` for `v + w`. Otherwise it is the binding of the
    /// operator's name in `env`. `!=` calls what `==` does, and `-x` what `negate` is. `and` and `or` are keywords, so
    /// their names are never bound. The ordering operators are always read through `Ord` (see `OrderingOperators`).
    ///
    /// `==` is a lookup of `Eq` at its operands' type, whose order puts an instance in scope before the type's own: a
    /// binding of `==` that takes the operands comes first, on a built-in type too, so after `use ignoringCase`, an
    /// `Eq(string)`, `a == b` on strings calls its `==`.
    ///
    /// On a type variable, the operator calls the member of the nearest instance in scope of a context of that
    /// variable that has it, which `instanceMember` finds, with that instance as written: in a function that requires
    /// `[Add(a), Add(b)]`, `x + y` on two `a`s calls the `` `+` `` of `Add(a)`, though `Add(b)`'s is bound to the
    /// name too.
    /// The context an instance, as `instanceMember` writes it, is of: `Add` of `Add(a)`.
    let private instanceContext (instance: TypeExpr) =
        match instance.Kind with
        | TypeApply(context, _)
        | TypeName context -> Some context
        | _ -> None

    let definition
        (instanceMember: Identifier -> Ty -> (TypeExpr * TypeScheme) option)
        (attached: Identifier -> Identifier option)
        (env: TypeEnv)
        (state: InferState)
        (expr: Expression)
        (leftTy: Ty)
        (input: Ty)
        : (Identifier * TypeScheme * TypeExpr option) option =
        let bound (name: Identifier) = TypeEnv.tryFind name env |> Option.map (fun scheme -> name, scheme, None)
        let name =
            match expr.Kind with
            | BinaryOp(op, _, _, _) when OrderingOperators.isOperator op -> None
            | BinaryOp(op, _, _, _) when EqualityOperators.isOperator op -> Some EqualityOperators.equals
            | BinaryOp(op, _, _, _) -> Some op
            | FunctionCall(name, _, [ _ ]) when name = NegateSyntax.builtIn -> Some ArithmeticOperators.negate
            | _ -> None
        let comparesOperands (operandTy: Ty) (_, scheme, _) = compares state operandTy input scheme
        // On a type variable, an operator reads its member only through the prelude's contexts: the one with the fewest
        // members that has it, or a larger one of them that has it too, as `Num` has `Add`'s `+`. Never through another
        // context's member of that name, a module's own `Add` included (see `OperatorContexts`, #2745).
        // An operand whose variable no instance is of yet, as a lambda's parameter is before the call it is passed to
        // settles it, reads the member bound to the operator's name, while one of those contexts is in scope.
        let readThroughPrelude (name: Identifier) (operandTy: Ty) =
            let preludeHaving = OperatorContexts.declared state |> List.filter (fun (_, members) -> members.Contains name) |> List.map fst
            let isPrelude (context: Identifier) = List.contains context preludeHaving
            match instanceMember name operandTy with
            | Some (instance, scheme) when instanceContext instance |> Option.exists isPrelude -> Some (name, scheme, Some instance)
            | Some _ -> None
            | None when env.Instances |> List.exists (fun binding -> instanceContext binding.Written |> Option.exists isPrelude) -> bound name
            | None -> None
        let inScope (name: Identifier) (operandTy: Ty) =
            if name = EqualityOperators.equals then bound name |> Option.filter (comparesOperands operandTy) else None
        match name, Unifier.unalias (resolveSoFar state leftTy) with
        | None, _ -> None
        | Some name, (TyVar _ as operandTy) when (OperatorContexts.smallest state name).IsSome -> readThroughPrelude name operandTy
        | Some name, operandTy when (inScope name operandTy).IsSome -> inScope name operandTy
        | Some name, TyPrimitive _ when not (isSpareOperator name) -> None
        | Some name, operandTy when name = AppendOperator.operator && AppendOperator.isBuiltIn operandTy -> None
        | Some name, operandTy ->
            match attached name with
            | Some attachedName -> bound attachedName
            | None when name = EqualityOperators.equals -> bound name |> Option.filter (comparesOperands operandTy)
            | None -> bound name

    /// `expr`, an operator that calls `scheme` by the name `callee`, of `instance` if it is an instance's member,
    /// typed as that call: the definition takes its operands (`input`) and the operator's value is what it returns.
    /// Each of two operands is checked against its parameter as a call's argument is, so that a tag widens into the
    /// union its parameter is: `#none >>= f` where `` `>>=` `` takes a `#some(a) | z`.
    let infer (state: InferState) (expr: Expression) (callee: Identifier) (scheme: TypeScheme) (instance: TypeExpr option) (input: Ty) : Ty * InferState =
        let funcTy, next = instantiate (recordReference expr scheme state) scheme
        let resultTy, next = freshVar next
        let contexts = match Unifier.unalias funcTy with TyFunc(_, _, contexts) -> contexts | _ -> []
        let next =
            match Unifier.unalias funcTy, input with
            | TyFunc(TyRecord(parameters, order), _, _), TyRecord(operands, _) when Set.ofSeq parameters.Keys = Set.ofSeq operands.Keys ->
                operands
                |> Map.fold (fun current field operandTy -> addAssignableConstraint operandTy parameters.[field] current)
                    (addConstraint funcTy (TyFunc(TyRecord(parameters, order), resultTy, contexts)) next)
            | _ -> addConstraint funcTy (TyFunc(input, resultTy, contexts)) next
        resultTy,
        { next with
            CalleeTypes = next.CalleeTypes |> Map.add expr.Id funcTy
            OperatorCalls = next.OperatorCalls |> Map.add expr.Id callee
            OperatorInstances =
                match instance with
                | Some instance -> next.OperatorInstances |> Map.add expr.Id instance
                | None -> next.OperatorInstances }

    /// The definitions attached to `ownTy`, the type of an arithmetic operator's left operand, that take its operands
    /// (`input`) as the operator would, by their bare names: the functions a type with no operator of its own gives
    /// for them instead, such as `Instant`'s `since`. Operators and the members of the well-known contexts, such as
    /// `compare`, are not among them.
    let private attachedTaking (env: TypeEnv) (state: InferState) (ownTy: Ty) (input: Ty) : Identifier list =
        let wellKnown = [ WellKnownContexts.equality; WellKnownContexts.ordering; WellKnownContexts.hashing ] |> List.map _.Member
        TypeResolution.attachedTypeNames ownTy
        |> List.collect (fun typeName ->
            env.Names
            |> Map.toList
            |> List.choose (fun (name, scheme) ->
                match name.Split('.', 2) with
                | [| owner; bare |] when owner = typeName && System.Char.IsLetter bare[0] && not (List.contains bare wellKnown) && compares state ownTy input scheme ->
                    Some bare
                | _ -> None))
        |> List.distinct

    /// The error for `expr`, the arithmetic operator `op` (or prefix `-`), with the operands `input`, when its left
    /// operand's type, `leftTy`, which `definition` finds the operator by, is a type of its own that is no number and
    /// that nothing has given `op`: neither a definition attached under its name nor an instance of the context it reads. Without it, the
    /// operator would be the built-in one, on `int`, and the error a bare mismatch with `int`. Where the type attaches
    /// definitions that take the operands, as `Instant` does `since`, it names them, which are the way to write it: an
    /// operator reads a context whose members give a value of the operands' type, which `Instant`'s `-` would not.
    /// Otherwise it says how to give the type the operator.
    let lacking (env: TypeEnv) (state: InferState) (expr: Expression) (op: string) (leftTy: Ty) (input: Ty) : Diagnostic option =
        match Unifier.unalias (resolveSoFar state leftTy) with
        | SizedNumberType _ -> None
        | TyNominal(id, _, _, _) as own when not (Unifier.isError own) -> Some (id.Name, own)
        | _ -> None
        |> Option.map (fun (typeName, own) ->
            let tyText = Unifier.tyToDisplayString Ascii own
            let instance =
                match OperatorContexts.smallest state (ArithmeticOperators.memberOf op) with
                | Some context -> $", or load an instance, `use {context}({tyText})(...)`"
                | None -> ""
            let message =
                if op = NegateSyntax.builtIn then
                    $"'{tyText}' has no prefix `-`: attach `{ArithmeticOperators.negate}`, as in `def {typeName}.{ArithmeticOperators.negate} = ...`{instance}"
                else
                    match attachedTaking env state own input with
                    | [] -> $"'{tyText}' has no `{op}`: attach one, as in ``def {typeName}.`{op}` = ...``{instance}"
                    | [ one ] -> $"'{tyText}' has no `{op}`: it attaches `{one}` for these operands, as in `x \\{one}(y)`"
                    | found ->
                        let named = found |> List.map (fun name -> $"`{name}`")
                        let listed = String.concat ", " (List.take (named.Length - 1) named) + " and " + List.last named
                        let written = found |> List.map (fun name -> $"`x \\{name}(y)`") |> String.concat " or "
                        $"'{tyText}' has no `{op}`: it attaches {listed} for these operands, as in {written}"
            Diagnostics.at DiagnosticCodes.TypeMismatch expr.Span message |> Diagnostics.about (NumericLiterals.written op))

    /// The error, at `span`, for the spare operator `op` (`^`, `<>`, `+=`, ...) on `left`, of the type `leftTy`, which
    /// nothing gives it a meaning on. `+=` is not assignment, so on a name that `isCell` says is a cell, the error says
    /// what is.
    let undefinedSpare (isCell: Identifier -> bool) (op: string) (span: SourceSpan) (left: Expression) (leftTy: Ty) : Diagnostic =
        let message =
            match left.Kind with
            | IdentifierExpr name when op = addInPlaceOperator && isCell name ->
                $"`+=` doesn't change a cell, and means only what a type attaches to it, as `MList` does to add to the list: write `{name} := {name} + ...`"
            | _ ->
                $"`{op}` has no built-in meaning, and '{Unifier.tyToString leftTy}' has no `{op}` attached: attach one to the type, as in ``def Money.`{op}` = ...``"
        Diagnostics.at DiagnosticCodes.UnknownFunction span message |> Diagnostics.about op

    /// `expr`, `-x` on an operand of the type `operandTy`: a call to the `negate` of that type or of the function's
    /// `Sub` (see `definition`), or else the negation of a built-in number. `numberTy` is the number that negation is
    /// of, `float` or `int`, as far as the operand's type is known.
    let rec inferNegation
        (instanceMember: Identifier -> Ty -> (TypeExpr * TypeScheme) option)
        (attached: Identifier -> Identifier option)
        (env: TypeEnv)
        (state: InferState)
        (expr: Expression)
        (operandTy: Ty)
        (numberTy: Ty)
        : Result<Ty * InferState, Diagnostic list> =
        match Unifier.unalias (resolveSoFar state operandTy) with
        // A measured value is negated as its storage is, and keeps its dimension (see `MeasuredArithmetic`).
        | TyMeasure(dimension, storage) ->
            inferNegation instanceMember attached env state expr storage storage
            |> Result.map (fun (negated, next) -> TyMeasure(dimension, negated), next)
        | _ ->
        match definition instanceMember attached env state expr operandTy operandTy with
        | Some (callee, scheme, instance) -> Ok (infer state expr callee scheme instance operandTy)
        | None ->
        match lacking env state expr NegateSyntax.builtIn operandTy operandTy with
        | Some error -> Error [ error ]
        | None ->
            ArithmeticOperators.check state expr NegateSyntax.builtIn operandTy operandTy operandTy numberTy
            |> Result.map (fun negated -> operandTy, negated)
