namespace NyxCompiler

open System
open Parser.Program
open NyxCompiler.ExpressionRewrites

/// Where in a builder's block a workflow keyword is when the rest of the block cannot be its continuation.
type Unreachable =
    /// Inside a lambda, which runs when it is called, not where it is written.
    | InLambda
    /// In the body of a `for` loop, which runs once per element.
    | InLoopBody
    /// In the condition or the body of a `while` loop, which run again each time round.
    | InWhileLoop
    /// In a part of an expression that may not run: a branch of an `if` or an arm of a `match` that is part of a
    /// larger expression, or the right of `and` or `or`.
    | InConditionalPart

module internal IdentifierNames =
    /// `name` with every character that cannot be in an identifier made `_`, and `_` in front if it starts with a digit.
    let sanitizeIdentifier (name: string) =
        let sanitized =
            name
            |> Seq.map (fun c -> if Char.IsLetterOrDigit c || c = '_' then c else '_')
            |> Seq.toArray
            |> String
        if String.IsNullOrWhiteSpace sanitized then
            "_"
        elif Char.IsDigit sanitized.[0] then
            "_" + sanitized
        else
            sanitized

/// Desugaring before type checking: workflow keywords into calls to their builder's members, `for` loops, a match's
/// lone `else` and the holes of interpolated strings.
module WorkflowDesugaring =
    /// `module'` desugared, with the errors desugaring found (a workflow keyword the rest of its builder's block cannot
    /// follow, see `Unreachable`); where each builder block falls off its end without `return`, by the node of the
    /// made-up read of the builder's `zero` that is the block's value there, the place in the block to report a
    /// missing `zero` at; and the expressions of the desugared module made up for each node of `module'`, in the order
    /// they were made: a workflow bind's are the call to its builder's member and the continuation passed to it.
    let desugarWithDiagnostics (module': Module) : Module * Diagnostic list * Map<NodeId, SourceSpan> * Map<NodeId, NodeId list> =
        let isReturnKeyword keyword = keyword = WorkflowSyntax.returnKeyword
        let diagnostics = ResizeArray<Diagnostic>()
        let mutable zeros: Map<NodeId, SourceSpan> = Map.empty

        // The expressions made up for each node of `module'`, newest first, and the node of `module'` each one was
        // made for. One made for a made-up node was made for the node that one was.
        let mutable lowered: Map<NodeId, NodeId list> = Map.empty
        let mutable madeForNode: Map<NodeId, NodeId> = Map.empty
        let recordMade (source: NodeId) (made: Expression) =
            let surface = madeForNode |> Map.tryFind source |> Option.defaultValue source
            madeForNode <- madeForNode |> Map.add made.Id surface
            lowered <- lowered |> Map.add surface (made.Id :: (lowered |> Map.tryFind surface |> Option.defaultValue []))
            made
        // `Expression.from source kind`, recorded as made for `source`.
        let madeFor (source: Expression) (kind: ExpressionKind) = recordMade source.Id (Expression.from source kind)

        // The builder whose block is being desugared (`proc` in `proc { ... }`), and whether that block used a
        // workflow keyword. A keyword `k` in the block calls the builder's member `k`, `return` its member `return`.
        let mutable builder: string option = None
        let mutable builderUsed = false

        // Where the expression being desugared is, when the rest of the builder's block cannot follow a keyword
        // there: inside a lambda, which runs when it is called; in the body of a `for` loop, which runs once per
        // element; or in a part of an expression that may not run, as a branch of an `if` that is not a statement
        // of its own. A builder block nested there starts afresh. The outermost place is the one that says why: a `try`
        // in an arm of a `for` loop's patterns is in the loop's body, whatever else it is in.
        let mutable unreachable: Unreachable option = None
        let within (place: Unreachable) (desugarIt: unit -> 'a) : 'a =
            let outer = unreachable
            unreachable <- outer |> Option.orElse (Some place)
            let result = desugarIt ()
            unreachable <- outer
            result

        // The error for the keyword `keyword` at `expr`, which the rest of the block of the builder `builderName`
        // cannot follow from `place`.
        let reportUnreachable (expr: Expression) (keyword: string) (builderName: string) (place: Unreachable) =
            let where, advice =
                match place with
                | InLambda -> "inside a lambda", "move it out of the lambda, or handle the value with a `match` there"
                | InLoopBody -> "in the body of a `for` loop", "handle the value with a `match` in the loop's body"
                | InWhileLoop -> "in a `while` loop", "handle the value with a `match` in the loop"
                | InConditionalPart -> "in a part of an expression that may not run", "make the `if` or `match` it is in a statement of its own"
            diagnostics.Add(
                Diagnostics.at DiagnosticCodes.InvalidWorkflow expr.Span
                    $"`{keyword}` is {where}, so the rest of the `{builderName}` block cannot follow it: {advice}")
        let mutable freshNames = 0
        let freshName prefix =
            freshNames <- freshNames + 1
            $"__{prefix}_{freshNames}"

        // `k(args)` for the workflow keyword `k`: a call to the member `k` of the builder the block was written
        // after, through a local of its own (`def m = proc.try` then `m(args)`), so that each use instantiates the
        // builder's type afresh. Outside a builder's block it is a call to a function named `k`, and an error if `k`
        // is one of the language's own keywords, `try` or `return`, which no function can be named.
        let workflowCall (source: Expression) (keyword: string) (args: Expression list) : Expression =
            match builder with
            | Some builderName ->
                builderUsed <- true
                let memberLocal = freshName $"{IdentifierNames.sanitizeIdentifier builderName}_{keyword}"
                let memberAccess = madeFor source (MemberAccess(madeFor source (IdentifierExpr builderName), keyword, None))
                madeFor source (
                    Block [
                        Statement.from source (DefStatement(false, memberLocal, None, memberAccess))
                        Statement.from source (ExprStatement (madeFor source (FunctionCall(memberLocal, None, args))))
                    ])
            | None ->
                if keyword = WorkflowSyntax.tryKeyword || isReturnKeyword keyword then
                    let keywordSpan = { source.Span with End = source.Span.Start + keyword.Length }
                    diagnostics.Add(
                        Diagnostics.at DiagnosticCodes.WorkflowKeywordOutsideWorkflow keywordSpan
                            $"`{keyword}` is a workflow keyword, so it can only be used inside a builder block, such as `proc {{ ... }}`")
                madeFor source (FunctionCall(keyword, None, args))

        // Whether `expr` uses a workflow keyword of the block it is in: one not inside a lambda, or inside a builder
        // block of its own.
        let rec usesKeyword (expr: Expression) : bool =
            let inValue (statement: Statement) =
                match statement.Kind with
                | DefStatement(_, _, _, value)
                | ExprStatement value
                | UseStatement value -> usesKeyword value
                | ImportStatement _
                | TypeDefStatement _ -> false
            match expr.Kind with
            | WorkflowBindExpr _
            | WorkflowReturnExpr _ -> true
            | Lambda _ -> false
            | FunctionCall(name, _, [ Kind (Lambda([], _, _)) ]) when not (name.StartsWith "__") -> false
            | FunctionCall(_, _, args)
            | ContextMemberCall(_, _, args) -> List.exists usesKeyword args
            | Pipe(value, _, _, args) -> List.exists usesKeyword (value :: args)
            | BinaryOp(_, _, left, right) -> usesKeyword left || usesKeyword right
            | IfExpr(cond, thenExpr, elseExpr) -> usesKeyword cond || usesKeyword thenExpr || usesKeyword elseExpr
            | Match(values, arms) -> List.exists usesKeyword values || arms |> List.exists (snd >> usesKeyword)
            | Except(value, arms) -> usesKeyword value || arms |> List.exists (snd >> usesKeyword)
            | Block statements -> List.exists inValue statements
            | TupleExpr items
            | ListExpr items -> List.exists usesKeyword items
            | RecordExpr fields -> fields |> List.exists (function NamedField(_, value) | PositionalField value -> usesKeyword value)
            | TagExpr(_, Some payload) -> usesKeyword payload
            | MemberAccess(baseExpr, _, _) -> usesKeyword baseExpr
            | UseIn(_, body) -> usesKeyword body
            | InterpolatedString parts -> parts |> List.exists (function StringExpr value -> usesKeyword value | StringText _ -> false)
            | TagExpr(_, None)
            | ContextMemberAccess _
            | UnitExpr
            | LiteralExpr _
            | IdentifierExpr _ -> false

        // Whether `expr` is an `if` or a `match` with a workflow keyword in a branch: as a statement of its own, each
        // branch goes on to the rest of the block (see `desugarBranching`).
        let branchesUseKeywords (expr: Expression) =
            match expr.Kind with
            | IfExpr(_, thenExpr, elseExpr) -> usesKeyword thenExpr || usesKeyword elseExpr
            | Match(_, arms)
            | Except(_, arms) -> arms |> List.exists (snd >> usesKeyword)
            | _ -> false

        // A workflow bind nested in an expression, as in `return R(a = try f())`, becomes a bind of a fresh name
        // just before its statement, and the expression refers to that name. Binds are lifted from the places an
        // expression always evaluates, inner ones before outer ones and left to right; not from lambdas, nested
        // blocks, `while` loops, the branches of an `if` or a `match`, or the right of `and`/`or`.
        let rec liftBinds (expr: Expression) : Statement list * Expression =
            let liftAll exprs =
                let lifted = exprs |> List.map liftBinds
                List.collect fst lifted, List.map snd lifted
            let rebuild kind = { expr with Kind = kind }
            match expr.Kind with
            | WorkflowBindExpr(keyword, value) when not (isReturnKeyword keyword) ->
                let prefix, value' = liftBinds value
                let name = freshName keyword
                prefix @ [ Statement.from expr (DefStatement(false, name, None, { expr with Kind = WorkflowBindExpr(keyword, value') })) ],
                madeFor expr (IdentifierExpr name)
            | WorkflowBindExpr(keyword, value) ->
                let prefix, value' = liftBinds value
                prefix, rebuild (WorkflowBindExpr(keyword, value'))
            | WorkflowReturnExpr value ->
                let prefix, value' = liftBinds value
                prefix, rebuild (WorkflowReturnExpr value')
            // A `while` loop's condition and body run again each time round.
            | WhileSyntax.Call _ -> [], expr
            | FunctionCall(name, rangeOpt, args) ->
                let prefix, args' = liftAll args
                prefix, rebuild (FunctionCall(name, rangeOpt, args'))
            | ContextMemberCall(ctxType, field, args) ->
                let prefix, args' = liftAll args
                prefix, rebuild (ContextMemberCall(ctxType, field, args'))
            | BinaryOp(op, opSpan, left, right) when isShortCircuit op ->
                let prefix, left' = liftBinds left
                prefix, rebuild (BinaryOp(op, opSpan, left', right))
            | BinaryOp(op, opSpan, left, right) ->
                match liftAll [ left; right ] with
                | prefix, [ left'; right' ] -> prefix, rebuild (BinaryOp(op, opSpan, left', right'))
                | _ -> [], expr
            | Pipe(value, name, rangeOpt, args) ->
                match liftAll (value :: args) with
                | prefix, value' :: args' -> prefix, rebuild (Pipe(value', name, rangeOpt, args'))
                | _ -> [], expr
            | TupleExpr items ->
                let prefix, items' = liftAll items
                prefix, rebuild (TupleExpr items')
            | ListExpr items ->
                let prefix, items' = liftAll items
                prefix, rebuild (ListExpr items')
            | RecordExpr fields ->
                let lifted =
                    fields
                    |> List.map (function
                        | NamedField(name, value) ->
                            let prefix, value' = liftBinds value
                            prefix, NamedField(name, value')
                        | PositionalField value ->
                            let prefix, value' = liftBinds value
                            prefix, PositionalField value')
                List.collect fst lifted, rebuild (RecordExpr(List.map snd lifted))
            | TagExpr(name, Some payload) ->
                let prefix, payload' = liftBinds payload
                prefix, rebuild (TagExpr(name, Some payload'))
            | MemberAccess(baseExpr, field, rangeOpt) ->
                let prefix, baseExpr' = liftBinds baseExpr
                prefix, rebuild (MemberAccess(baseExpr', field, rangeOpt))
            // One with a keyword in a branch, which is not a statement of its own here (as the value a destructuring
            // def matches on is not), becomes a def of a fresh name just before the statement, which the branching
            // rewrite handles (see `desugarBranching`).
            | IfExpr _
            | Match _
            | Except _ ->
                let prefix, expr' = liftScrutinee expr
                if branchesUseKeywords expr' then
                    let name = freshName "branch"
                    prefix @ [ Statement.from expr (DefStatement(false, name, None, expr')) ], madeFor expr (IdentifierExpr name)
                else
                    prefix, expr'
            | InterpolatedString parts ->
                let lifted =
                    parts
                    |> List.map (function
                        | StringText text -> [], StringText text
                        | StringExpr value ->
                            let prefix, value' = liftBinds value
                            prefix, StringExpr value')
                List.collect fst lifted, rebuild (InterpolatedString(List.map snd lifted))
            | TagExpr(_, None)
            | Lambda _
            | Block _
            | UseIn _
            | ContextMemberAccess _
            | UnitExpr
            | LiteralExpr _
            | IdentifierExpr _ -> [], expr

        // An `if` or a `match` with the binds in the parts of it that always run lifted out: its condition, or the
        // values it matches. Its branches only run when chosen.
        and liftScrutinee (expr: Expression) : Statement list * Expression =
            let rebuild kind = { expr with Kind = kind }
            match expr.Kind with
            | IfExpr(cond, thenExpr, elseExpr) ->
                let prefix, cond' = liftBinds cond
                prefix, rebuild (IfExpr(cond', thenExpr, elseExpr))
            | Match(values, arms) ->
                let lifted = values |> List.map liftBinds
                List.collect fst lifted, rebuild (Match(List.map snd lifted, arms))
            | Except(value, arms) ->
                let prefix, value' = liftBinds value
                prefix, rebuild (Except(value', arms))
            | _ -> [], expr

        // The statement with the binds nested in it lifted out before it. A bind that is a whole statement
        // (`def a = try f()`, or `try f()` on its own) stays where it is, and so does an `if` or a `match`: only binds
        // inside its value are lifted.
        let liftStatementBinds (statement: Statement) : Statement list =
            let liftValue (value: Expression) =
                match value.Kind with
                | WorkflowBindExpr(keyword, bound) when not (isReturnKeyword keyword) ->
                    let prefix, bound' = liftBinds bound
                    prefix, { value with Kind = WorkflowBindExpr(keyword, bound') }
                | IfExpr _
                | Match _
                | Except _ -> liftScrutinee value
                | _ -> liftBinds value
            match statement.Kind with
            | DefStatement(isExport, name, typeOpt, value) ->
                let prefix, value' = liftValue value
                prefix @ [ { statement with Kind = DefStatement(isExport, name, typeOpt, value') } ]
            | ExprStatement value ->
                let prefix, value' = liftValue value
                prefix @ [ { statement with Kind = ExprStatement value' } ]
            | UseStatement _
            | ImportStatement _
            | TypeDefStatement _ -> [ statement ]

        // A workflow bind or return becomes a call to the workflow's members, whose type is not that of the bind as
        // written (a bind's call is the whole rest of the workflow), so that call is a node of its own, with a new id.
        // Everything else keeps its node's id.
        let rec desugarExpr (expr: Expression) : Expression =
            match expr.Kind with
            | WorkflowBindExpr _
            | WorkflowReturnExpr _ -> madeFor expr (desugarKind expr)
            | _ -> { expr with Kind = desugarKind expr }
        and desugarKind expr =
            match expr.Kind with
            // A block written after a builder's name, `proc { ... }`: when the block uses a workflow keyword, it
            // is the block itself, with its keywords calling the builder's members (see `workflowCall`). The
            // documented `proc.apply({ ... })` around it is not made yet: the block runs where it is written.
            | FunctionCall(name, rangeOpt, [ Kind (Lambda([], body, takesUnit)) as lambda ]) when not (name.StartsWith "__") ->
                let outerBuilder, outerUsed, outerUnreachable = builder, builderUsed, unreachable
                builder <- Some name
                builderUsed <- false
                unreachable <- None
                let body' = desugarExpr body
                let used = builderUsed
                builder <- outerBuilder
                builderUsed <- outerUsed
                unreachable <- outerUnreachable
                if used then body'.Kind
                else FunctionCall(name, rangeOpt, [ { lambda with Kind = Lambda([], body', takesUnit) } ])
            // A `for` loop's body is a lambda of the elements, which the loop runs once per step.
            | ForSyntax.Call(iterables, loop, parameters, body) ->
                let body' = within InLoopBody (fun () -> desugarExpr body)
                FunctionCall(ForSyntax.builtIn, None, List.map desugarExpr iterables @ [ { loop with Kind = Lambda(parameters, body', false) } ])
            | WhileSyntax.Call(condition, body) ->
                FunctionCall(WhileSyntax.builtIn, None, within InWhileLoop (fun () -> [ desugarExpr condition; desugarExpr body ]))
            | FunctionCall(name, rangeOpt, args) ->
                FunctionCall(name, rangeOpt, args |> List.map desugarExpr)
            | Lambda(args, body, takesUnit) ->
                Lambda(args, within InLambda (fun () -> desugarExpr body), takesUnit)
            // The right of `and`, `or` and `??` only runs when the left does not settle the result.
            | BinaryOp(op, opSpan, left, right) when isShortCircuit op ->
                BinaryOp(op, opSpan, desugarExpr left, within InConditionalPart (fun () -> desugarExpr right))
            | BinaryOp(op, opSpan, left, right) ->
                BinaryOp(op, opSpan, desugarExpr left, desugarExpr right)
            | Pipe(value, name, rangeOpt, args) ->
                Pipe(desugarExpr value, name, rangeOpt, args |> List.map desugarExpr)
            | Block statements ->
                Block(desugarBlock statements)
            // An arm, like a branch of an `if`, only runs when it is chosen. One that is a statement of its own is
            // desugared by `desugarBranching` instead when it uses a keyword.
            // A lone `else` in a match on several values becomes one per value (`PatternSpace.ofArm`).
            | Match(exprs, arms) ->
                let mappedExprs = exprs |> List.map desugarExpr
                let mappedArms =
                    arms
                    |> List.map (fun (patterns, expr) ->
                        PatternSpace.ofArm exprs.Length patterns |> List.map (Pattern.mapExpressions desugarExpr),
                        within InConditionalPart (fun () -> desugarExpr expr))
                Match(mappedExprs, mappedArms)
            | Except(value, arms) -> desugarKind { expr with Kind = Except.asMatch expr value arms }
            | TupleExpr items -> TupleExpr(items |> List.map desugarExpr)
            | RecordExpr fields ->
                let mappedFields =
                    fields
                    |> List.map (function
                        | NamedField(name, value) -> NamedField(name, desugarExpr value)
                        | PositionalField value -> PositionalField(desugarExpr value))
                RecordExpr mappedFields
            | ListExpr items -> ListExpr(items |> List.map desugarExpr)
            | TagExpr(name, payload) -> TagExpr(name, payload |> Option.map desugarExpr)
            | IfExpr(cond, thenExpr, elseExpr) ->
                IfExpr(
                    desugarExpr cond,
                    within InConditionalPart (fun () -> desugarExpr thenExpr),
                    within InConditionalPart (fun () -> desugarExpr elseExpr))
            | MemberAccess(baseExpr, field, rangeOpt) -> MemberAccess(desugarExpr baseExpr, field, rangeOpt)
            | ContextMemberAccess(ctxType, field) -> ContextMemberAccess(ctxType, field)
            | ContextMemberCall(ctxType, field, args) -> ContextMemberCall(ctxType, field, args |> List.map desugarExpr)
            | UseIn(value, body) -> UseIn(desugarExpr value, desugarExpr body)
            // A keyword the rest of its builder's block cannot follow is an error; the value stands in for it, as
            // nothing is checked once desugaring fails.
            | WorkflowBindExpr(keyword, value) ->
                match builder, unreachable with
                | Some builderName, Some place ->
                    reportUnreachable expr keyword builderName place
                    (desugarExpr value).Kind
                | _ -> (workflowCall expr keyword [desugarExpr value]).Kind
            | WorkflowReturnExpr value ->
                match builder, unreachable with
                | Some builderName, Some place ->
                    reportUnreachable expr WorkflowSyntax.returnKeyword builderName place
                    (desugarExpr value).Kind
                | _ -> (workflowCall expr WorkflowSyntax.returnKeyword [desugarExpr value]).Kind
            // Each hole calls the prelude's hole function, which converts the value through `ToString`.
            | InterpolatedString parts ->
                let mappedParts =
                    parts
                    |> List.map (function
                        | StringText text -> StringText text
                        | StringExpr hole -> StringExpr (madeFor hole (FunctionCall(InterpolationHoles.holeFunction, None, [ desugarExpr hole ]))))
                InterpolatedString mappedParts
            | UnitExpr
            | LiteralExpr _
            | IdentifierExpr _ -> expr.Kind
        and desugarStatement (stmt: Statement) =
            match stmt.Kind with
            | DefStatement(isExport, name, typeOpt, expr) ->
                let expr' =
                    match expr.Kind with
                    | WorkflowBindExpr(keyword, value) when isReturnKeyword keyword ->
                        workflowCall expr WorkflowSyntax.returnKeyword [desugarExpr value]
                    | _ -> desugarExpr expr
                { stmt with Kind = DefStatement(isExport, name, typeOpt, expr') }
            | ExprStatement expr -> { stmt with Kind = ExprStatement (desugarExpr expr) }
            | UseStatement value -> { stmt with Kind = UseStatement (desugarExpr value) }
            | ImportStatement _
            | TypeDefStatement _ -> stmt
        // The statements of a block from its first workflow keyword on, as that keyword's call with the rest of the
        // block as its continuation, and so on. A block that falls off its end without `return` has the builder's
        // `zero` as its value there (`#some()` for `proc`), its last value only run; outside a builder's block, `()`.
        and desugarWorkflowChain (statements: Statement list) =
            let ending =
                match builder with
                | Some builderName ->
                    let zero = Expression.synthetic (MemberAccess(Expression.synthetic (IdentifierExpr builderName), "zero", None))
                    let place = statements |> List.tryLast |> Option.map _.Span |> Option.defaultValue SourceSpan.none
                    zeros <- zeros |> Map.add zero.Id place
                    builderUsed <- true
                    zero
                | None -> Expression.synthetic UnitExpr
            desugarChain statements (fun last ->
                match last with
                | Some value -> madeFor value (Block [ Statement.from value (ExprStatement value); Statement.synthetic (ExprStatement ending) ])
                | None -> ending)
        // `statements` as a workflow chain that goes on to `next` at its end, given the value of its last statement
        // if that is an expression. A keyword takes the rest of the chain as its continuation; `return` ends the
        // whole block, so what follows it, `next` included, never runs.
        and desugarChain (statements: Statement list) (next: Expression option -> Expression) : Expression =
            match statements with
            | [] -> next None
            | statement :: rest ->
                let restOfChain () = desugarChain rest next
                match statement.Kind with
                | DefStatement(isExport, name, typeOpt, expr) ->
                    match expr.Kind with
                    | WorkflowBindExpr(keyword, bindExpr) when isReturnKeyword keyword ->
                        workflowCall expr WorkflowSyntax.returnKeyword [desugarExpr bindExpr]
                    | WorkflowBindExpr(keyword, bindExpr) ->
                        workflowCall expr keyword [desugarExpr bindExpr; madeFor expr (Lambda([name, None], restOfChain (), false))]
                    | _ when branchesUseKeywords expr -> desugarBranching expr (Some name) rest next
                    | _ ->
                        chainBlock statement (restOfChain ()) { statement with Kind = DefStatement(isExport, name, typeOpt, desugarExpr expr) }
                | ExprStatement expr ->
                    match expr.Kind with
                    | WorkflowReturnExpr value -> workflowCall expr WorkflowSyntax.returnKeyword [desugarExpr value]
                    | WorkflowBindExpr(keyword, bindExpr) when isReturnKeyword keyword ->
                        workflowCall expr WorkflowSyntax.returnKeyword [desugarExpr bindExpr]
                    // The last statement's bound value is the chain's value.
                    | WorkflowBindExpr(keyword, bindExpr) when rest.IsEmpty ->
                        let value = freshName "value"
                        let continuation = next (Some (madeFor expr (IdentifierExpr value)))
                        workflowCall expr keyword [desugarExpr bindExpr; madeFor expr (Lambda([value, None], continuation, false))]
                    | WorkflowBindExpr(keyword, bindExpr) ->
                        workflowCall expr keyword [desugarExpr bindExpr; madeFor expr (Lambda([("_", None)], restOfChain (), false))]
                    | _ when branchesUseKeywords expr -> desugarBranching expr None rest next
                    | _ when rest.IsEmpty -> next (Some (desugarExpr expr))
                    | _ ->
                        chainBlock statement (restOfChain ()) { statement with Kind = ExprStatement (desugarExpr expr) }
                | UseStatement _
                | ImportStatement _
                | TypeDefStatement _ ->
                    chainBlock statement (restOfChain ()) (desugarStatement statement)
        // An `if` or a `match` statement with a workflow keyword in a branch, `expr`, followed by `rest`. The rest of
        // the block, up to `next`, is bound once to a local function, and each branch is a chain of its own that goes
        // on to it: a keyword in a branch has the rest of the block as its continuation, as one outside does, and a
        // `return` in a branch ends the whole block. `name` is what a `def` binds the statement's value to.
        //
        //   def __rest_1 = { name -> <rest of the block> }
        //   if cond then <then branch, then __rest_1(its value)> else <else branch, then __rest_1(its value)>
        and desugarBranching (expr: Expression) (name: Identifier option) (rest: Statement list) (next: Expression option -> Expression) : Expression =
            freshNames <- freshNames + 1
            let restName = $"{NyxCompiler.Infer.workflowRestPrefix}{freshNames}"
            let parameter = name |> Option.defaultValue "_"
            let restFunction = madeFor expr (Lambda([ parameter, None ], desugarChain rest next, false))
            // A branch goes on to the rest with its value when a `def` binds it, and with `()` otherwise, so that
            // branches of different types can still be a statement.
            let goOn (last: Expression option) =
                let argument =
                    match last, name with
                    | Some value, Some _ -> value
                    | _ -> madeFor expr UnitExpr
                let call = madeFor expr (FunctionCall(restName, None, [ argument ]))
                match last, name with
                | Some value, None -> madeFor value (Block [ Statement.from value (ExprStatement value); Statement.from call (ExprStatement call) ])
                | _ -> call
            let branch (body: Expression) =
                let statements =
                    match body.Kind with
                    | Block statements -> statements
                    | _ -> [ Statement.from body (ExprStatement body) ]
                desugarChain (statements |> List.collect liftStatementBinds) goOn
            let branched =
                let kind =
                    match expr.Kind with
                    | Except(value, arms) -> Except.asMatch expr value arms
                    | kind -> kind
                match kind with
                | IfExpr(cond, thenExpr, elseExpr) -> IfExpr(desugarExpr cond, branch thenExpr, branch elseExpr)
                | Match(values, arms) ->
                    let desugarArm (patterns, body) =
                        PatternSpace.ofArm values.Length patterns |> List.map (Pattern.mapExpressions desugarExpr), branch body
                    Match(values |> List.map desugarExpr, arms |> List.map desugarArm)
                | other -> other
            madeFor expr (
                Block [
                    Statement.from expr (DefStatement(false, restName, None, restFunction))
                    Statement.from expr (ExprStatement { expr with Kind = branched })
                ])
        // A statement followed by the rest of the chain, as a block made up for them that spans from the statement
        // to the end of the rest. The rest is a made-up `()` with no span when the statement is the last one.
        and chainBlock (statement: Statement) (nextExpr: Expression) (desugared: Statement) =
            let span =
                if SourceSpan.isNone nextExpr.Span then statement.Span
                else SourceSpan.between statement.Span nextExpr.Span
            recordMade statement.Id (Expression.at span (Block [ desugared; Statement.from nextExpr (ExprStatement nextExpr) ]))
        // A block, from its first workflow keyword on, as a workflow chain (see `desugarWorkflowChain`): a statement
        // that is a keyword, or an `if` or `match` with one in a branch. Where the rest of a builder's block cannot
        // follow (see `unreachable`), its keywords are left to be reported.
        and desugarBlock statements =
            let startsChain (statement: Statement) =
                (unreachable.IsNone || builder.IsNone)
                && (match statement.Kind with
                    | DefStatement(_, _, _, Kind (WorkflowBindExpr _))
                    | ExprStatement (Kind (WorkflowBindExpr _))
                    | ExprStatement (Kind (WorkflowReturnExpr _)) -> true
                    | DefStatement(_, _, _, value)
                    | ExprStatement value -> branchesUseKeywords value
                    | _ -> false)
            let rec split prefix remaining =
                match remaining with
                | [] -> List.rev prefix
                | stmt :: _ when startsChain stmt ->
                    let chainExpr = desugarWorkflowChain remaining
                    List.rev prefix @ [ Statement.from chainExpr (ExprStatement chainExpr) ]
                | stmt :: rest ->
                    split (desugarStatement stmt :: prefix) rest
            split [] (statements |> List.collect liftStatementBinds)

        let desugared =
            module'
            |> List.map (function
                | Kind (Def (ValueDef(isExport, name, typeOpt, expr))) as item ->
                    { item with Kind = Def (ValueDef(isExport, name, typeOpt, desugarExpr expr)) }
                | Kind (Expr expr) as item -> { item with Kind = Expr (desugarExpr expr) }
                | other -> other)
        // Some made-up expressions only lend their kind to another and are not in the desugared module.
        let kept = desugared |> List.collect SyntaxTree.ofItem |> List.collect SyntaxTree.expressionsWithin |> List.map _.Id |> Set.ofList
        let lowered =
            lowered
            |> Map.map (fun _ made -> made |> List.filter kept.Contains |> List.rev)
            |> Map.filter (fun _ made -> not made.IsEmpty)
        desugared, List.ofSeq diagnostics, zeros, lowered

    let desugar (module': Module) : Module =
        let desugared, _, _, _ = desugarWithDiagnostics module'
        desugared
