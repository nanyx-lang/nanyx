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
            | Lambda(parameters, body) ->
                Lambda(parameters, mapExpr body)
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
    /// node) written with that definition's name, so the backend calls it: `x \trim` becomes `x \String.trim`.
    let retargetPipes (targets: Map<NodeId, Identifier>) (expr: Expression) : Expression =
        if targets.IsEmpty then
            expr
        else
            expr
            |> mapExpressionKinds (fun current kind ->
                match kind, targets |> Map.tryFind current.Id with
                | Pipe(value, _, rangeOpt, args), Some target -> Pipe(value, target, rangeOpt, args)
                | other, _ -> other)

    /// Every operator in an expression that inference found calls a definition (`calls`, by the operator's node)
    /// written as that call, so the backend calls it: `x + y` becomes `` `+`(x, y) ``. `x != y` calls what `==` is
    /// bound to, and becomes `` not `==`(x, y) ``; the call keeps the operator's node, which is what its types are
    /// recorded by.
    let callOperators (calls: Set<NodeId>) (expr: Expression) : Expression =
        if calls.IsEmpty then
            expr
        else
            expr
            |> mapExpressionKinds (fun current kind ->
                match kind with
                | BinaryOp(op, left, right) when calls.Contains current.Id && op = EqualityOperators.notEquals ->
                    FunctionCall(NotSyntax.builtIn, None, [ { current with Kind = FunctionCall(EqualityOperators.equals, None, [ left; right ]) } ])
                | BinaryOp(op, left, right) when calls.Contains current.Id -> FunctionCall(op, None, [ left; right ])
                | other -> other)

    /// The function that reads a built-in property: the string-length intrinsic, which needs no import, and the
    /// prelude's `List.length`, which every module imports.
    let builtInPropertyFunction (property: BuiltInProperty) : Identifier =
        match property with
        | StringLength -> Intrinsics.stringLength
        | ListLength -> "List.length"

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
