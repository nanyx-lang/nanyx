namespace NyxCompiler

open Parser.Program

/// Rewrites of a module's expressions, node by node: the walk itself, and the rewrites that write what inference
/// resolved into the syntax the backend reads.
module internal ExpressionRewrites =
    /// An expression with `visit` applied to every node, inner nodes first: `visit` is given the node as it was
    /// (for its id) and its kind with the children already visited, and returns the node's new kind.
    let mapExpressionKinds (visit: Expression -> ExpressionKind -> ExpressionKind) (expr: Expression) : Expression =
        let rec mapExpr (current: Expression) : Expression =
            { current with Kind = visit current (mapKind current) }
        and mapKind current =
            match current.Kind with
            | IdentifierExpr _ -> current.Kind
            | FunctionCall(name, rangeOpt, args) ->
                FunctionCall(name, rangeOpt, args |> List.map mapExpr)
            | Lambda(parameters, body, takesUnit) ->
                Lambda(parameters, mapExpr body, takesUnit)
            | BinaryOp(op, left, right) ->
                BinaryOp(op, mapExpr left, mapExpr right)
            | Pipe(value, name, rangeOpt, args) ->
                Pipe(mapExpr value, name, rangeOpt, args |> List.map mapExpr)
            | Block statements ->
                Block(statements |> List.map mapStatement)
            | Match(values, arms) ->
                let mappedValues = values |> List.map mapExpr
                let mappedArms = arms |> List.map (fun (patterns, armExpr) -> patterns |> List.map (Pattern.mapExpressions mapExpr), mapExpr armExpr)
                Match(mappedValues, mappedArms)
            | Except(value, arms) ->
                Except(mapExpr value, arms |> List.map (fun (patterns, armExpr) -> patterns |> List.map (Pattern.mapExpressions mapExpr), mapExpr armExpr))
            | TupleExpr items -> TupleExpr(items |> List.map mapExpr)
            | RecordExpr fields ->
                RecordExpr(
                    fields
                    |> List.map (function
                        | NamedField(name, value) -> NamedField(name, mapExpr value)
                        | PositionalField value -> PositionalField(mapExpr value)))
            | ListExpr items -> ListExpr(items |> List.map mapExpr)
            | TagExpr(tagName, payloadOpt) -> TagExpr(tagName, payloadOpt |> Option.map mapExpr)
            | IfExpr(cond, thenExpr, elseExpr) ->
                IfExpr(mapExpr cond, mapExpr thenExpr, mapExpr elseExpr)
            | MemberAccess(baseExpr, fieldName, rangeOpt) -> MemberAccess(mapExpr baseExpr, fieldName, rangeOpt)
            | ContextMemberCall(ctxType, memberName, args) ->
                ContextMemberCall(ctxType, memberName, args |> List.map mapExpr)
            | UseIn(value, body) ->
                UseIn(mapExpr value, mapExpr body)
            | InterpolatedString parts ->
                InterpolatedString(
                    parts
                    |> List.map (function
                        | StringText text -> StringText text
                        | StringExpr value -> StringExpr(mapExpr value)))
            | WorkflowBindExpr(keyword, value) -> WorkflowBindExpr(keyword, mapExpr value)
            | WorkflowReturnExpr value -> WorkflowReturnExpr(mapExpr value)
            | ContextMemberAccess _
            | UnitExpr
            | LiteralExpr _ -> current.Kind
        and mapStatement (statement: Statement) =
            { statement with Kind = mapStatementKind statement.Kind }
        and mapStatementKind kind =
            match kind with
            | DefStatement(isExport, name, typeOpt, value) -> DefStatement(isExport, name, typeOpt, mapExpr value)
            | ExprStatement value -> ExprStatement(mapExpr value)
            | UseStatement value -> UseStatement(mapExpr value)
            | ImportStatement _
            | TypeDefStatement _ -> kind
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
    /// `` not `==`(x, y) ``; the call keeps the operator's node, which is what its types are recorded by. An operator
    /// that calls the member of an instance in scope (`instances`, by the operator's node) calls it through that
    /// instance, `` Add(a).`+`(x, y) ``, so that a function that requires `[Add(a) + Add(b)]` calls the right one.
    let callOperators (calls: Map<NodeId, Identifier>) (instances: Map<NodeId, TypeExpr>) (expr: Expression) : Expression =
        if calls.IsEmpty then
            expr
        else
            expr
            |> mapExpressionKinds (fun current kind ->
                let call (callee: Identifier) (args: Expression list) =
                    match instances |> Map.tryFind current.Id with
                    | Some instance -> ContextMemberCall(instance, callee, args)
                    | None -> FunctionCall(callee, None, args)
                match kind, calls |> Map.tryFind current.Id with
                | BinaryOp(op, left, right), Some callee when op = EqualityOperators.notEquals ->
                    FunctionCall(NotSyntax.builtIn, None, [ { current with Kind = call callee [ left; right ] } ])
                | BinaryOp(_, left, right), Some callee -> call callee [ left; right ]
                | FunctionCall(_, _, [ operand ]), Some callee -> call callee [ operand ]
                | other, _ -> other)

    /// Every expression that inference read as another (`readAs`, by the node of the one written) written as that
    /// one: `0` in `x + 0` becomes `zero`, `x ** n` becomes `__pow(x, n)` (see `ArithmeticOperators`), and a call of a
    /// map, `m(k)`, becomes `map.get(m, k)` (see `Maps`). A call an operator is read as takes the operands as they are
    /// rewritten, since one may be read as another too, and so does a call that a call is read as with the callee
    /// first: the map's lookup takes the call's arguments as they are rewritten.
    let writeAsRead (readAs: Map<NodeId, Expression>) (expr: Expression) : Expression =
        if readAs.IsEmpty then
            expr
        else
            expr
            |> mapExpressionKinds (fun current kind ->
                match kind, readAs |> Map.tryFind current.Id with
                | BinaryOp(_, left, right), Some { Kind = FunctionCall(callee, span, [ _; _ ]) } -> FunctionCall(callee, span, [ left; right ])
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
    /// node): `float.zero`, `` float.`+` ``, ... for a call in it that is passed a `Num(float)`.
    let synthesizedMembersIn (instances: Map<NodeId, ContextInstance option list>) (expr: Expression) : string list =
        let found = ResizeArray<string>()
        expr
        |> mapExpressionKinds (fun current kind ->
            for instance in instances |> Map.tryFind current.Id |> Option.defaultValue [] do
                match instance with
                | Some (InstanceSynthesized members) -> found.AddRange(List.map snd members)
                | _ -> ()
            kind)
        |> ignore
        List.ofSeq found

    /// `expr` with its cells written as the backend reads them (see `CellSyntax`), by what inference found: `state`'s
    /// cells, the names that resolved to them, and the def each `mut` makes. A cell is captured when a lambda other than
    /// a `for` loop's body names it and does not declare it.
    let private writeCellsIn (state: InferState.InferState) (expr: Expression) : Expression =
        let within = SyntaxTree.expressionsWithin expr
        let cellOf (node: Expression) = state.References |> Map.tryFind node.Id |> Option.filter state.Cells.Contains
        let loopBodies = within |> List.choose (fun node -> match node.Kind with ForSyntax.Call(_, loop, _, _) -> Some loop.Id | _ -> None) |> Set.ofList
        let lambdas = within |> List.filter (fun node -> match node.Kind with Lambda _ -> not (loopBodies.Contains node.Id) | _ -> false)
        let captured =
            within
            |> List.choose (fun node -> cellOf node |> Option.map (fun cell -> node, state.Symbols.[cell].Declaration, cell))
            |> List.filter (fun (node, declared, _) ->
                lambdas |> List.exists (fun lambda -> SourceSpan.contains lambda.Span node.Span && not (SourceSpan.contains lambda.Span declared)))
            |> List.map (fun (_, _, cell) -> cell)
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
