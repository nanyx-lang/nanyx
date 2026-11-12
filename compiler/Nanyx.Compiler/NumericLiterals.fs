namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// Number literals of the type the code around them expects (see "Sized numbers" in the docs' work in progress):
/// `def small: U8 = 200`. A literal's type is a variable of its own (`typeOf`), which the code around it settles. One
/// that nothing settles is `int`, or `float` with a decimal point, once the def it is in is generalised (`settle`).
/// Once every type is known, each literal is checked (`check`): an integer literal must be of an integer type, and fit
/// it, and a decimal one of a floating-point type.
module internal NumericLiterals =
    /// The type a literal is when nothing settles it.
    let private defaultType (literal: Literal) =
        match literal with
        | FloatLit _ -> TyPrimitive "float"
        | _ -> TyPrimitive "int"

    /// The type of `literal`, written at `span` (as the expression `node`, unless it is a pattern), and `state` with it
    /// recorded: a variable of its own for a number, and the literal's type for any other.
    let typeOf (state: InferState) (span: SourceSpan) (node: NodeId option) (literal: Literal) : Ty * InferState =
        match literal with
        | IntLit _
        | FloatLit _ ->
            let ty, next = freshVar state
            let recorded = { Type = ty; Value = literal; At = span; Node = node }
            ty, { next with NumericLiterals = recorded :: next.NumericLiterals; UnsettledLiterals = recorded :: next.UnsettledLiterals }
        | StringLit _
        | BoolLit _ -> TypeResolution.literalType literal, state

    /// `state` with each literal not yet settled whose type is still a variable of its own, and one `isOwn` says the
    /// def being generalised has, made its default type. So is one of a variable a type annotation names, which a
    /// literal cannot be of, since it is not of every type: the def's annotation is then more general than its value.
    /// A literal of any other type is settled as it is.
    let settle (isOwn: Ty -> bool) (state: InferState) : InferState =
        if state.UnsettledLiterals.IsEmpty then state
        else
            let subst = fst (Unifier.solve state.Constraints)
            let isOpen (literal: NumericLiteral) =
                match Unifier.apply subst literal.Type with
                | TyVar { Name = None } -> true
                | _ -> false
            let isNamedVariable (literal: NumericLiteral) =
                match Unifier.apply subst literal.Type with
                | TyVar { Name = Some _ } -> true
                | _ -> false
            let settling, unsettled =
                state.UnsettledLiterals |> List.partition (fun literal -> not (isOpen literal) || isOwn (Unifier.apply subst literal.Type))
            settling
            |> List.filter (fun literal -> isOpen literal || isNamedVariable literal)
            |> List.fold (fun current literal -> addConstraint literal.Type (defaultType literal.Value) current) { state with UnsettledLiterals = unsettled }

    /// The literals not yet settled whose type is still a variable of their own, each with that variable.
    let private openLiterals (state: InferState) =
        state.UnsettledLiterals
        |> List.choose (fun literal ->
            match resolveSoFar state literal.Type with
            | TyVar ({ Name = None } as v) -> Some (literal, v)
            | _ -> None)

    /// Whether `ty` is, so far, a literal's own variable: a number, but of a type the code around it has not settled.
    let isOpen (state: InferState) (ty: Ty) =
        match resolveSoFar state ty with
        | TyVar v -> openLiterals state |> List.exists (fun (_, other) -> other.Id = v.Id)
        | _ -> false

    /// The literals of a variable of its own, as `state` resolves it, that is in `tys`.
    let openIn (tys: Ty list) (state: InferState) : NumericLiteral list =
        let inTys = tys |> List.map (resolveSoFar state >> TypeEnv.freeTyVars) |> Set.unionMany
        openLiterals state |> List.filter (fun (_, v) -> inTys.Contains v.Id) |> List.map fst

    /// `state` with each of `literals` made its default type.
    let settleLiterals (literals: NumericLiteral list) (state: InferState) : InferState =
        literals |> List.fold (fun current literal -> addConstraint literal.Type (defaultType literal.Value) current) state

    /// `state` with each literal of a variable of its own that is in `tys` made its default type, if there is one: where
    /// a call finds the instance of a context of the literal's type, the type cannot wait for the rest of the def.
    let settleIn (tys: Ty list) (state: InferState) : InferState option =
        match openIn tys state with
        | [] -> None
        | settling -> Some (settleLiterals settling state)

    /// Whether a value of the type `ty`, as far as `state` knows it, can be of the type `target`: `ty` is `target`, or
    /// it is still a literal's own variable and `target` is a number of the literal's kind.
    let canBe (state: InferState) (ty: Ty) (target: Ty) =
        match resolveSoFar state ty with
        | TyVar v ->
            match openLiterals state |> List.tryFind (fun (_, other) -> other.Id = v.Id), Unifier.unalias target with
            | Some ({ Value = IntLit _ }, _), (TyPrimitive "int" | SizedIntegerType _)
            | Some ({ Value = FloatLit _ }, _), TyPrimitive ("float" | "F32") -> true
            | Some _, _ -> false
            | None, _ -> TyVar v = target
        | resolved -> resolved = target

    /// `state` with each literal of a variable of its own that is in `tys` made its default type: where the type is
    /// read before the rest of the def can settle it, as a pipe reads it to pick the attached function it calls.
    let settleWithin (tys: Ty list) (state: InferState) : InferState = settleIn tys state |> Option.defaultValue state

    /// The state an operator on operands of the types `left` and `right` reads them in, and that state with the two made
    /// one type. Only arithmetic leaves a literal to be whatever number the code around it settles: any other operator
    /// reads its operands' type, once they are one type, so a literal still of its own variable then is of its default
    /// type, as in `1 == 2`, while `x == 1` is `x`'s. An operand that failed to check says nothing of the other, so the
    /// two are not made one type, and a literal there is of its default type.
    let atOperator (arithmetic: bool) (left: Ty) (right: Ty) (state: InferState) : InferState * InferState =
        let state = if arithmetic then state else settleLiterals (openIn [ left ] (addConstraint left right state)) state
        let constrained =
            if [ left; right ] |> List.exists (fun ty -> Unifier.isError (resolveSoFar state ty)) then settleWithin [ left; right ] state
            else addConstraint left right state
        state, constrained

    /// The literal `value`, as it is written, and the smallest and largest values of the integer type `name`.
    let private range (value: bigint) (name: string) (smallest: bigint) (largest: bigint) =
        if value < smallest || value > largest then Some $"'{value}' does not fit in {name} ({smallest} to {largest})"
        else None

    /// Why `literal` does not fit its type `ty`, if it does not.
    let private outOfRange (literal: NumericLiteral) (ty: Ty) : string option =
        match literal.Value, Unifier.unalias ty with
        | IntLit value, TyPrimitive "int" -> range value "int" (bigint System.Int64.MinValue) (bigint System.Int64.MaxValue)
        | IntLit value, SizedIntegerType integer ->
            range value (SizedInteger.name integer) (SizedInteger.minValue integer) (SizedInteger.maxValue integer)
        | FloatLit value, TyPrimitive "F32" when System.Single.IsInfinity(float32 value) && not (System.Double.IsInfinity value) ->
            let written = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            Some $"'{written}' does not fit in F32: it is past the largest F32, about 3.4e38"
        | _ -> None

    /// Whether `ty` can be a literal of `literal`'s kind: an integer type for an integer literal, and a floating-point
    /// one for a decimal literal.
    let private ofItsKind (literal: NumericLiteral) (ty: Ty) =
        match literal.Value, Unifier.unalias ty with
        | IntLit _, (TyPrimitive "int" | SizedIntegerType _)
        | FloatLit _, TyPrimitive ("float" | "F32") -> true
        | _, ty -> Unifier.isError ty

    /// The error for an integer literal `value`, at `span`, where the floating-point type `ty` is expected: it is not a
    /// float, as in Rust, and the fix writes it as one.
    let private integerAsFloat (span: SourceSpan) (value: bigint) (ty: Ty) : Diagnostic =
        let tyText = Unifier.tyToDisplayString Ascii ty
        { Diagnostics.at DiagnosticCodes.TypeMismatch span $"'{value}' is an integer literal, so it cannot be a '{tyText}': write '{value}.0'" with
            Fixes = [ { Title = $"Write `{value}.0`"; Edits = [ { Span = span; NewText = $"{value}.0" } ] } ] }

    /// `state` with every literal settled, and each that cannot be of the type it has, as the constraints solve it, an
    /// error. An integer literal where a floating-point type is expected is an error here, with a fix. One of any other
    /// type of another kind is constrained to its default type at the literal, so that the conflict is reported as any
    /// other is, with the module's constraints. One out of its type's range is an error here. An error here that is at
    /// an expression fails to check there, and the errors of the others, which are patterns, are given back.
    let check (state: InferState) : InferState * Diagnostic list =
        let settled = settle (fun _ -> true) state
        let subst = fst (Unifier.solve settled.Constraints)
        let report (literal: NumericLiteral) (error: Diagnostic) (current: InferState, errors: Diagnostic list) =
            match literal.Node with
            | Some node -> { current with FailedExprs = (node, [ error ]) :: current.FailedExprs }, errors
            | None -> current, errors @ [ error ]
        settled.NumericLiterals
        |> List.rev
        |> List.fold (fun (current: InferState, errors) literal ->
            let ty = Unifier.apply subst literal.Type
            match literal.Value, Unifier.unalias ty, outOfRange literal ty with
            | IntLit value, TyPrimitive ("float" | "F32"), _ -> report literal (integerAsFloat literal.At value ty) (current, errors)
            | _ when not (ofItsKind literal ty) -> addConstraintAt literal.At literal.Type (defaultType literal.Value) current, errors
            | _, _, None -> current, errors
            | _, _, Some message -> report literal (Diagnostics.at DiagnosticCodes.LiteralOutOfRange literal.At message) (current, errors)) (settled, [])
