namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// Number literals of the type the code around them expects (see "Sized numbers" in the docs' work in progress):
/// `def small: U8 = 200`. A literal's type is a variable of its own (`typeOf`), which the code around it settles. One
/// that nothing settles is `int`, or `float` with a decimal point, once the def it is in is generalised (`settle`); a
/// number with a unit is `int` when it is a whole number of its dimension's default storage unit that fits one.
/// Once every type is known, each literal is checked (`check`): an integer literal must be of an integer type, and fit
/// it, and a decimal one of a floating-point type.
module internal NumericLiterals =
    /// How many default storage units of its dimension the number with a unit `number unitName` is, exactly.
    let inStorageUnits (state: InferState) (number: WrittenNumber) (unitName: Identifier) : Rational =
        let scale = state.Units |> Map.tryFind unitName |> Option.map _.Scale |> Option.defaultValue Rational.one
        Rational.multiply (Rational.ofWritten number) scale

    /// The type a literal is when nothing settles it. A number with a unit is stored as an `int` when it is a whole
    /// number of its dimension's default storage unit that fits one, however it is written: `0.5s` and `500ms` are both
    /// 500000000 `ns`. One that is a fraction of that unit, `0.25ns`, or too many of it for an `int`, is a `float`.
    let private defaultType (state: InferState) (literal: Literal) =
        match literal with
        | FloatLit _ -> TyPrimitive "float"
        | UnitLit(number, unitName) ->
            let value = inStorageUnits state number unitName
            if Rational.isWhole value && value.Numerator >= bigint System.Int64.MinValue && value.Numerator <= bigint System.Int64.MaxValue then
                TyPrimitive "int"
            else
                TyPrimitive "float"
        | _ -> TyPrimitive "int"

    /// The type each of `literals` is when nothing settles it. A number with a unit of the same type (a variable `resolve`
    /// gives) as one that is a `float` is a `float` too, so that `2.0 * 1.5s` scales a `Time(float)` and `[0.5s, 0.25ns]`
    /// holds two. An integer literal with no unit stays an `int`, as `1` is never a `float`.
    let private sharedDefaults (state: InferState) (resolve: Ty -> Ty) (literals: NumericLiteral list) : NumericLiteral -> Ty =
        let varOf (literal: NumericLiteral) = match resolve literal.Type with TyVar v -> Some v.Id | _ -> None
        let floatVars =
            literals
            |> List.filter (fun literal -> defaultType state literal.Value = TyPrimitive "float")
            |> List.choose varOf
            |> Set.ofList
        fun literal ->
            match literal.Value, varOf literal with
            | UnitLit _, Some id when floatVars.Contains id -> TyPrimitive "float"
            | value, _ -> defaultType state value

    /// The type of `literal`, written at `span` (as the expression `node`, unless it is a pattern), and `state` with it
    /// recorded: a variable of its own for a number, and the literal's type for any other. For a number with a unit, it
    /// is the type of its storage, which the measured value's type is of (see the `UnitLit` arm in `Infer.fs`).
    let typeOf (state: InferState) (span: SourceSpan) (node: NodeId option) (literal: Literal) : Ty * InferState =
        match literal with
        | IntLit _
        | FloatLit _
        | UnitLit _ ->
            let ty, next = freshVar state
            let recorded = { Type = ty; Value = literal; At = span; Node = node; Operator = None; Requires = None }
            ty, { next with NumericLiterals = recorded :: next.NumericLiterals; UnsettledLiterals = recorded :: next.UnsettledLiterals }
        | StringLit _
        | BoolLit _ -> TypeResolution.literalType literal, state

    /// Whether `ty` is a type that is known not to be a number. A type variable, an open record and a nominal type
    /// are left to unification, which reports them if they are not numbers; the error type is what failed to check, and
    /// `undefined` never gives a value to use.
    let isKnownNonNumber (ty: Ty) =
        match Unifier.unalias ty with
        | ty when Unifier.isError ty || Unifier.isBottom ty -> false
        | TyPrimitive("int" | "float")
        | SizedNumberType _
        | TyVar _
        | TyOpenRecord _
        | TyNominal _ -> false
        | _ -> true

    /// The operator `op` as a diagnostic writes it: prefix `-` is written `-`.
    let written (op: string) = if op = NegateSyntax.builtIn then "-" else op

    /// The error for using `op` on a value of the type `ty`, which is not a number.
    let notANumber (op: string) (ty: Ty) =
        let advice =
            match op, Unifier.unalias ty with
            | "+", (TyPrimitive "string" | Lists.ListType _) -> ": use `++` to join them"
            | _ -> ""
        $"'{Unifier.tyToDisplayString Ascii ty}' is not a number, so it cannot be used with {written op}{advice}"

    /// The error for `op`, which needs `required.Context`, on values of the type variable `v` of the function
    /// `required.Def`, whose type does not require it, with the fix that adds it.
    let undeclared (state: InferState) (at: SourceSpan) (op: string) (required: OperandRequirement) (v: TyVar) =
        let tyText = Unifier.tyToDisplayString Ascii (TyVar v)
        let context = $"{required.Context}({tyText})"
        Diagnostics.at DiagnosticCodes.MissingContextMembers at
            $"'{required.Def}' {required.Does} values of type '{tyText}' with {written op}, so its type must require {context}: add [{context}] to the type of '{required.Def}'"
        |> Diagnostics.about required.Context
        |> ContextFixes.withAddToType state required.Def context

    /// The variables the type annotation being read names, as `subst` solves them, each with its name.
    let private writtenVariables (subst: Subst) (state: InferState) : Map<int, string> =
        state.TypeVars
        |> Map.toList
        |> List.choose (fun (key, var) ->
            match key, Unifier.applySolved subst (TyVar var) with
            | WrittenVar name, TyVar solved -> Some (solved.Id, name)
            | _ -> None)
        |> Map.ofList

    /// A list of literals `settleOf` left unsettled, as it found them: the variables they were of, each once, and the
    /// constraints that solved them so. A list holds the literals of a function's earlier lines, so each of its local defs
    /// would otherwise look at all of them again (#2407).
    type private Unsettled = { Vars: TyVar list; Constraints: ConstraintSet }

    /// What `settleOf` found of each list it left unsettled, for the literals (`false`) and for the operands (`true`).
    let private unsettledLists =
        [ false; true ] |> List.map (fun operands -> operands, System.Runtime.CompilerServices.ConditionalWeakTable<NumericLiteral list, Unsettled>()) |> Map.ofList

    /// How many constraints, at most, `settlesNone` looks through for those it found a list's literals unsettled under.
    let private constraintsLookedThrough = 4096

    /// Whether `literals`, the literals (or operands) `isFor` says `settleOf` is settling, are certainly all still of a
    /// variable `openVar` gives, one of their own the def being generalised is not generic in, so that none settles: it
    /// then gives each variable they are of, once. Where they extend a list whose literals were all found so, under
    /// constraints the current ones extend, each of those is of what its variable then is now, so only the variables are
    /// looked at, and the literals in front of them. Otherwise, or where one may settle, it gives nothing, and `settleOf`
    /// looks at each literal.
    let private settlesNone (operands: bool) (isFor: NumericLiteral -> bool) (openVar: Ty -> TyVar option) (constraints: ConstraintSet) (literals: NumericLiteral list) =
        let found = unsettledLists[operands]
        let rec earlier (newer: NumericLiteral list) (remaining: NumericLiteral list) =
            match found.TryGetValue remaining with
            | true, known -> Some (newer, known)
            | false, _ ->
                match remaining with
                | [] -> None
                | literal :: rest -> earlier (literal :: newer) rest
        let rec extends (looked: int) (current: ConstraintSet) (known: ConstraintSet) =
            obj.ReferenceEquals(current, known)
            || (looked < constraintsLookedThrough
                && match current with
                   | _ :: rest -> extends (looked + 1) rest known
                   | [] -> false)
        match earlier [] literals with
        | Some (newer, known) when extends 0 constraints known.Constraints ->
            let vars =
                (newer |> List.filter isFor |> List.map _.Type) @ (known.Vars |> List.map TyVar) |> List.map openVar
            if vars |> List.forall Option.isSome then Some (vars |> List.choose id |> List.distinctBy _.Id) else None
        | _ -> None

    /// Keeps what `settleOf` found of `literals`, under `constraints`: the variables those it is settling are of.
    let private keepUnsettled (operands: bool) (vars: TyVar list) (literals: NumericLiteral list) (constraints: ConstraintSet) =
        if not literals.IsEmpty then unsettledLists[operands].AddOrUpdate(literals, { Vars = vars; Constraints = constraints })

    /// `settle` for the literals, or else for the operators' operands (`operands`). Operands that turned out to be of a
    /// variable the def's annotation names, which a `for` over an `[Iterable(i)] i` makes its associated `elem` only once
    /// the loop's instance is found, fail to check, as they would have had they been of it when the operator was read.
    let private settleOf (operands: bool) (isOwn: Ty -> bool) (state: InferState) : InferState =
        let subst, failures = Solving.solve state.Constraints
        let isFor (literal: NumericLiteral) = literal.Operator.IsSome = operands
        let openVar (ty: Ty) =
            match Unifier.applySolved subst ty with
            | TyVar ({ Name = None } as v) when not (isOwn (TyVar v)) -> Some v
            | _ -> None
        let settleEach () =
            let written = if operands then writtenVariables subst state else Map.empty
            let settling, unsettled = state.UnsettledLiterals |> List.partition (fun literal -> isFor literal && (openVar literal.Type).IsNone)
            keepUnsettled operands (unsettled |> List.filter isFor |> List.choose (_.Type >> openVar) |> List.distinctBy _.Id) unsettled state.Constraints
            let defaultOf = sharedDefaults state (Unifier.applySolved subst) settling
            settling
            |> List.fold (fun current literal ->
                match Unifier.applySolved subst literal.Type, literal.Operator with
                | TyVar v, Some (op, node) when literal.Requires.IsSome && written.ContainsKey v.Id ->
                    let error = undeclared current literal.At op literal.Requires.Value { v with Name = Some written.[v.Id] }
                    { current with FailedExprs = (node, [ error ]) :: current.FailedExprs }
                | TyVar _, _ -> addConstraint literal.Type (defaultOf literal) current
                | ty, Some (op, node) when isKnownNonNumber ty ->
                    let error = Diagnostics.at DiagnosticCodes.TypeMismatch literal.At (notANumber op ty)
                    { current with FailedExprs = (node, [ error ]) :: current.FailedExprs }
                | _ -> current) { state with UnsettledLiterals = unsettled }
        // Under constraints that fail, a later solution need not agree with an earlier one, so each literal is looked at.
        match (if failures.IsEmpty then settlesNone operands isFor openVar state.Constraints state.UnsettledLiterals else None) with
        | Some vars ->
            keepUnsettled operands vars state.UnsettledLiterals state.Constraints
            state
        | None -> settleEach ()

    /// `state` with each literal not yet settled whose type is still a variable of its own, and one `isOwn` says the
    /// def being generalised has, made its default type. So is one of a variable a type annotation names, which a
    /// literal cannot be of, since it is not of every type: the def's annotation is then more general than its value.
    /// A literal of any other type is settled as it is.
    ///
    /// The operands of an operator left for the code around them to settle (see `awaitOperand`) are settled the same
    /// way, once the literals are, so that `1.5` in the def makes them `float` rather than both defaulting. Those settled
    /// to a type known not to be a number fail to check at the operator.
    let settle (isOwn: Ty -> bool) (state: InferState) : InferState =
        if state.UnsettledLiterals.IsEmpty then state
        else state |> settleOf false isOwn |> settleOf true isOwn

    /// `state` with the operands of the arithmetic operator `op`, the expression `expr`, of the type `ty`, which is still
    /// a variable, left for the code around them to settle, as a literal is: they are `int` if nothing in their def
    /// does (#1485). In a def that writes its type, they need what `requires` says of one of its variables.
    let awaitOperand (op: string) (expr: Expression) (ty: Ty) (requires: OperandRequirement option) (state: InferState) : InferState =
        let recorded = { Type = ty; Value = IntLit 0I; At = expr.Span; Node = None; Operator = Some (op, expr.Id); Requires = requires }
        { state with UnsettledLiterals = recorded :: state.UnsettledLiterals }

    /// The literals not yet settled whose type is still a variable of their own, each with that variable, newest first.
    /// Each is resolved only as it is read, so a search for one stops at it: a long chain of `+` has many literals.
    let private openLiterals (state: InferState) =
        state.UnsettledLiterals
        |> Seq.choose (fun literal ->
            match resolveSoFar state literal.Type with
            | TyVar ({ Name = None } as v) -> Some (literal, v)
            | _ -> None)

    /// Whether `v` is the own variable of a number literal whose type nothing has settled yet.
    let isUnsettledVar (state: InferState) (v: TyVar) =
        state.UnsettledLiterals |> List.exists (fun literal -> resolveSoFar state literal.Type = TyVar v)

    /// Whether `ty` is, so far, a literal's own variable: a number, but of a type the code around it has not settled.
    let isOpen (state: InferState) (ty: Ty) =
        match resolveSoFar state ty with
        | TyVar v -> openLiterals state |> Seq.exists (fun (_, other) -> other.Id = v.Id)
        | _ -> false

    /// The literal written in the code that `ty` is, so far, the own variable of, a decimal one first if there is one. An
    /// operand an operator left for the code around it to settle (see `awaitOperand`) is no literal.
    let writtenLiteral (state: InferState) (ty: Ty) : Literal option =
        match resolveSoFar state ty with
        | TyVar v ->
            let written =
                openLiterals state
                |> Seq.filter (fun (literal, other) -> other.Id = v.Id && literal.Operator.IsNone)
                |> Seq.map (fun (literal, _) -> literal.Value)
                |> List.ofSeq
            written |> List.tryFind (function FloatLit _ -> true | _ -> false) |> Option.orElse (List.tryHead written)
        | _ -> None

    /// The literals of a variable of its own, as `state` resolves it, that is in `tys`.
    let openIn (tys: Ty list) (state: InferState) : NumericLiteral list =
        let inTys = tys |> List.map (resolveSoFar state >> TypeEnv.freeTyVars) |> Set.unionMany
        // Resolving every literal costs as much as there are, so a type with no variable, as `int` in `n == 1`, looks
        // at none of them: a long else-if over an `int` has a literal in each branch (#2362).
        if inTys.IsEmpty then []
        else openLiterals state |> Seq.filter (fun (_, v) -> inTys.Contains v.Id) |> Seq.map fst |> List.ofSeq

    /// `state` with each of `literals` made its default type.
    let settleLiterals (literals: NumericLiteral list) (state: InferState) : InferState =
        let defaultOf = sharedDefaults state (resolveSoFar state) literals
        literals |> List.fold (fun current literal -> addConstraint literal.Type (defaultOf literal) current) state

    /// The literals with no unit not yet settled that are where a measured value is, `isLong(0)` for a `Time`, each with
    /// the measured type's storage, still a variable, that is in `tys`. `check` reports such a literal, and makes its
    /// storage the literal's kind.
    let private measuredIn (tys: Ty list) (state: InferState) : (NumericLiteral * TyVar) list =
        let inTys = tys |> List.map (resolveSoFar state >> TypeEnv.freeTyVars) |> Set.unionMany
        if inTys.IsEmpty then []
        else
            state.UnsettledLiterals
            |> List.choose (fun literal ->
                match literal.Value, resolveSoFar state literal.Type with
                | (IntLit _ | FloatLit _), TyMeasure(_, TyVar storage) when inTys.Contains storage.Id -> Some (literal, storage)
                | _ -> None)

    /// `state` with each literal of a variable of its own that is in `tys` made its default type, if there is one: where
    /// a call finds the instance of a context of the literal's type, the type cannot wait for the rest of the def. A
    /// literal with no unit where a measured value is has its storage made its kind, as `check` makes it, so that what
    /// the measured value requires of its storage, `Num` of it, is found and not reported too.
    let settleIn (tys: Ty list) (state: InferState) : InferState option =
        match openIn tys state, measuredIn tys state with
        | [], [] -> None
        | settling, measured ->
            let state = settleLiterals settling state
            Some (measured |> List.fold (fun current (literal, storage) -> addConstraint (TyVar storage) (defaultType current literal.Value) current) state)

    /// Whether a value of the type `ty`, as far as `state` knows it, can be of the type `target`: `ty` is `target`, or
    /// it is still a literal's own variable and `target` is a number of the literal's kind.
    let canBe (state: InferState) (ty: Ty) (target: Ty) =
        match resolveSoFar state ty with
        | TyVar v ->
            match openLiterals state |> Seq.tryFind (fun (_, other) -> other.Id = v.Id), Unifier.unalias target with
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

    /// The error for the plain number literal `literal`, at `span`, where a value of the dimension `dimension` is
    /// expected: a number with no unit is never a measured value.
    let private withoutUnit (span: SourceSpan) (literal: Literal) (dimension: Dimension) : Diagnostic =
        let written = match literal with IntLit value -> string value | FloatLit value -> string value | _ -> "it"
        Diagnostics.at DiagnosticCodes.TypeMismatch span
            $"'{written}' has no unit, so it is no '{Dimension.text dimension}': write it with a unit of '{Dimension.text dimension}', or `zero` for nothing of it"

    /// What the number with a unit `number unitName`, at `span`, stored as `ty`, is at run time, the plain number of
    /// default storage units it is, or why it can't be stored so: a fraction of a default storage unit is no integer,
    /// and one outside the storage's range doesn't fit it.
    let private storedAs (state: InferState) (span: SourceSpan) (number: WrittenNumber) (unitName: Identifier) (ty: Ty) : Result<Literal, Diagnostic> =
        let value = inStorageUnits state number unitName
        // A derived dimension has no default storage unit of its own: it is stored in its parts', multiplied and
        // divided, `m/ns`.
        let baseOf (dimension: Dimension) =
            state.Units |> Map.tryFindKey (fun _ other -> other.Dimension = dimension && UnitInfo.isStorage other)
        let partBase (id: NominalId) = baseOf (Dimension.ofBase id)
        let baseUnit =
            state.Units
            |> Map.tryFind unitName
            |> Option.bind (fun info ->
                if not (Dimension.isDerived info.Dimension) then baseOf info.Dimension
                elif info.Dimension |> List.forall (fst >> partBase >> Option.isSome) then
                    Some (Dimension.writtenWith (partBase >> Option.get) "*" "/" info.Dimension)
                else None)
            |> Option.defaultValue "storage unit"
        let written = UnitSyntax.written number unitName
        match Unifier.unalias ty with
        | TyPrimitive "int"
        | SizedIntegerType _ when not (Rational.isWhole value) ->
            let fix =
                if number.Decimal then "store it as a `float`"
                else $"write it as a decimal, `{WrittenNumber.text { number with Decimal = true; Digits = number.Digits * 10I; Exponent = number.Exponent - 1 }}{unitName}`, to store it as a `float`"
            Error (
                Diagnostics.at DiagnosticCodes.TypeMismatch span
                    $"'{written}' is {Rational.text value} of a '{baseUnit}', which is no whole number of '{baseUnit}', so it can't be stored as '{Unifier.tyToString ty}': {fix}")
        | TyPrimitive "int"
        | SizedIntegerType _ ->
            let integer = IntLit value.Numerator
            match outOfRange { Type = ty; Value = integer; At = span; Node = None; Operator = None; Requires = None } ty with
            | Some message -> Error (Diagnostics.at DiagnosticCodes.LiteralOutOfRange span $"'{written}' is {value.Numerator} '{baseUnit}': {message}")
            | None -> Ok integer
        | _ -> Ok (FloatLit (Rational.toFloat value))

    /// `state` with every literal settled, and each that cannot be of the type it has, as the constraints solve it, an
    /// error. A number with a unit is stored as its number of default storage units, the plain literal it is read as
    /// from then on (`ReadAs`). An integer literal where a floating-point type is expected is an error here, with a
    /// fix. One of any other type of another kind is constrained to its default type at the literal, so that the
    /// conflict is reported as any other is, with the module's constraints. One out of its type's range is an error
    /// here. An error here that is at an expression fails to check there, and the errors of the others, which are
    /// patterns, are given back.
    let check (state: InferState) : InferState * Diagnostic list =
        let settled = settle (fun _ -> true) state
        let subst = fst (Solving.solve settled.Constraints)
        let report (literal: NumericLiteral) (error: Diagnostic) (current: InferState, errors: Diagnostic list) =
            match literal.Node with
            | Some node -> { current with FailedExprs = (node, [ error ]) :: current.FailedExprs }, errors
            | None -> current, errors @ [ error ]
        settled.NumericLiterals
        |> List.rev
        |> List.fold (fun (current: InferState, errors) literal ->
            let ty = Unifier.applySolved subst literal.Type
            match literal.Value, Unifier.unalias ty, outOfRange literal ty with
            | UnitLit(number, unitName), (TyPrimitive ("int" | "float") | SizedNumberType _ as resolved), _ ->
                match storedAs current literal.At number unitName resolved, literal.Node with
                | Ok stored, Some node -> { current with ReadAs = current.ReadAs |> Map.add node (Expression.at literal.At (LiteralExpr stored)) }, errors
                | Ok _, None -> current, errors
                | Error error, _ -> report literal error (current, errors)
            | UnitLit _, resolved, _ when Unifier.isError resolved -> current, errors
            | UnitLit _, _, _ -> addConstraintAt literal.At literal.Type (defaultType current literal.Value) current, errors
            // Its storage is the literal's kind, so that what the measured value requires of it is found, and not reported too.
            | (IntLit _ | FloatLit _), TyMeasure(dimension, storage), _ ->
                report literal (withoutUnit literal.At literal.Value dimension) (addConstraintAt literal.At storage (defaultType current literal.Value) current, errors)
            | IntLit value, TyPrimitive ("float" | "F32"), _ -> report literal (integerAsFloat literal.At value ty) (current, errors)
            | _ when not (ofItsKind literal ty) -> addConstraintAt literal.At literal.Type (defaultType current literal.Value) current, errors
            | _, _, None -> current, errors
            | _, _, Some message -> report literal (Diagnostics.at DiagnosticCodes.LiteralOutOfRange literal.At message) (current, errors)) (settled, [])
