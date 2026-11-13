namespace NyxCompiler

open Parser.Program

/// Rewrites of a module's expressions, node by node: the walk itself, and the rewrites that write what inference
/// resolved into the syntax the backend reads.
module internal ExpressionRewrites =
    /// An expression with `visit` applied to every node, inner nodes first: `visit` is given the node as it was
    /// (for its id) and its kind with the children already visited, and returns the node's new kind.
    let mapExpressionKinds (visit: Expression -> ExpressionKind -> ExpressionKind) (expr: Expression) : Expression =
        let rec mapExpr (current: Expression) : Expression =
            { current with Kind = visit current (SyntaxTree.mapChildren mapExpr current).Kind }
        mapExpr expr

    /// Every pipe in an expression that inference resolved to an attached definition (`targets`, by the pipe's
    /// node) written with that definition's name, so the backend calls it: `x \trim` becomes `x \string.trim`. A
    /// hole that calls a derived definition (see `StructuralInstances.callTargets`) is written as a call to it.
    let retargetPipes (targets: Map<NodeId, Identifier>) (expr: Expression) : Expression =
        if targets.IsEmpty then
            expr
        else
            expr
            |> mapExpressionKinds (fun current kind ->
                match kind, targets |> Map.tryFind current.Id with
                | Pipe(value, _, rangeOpt, args), Some target -> Pipe(value, target, rangeOpt, args)
                | FunctionCall(_, rangeOpt, args), Some target -> FunctionCall(target, rangeOpt, args)
                | other, _ -> other)

    /// Every operator in an expression that inference found calls a definition (`calls`, by the operator's node, with
    /// the name it calls) written as that call, so the backend calls it: `x + y` becomes `` `+`(x, y) ``, or
    /// `` $Vector.`+`(x, y) ``, and `-x` becomes `negate(x)`. `x != y` calls what `==` does, and becomes
    /// `` not `==`(x, y) ``; the call keeps the operator's node, which is what its types are recorded by, and the
    /// operator's span as its name's. An operator that calls the member of an instance in scope (`instances`, by the
    /// operator's node) calls it through that instance, `` Add(a).`+`(x, y) ``, so that a function that requires
    /// `[Add(a) & Add(b)]` calls the right one.
    let callOperators (calls: Map<NodeId, Identifier>) (instances: Map<NodeId, TypeExpr>) (expr: Expression) : Expression =
        if calls.IsEmpty then
            expr
        else
            expr
            |> mapExpressionKinds (fun current kind ->
                let call (callee: Identifier) (opSpan: SourceSpan option) (args: Expression list) =
                    match instances |> Map.tryFind current.Id with
                    | Some instance -> ContextMemberCall(instance, callee, args)
                    | None -> FunctionCall(callee, opSpan, args)
                match kind, calls |> Map.tryFind current.Id with
                | BinaryOp(op, opSpan, left, right), Some callee when op = EqualityOperators.notEquals ->
                    FunctionCall(NotSyntax.builtIn, None, [ { current with Kind = call callee opSpan [ left; right ] } ])
                | BinaryOp(_, opSpan, left, right), Some callee -> call callee opSpan [ left; right ]
                | FunctionCall(_, span, [ operand ]), Some callee -> call callee span [ operand ]
                | other, _ -> other)

    /// Every expression that inference read as another (`readAs`, by the node of the one written) written as that
    /// one: `0` in `x + 0` becomes `zero`, `x ** n` becomes `__pow(x, n)` (see `ArithmeticOperators`), and a call of a
    /// map, `m(k)`, becomes `map.get(m, k)` (see `Maps`). A call an operator is read as takes the operands as they are
    /// rewritten, since one may be read as another too, and so does a call that a call is read as with the callee
    /// first: the map's lookup takes the call's arguments as they are rewritten. A call that is already what it is read as
    /// keeps its arguments as they are rewritten: `(x ** 2) ** 3`.
    let writeAsRead (readAs: Map<NodeId, Expression>) (expr: Expression) : Expression =
        if readAs.IsEmpty then
            expr
        else
            expr
            |> mapExpressionKinds (fun current kind ->
                match kind, readAs |> Map.tryFind current.Id with
                // `a ?? b` is read as a call that takes `b` in a lambda (see `CoalesceOperator`), which keeps its node.
                | CoalesceOperator.Call(left, right), Some { Kind = FunctionCall(callee, span, [ _; Kind (Lambda(parameters, _, takesUnit)) as fallback ]) } ->
                    FunctionCall(callee, span, [ left; { fallback with Kind = Lambda(parameters, right, takesUnit) } ])
                | BinaryOp(_, _, left, right), Some { Kind = FunctionCall(callee, span, [ _; _ ]) } -> FunctionCall(callee, span, [ left; right ])
                // The typed tree can hold the call already, as it does in a top-level def's value: it keeps its arguments
                // as they are rewritten, rather than becoming the call as first read, whose operands are not.
                | FunctionCall(name, _, args), Some { Kind = FunctionCall(callee, _, readArgs) } when name = callee && args.Length = readArgs.Length -> kind
                | FunctionCall(_, _, args), Some { Kind = FunctionCall(callee, span, receiver :: readArgs) } when readArgs.Length = args.Length ->
                    FunctionCall(callee, span, receiver :: args)
                | _, Some read -> read.Kind
                | _, None -> kind)

    /// The function that reads a built-in property: the string-length intrinsic, which needs no import, and the
    /// prelude's `list.length`, which every module imports.
    let builtInPropertyFunction (property: BuiltInProperty) : Identifier =
        match property with
        | StringLength -> Intrinsics.stringLength
        | ListLength -> "list.length"

    /// `expr` with each read of a built-in property (`properties`, by the member access's node) written as a call to
    /// the function that reads it, for the backend, which knows only records' fields: `s.length` is
    /// `__nanyx_string_length(s)`.
    let callBuiltInProperties (properties: Map<NodeId, BuiltInProperty>) (expr: Expression) : Expression =
        if properties.IsEmpty then
            expr
        else
            expr
            |> mapExpressionKinds (fun current kind ->
                match kind, properties |> Map.tryFind current.Id with
                | MemberAccess(target, _, rangeOpt), Some property -> FunctionCall(builtInPropertyFunction property, rangeOpt, [ target ])
                | other, _ -> other)

    /// The definitions that the instances synthesized for the calls in `expr` are made of (`instances`, by the call's
    /// node): `float.zero`, `` float.`+` ``, ... for a call in it that is passed a `Num(float)`. A derived instance is
    /// made of its definition and what the instances it is passed are made of.
    let synthesizedMembersIn (instances: Map<NodeId, ContextInstance option list>) (expr: Expression) : string list =
        let found = ResizeArray<string>()
        let rec add (node: NodeId) =
            for instance in instances |> Map.tryFind node |> Option.defaultValue [] do
                match instance with
                | Some (InstanceSynthesized members) -> found.AddRange(List.map snd members)
                | Some (InstanceDerived(_, definition, at)) ->
                    found.Add definition
                    add at
                | _ -> ()
        expr
        |> mapExpressionKinds (fun current kind ->
            add current.Id
            kind)
        |> ignore
        List.ofSeq found

    /// `expr` with its cells written as the backend reads them (see `CellSyntax`), by what inference found: `state`'s
    /// cells, the names that resolved to them, and the def each `mut` makes. A cell is captured when a lambda other than
    /// a `for` loop's body names it and does not declare it.
    let private writeCellsIn (state: InferState.InferState) (expr: Expression) : Expression =
        let within = SyntaxTree.expressionsWithin expr
        let cellOf (node: Expression) = state.References |> Map.tryFind node.Id |> Option.filter state.Cells.ContainsKey
        let loopBodies = within |> List.choose (fun node -> match node.Kind with ForSyntax.Call(_, loop, _, _) -> Some loop.Id | _ -> None) |> Set.ofList
        let lambdas = within |> List.filter (fun node -> match node.Kind with Lambda _ -> not (loopBodies.Contains node.Id) | _ -> false)
        // A cell passed with `mut x` is a box too, and so is a `mut` parameter, which is given one (see
        // `CellPassingSyntax`).
        let passed =
            within |> List.choose (fun node -> match node.Kind with CellPassingSyntax.Argument cell -> cellOf cell | _ -> None)
        let parameters =
            within |> List.choose cellOf |> List.filter (fun cell -> state.Symbols.[cell].Kind = Parameter)
        let captured =
            within
            |> List.choose (fun node -> cellOf node |> Option.map (fun cell -> node, state.Symbols.[cell].Declaration, cell))
            |> List.filter (fun (node, declared, _) ->
                lambdas |> List.exists (fun lambda -> SourceSpan.contains lambda.Span node.Span && not (SourceSpan.contains lambda.Span declared)))
            |> List.map (fun (_, _, cell) -> cell)
            |> List.append (passed @ parameters)
            |> Set.ofList
        let boxedValues =
            state.DeclarationSymbols
            |> Map.toSeq
            |> Seq.choose (fun ((node, declarer, _), symbol) -> if declarer = DefValue && captured.Contains symbol then Some node else None)
            |> Set.ofSeq
        expr
        |> mapExpressionKinds (fun current kind ->
            match kind with
            | IdentifierExpr _ ->
                match cellOf current with
                | Some cell -> FunctionCall((if captured.Contains cell then CellSyntax.unbox else CellSyntax.read), None, [ Expression.from current kind ])
                | None -> kind
            | AssignSyntax.Call(Kind (FunctionCall(form, _, [ cell ])), value) when form = CellSyntax.unbox -> FunctionCall(CellSyntax.setBox, None, [ cell; value ])
            | AssignSyntax.Call(Kind (FunctionCall(form, _, [ cell ])), value) when form = CellSyntax.read -> FunctionCall(AssignSyntax.builtIn, None, [ cell; value ])
            | MutSyntax.Call value when boxedValues.Contains current.Id -> FunctionCall(CellSyntax.box, None, [ value ])
            // Passing a cell passes its box.
            | CellPassingSyntax.Argument(Kind (FunctionCall(form, _, [ cell ]))) when form = CellSyntax.unbox -> cell.Kind
            | other -> other)

    /// The parameter given to a lambda written without parameters whose type takes one (see `nameIgnoredParameters`).
    let ignoredParameter = "__ignored"

    /// `module'` with each lambda written without parameters, `{ ... }`, that was checked against a type taking an input
    /// other than unit given a parameter it ignores, so that the backend compiles it as taking one: in
    /// `apply { "x" }`, where `apply` takes an `int -> string`, the lambda takes the int. `types` are the types of
    /// `module'`'s expressions as inference left them, and `subst` solves them.
    let nameIgnoredParameters (types: Map<NodeId, Ty>) (subst: Subst) (module': Module) : Module =
        let takesInput (lambda: Expression) =
            match types |> Map.tryFind lambda.Id |> Option.map (Unifier.apply subst >> Unifier.unalias) with
            | Some (TyFunc(input, _, _)) -> Unifier.unalias input <> TyPrimitive "unit"
            | _ -> false
        let rewrite =
            mapExpressionKinds (fun current kind ->
                match kind with
                | Lambda([], body, false) when takesInput current -> Lambda([ (ignoredParameter, None) ], body, false)
                | other -> other)
        module'
        |> List.map (fun item ->
            match item.Kind with
            | Def (ValueDef(isExport, name, typeOpt, value)) -> { item with Kind = Def (ValueDef(isExport, name, typeOpt, rewrite value)) }
            | Expr value -> { item with Kind = Expr (rewrite value) }
            | _ -> item)

    /// `module'` with the cells of its definitions written as the backend reads them (see `writeCellsIn`).
    let writeCells (state: InferState.InferState) (module': Module) : Module =
        if state.Cells.IsEmpty then
            module'
        else
            module'
            |> List.map (fun item ->
                match item.Kind with
                | Def (ValueDef(isExport, name, typeOpt, value)) -> { item with Kind = Def (ValueDef(isExport, name, typeOpt, writeCellsIn state value)) }
                | Expr value -> { item with Kind = Expr (writeCellsIn state value) }
                | _ -> item)
