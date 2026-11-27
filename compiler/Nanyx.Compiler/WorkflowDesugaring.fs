namespace NyxCompiler

open System
open Parser.Program
open NyxCompiler.ExpressionRewrites

/// Where in a builder's block a workflow keyword is when the rest of the block cannot be its continuation.
type Unreachable =
    /// Inside a lambda, which runs when it is called, not where it is written.
    | InLambda
    /// In the body of a `for` loop that is not a statement of the block, or that walks several iterables. A loop over
    /// one iterable that is a statement of its own is run by the builder's `for` member instead.
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
/// lone `else`, `_` or pattern for all its values, and the holes of interpolated strings.
module WorkflowDesugaring =
    /// A builder's block: a call, to a name the compiler did not make up, whose last argument is a lambda without
    /// parameters, as `proc { ... }` and `retrying(3) { ... }` are. The builder's name, its other arguments, the lambda,
    /// its body, and whether it takes unit.
    let private (|BuilderBlock|_|) (kind: ExpressionKind) =
        match kind with
        | FunctionCall(name, _, args) when not (MadeUpNames.isMadeUp name) ->
            match List.rev args with
            | (Kind (Lambda([], body, takesUnit)) as lambda) :: others -> Some (name, List.rev others, lambda, body, takesUnit)
            | _ -> None
        | _ -> None

    /// `module'` desugared, with the errors desugaring found (a workflow keyword the rest of its builder's block cannot
    /// follow, see `Unreachable`); each made-up read of a member of a builder's context, by its node, with the place to
    /// report it at if the context has no such member and the builder's name: a keyword's call, at the keyword, where a
    /// block falls off its end without `return`, for its `zero`, and a loop whose body uses a keyword, for its `for`;
    /// the expressions of the desugared module made up for each node of `module'`, in the order they were made: a
    /// workflow bind's are the call to its builder's member and the continuation passed to it; and each `#delay`
    /// keyword's call, by its span, with where the keyword is written, `k!`, and the keyword (see `HiddenKeywords`).
    ///
    /// A keyword's kind is the one `@customKeywordType` gives the member of its name in `module'`'s context declarations,
    /// else in `imported`'s (the keywords of the modules it imports, see `Attributes.keywordKinds`), else `#bind`. A
    /// `#delay` keyword `k! e` is `k({ e })` wherever it is, with nothing else rewritten, so it needs no builder; a
    /// `#bindF` keyword is a `#bind` keyword whose value is passed as `{ e }`. The language's `yield` is a `#yield`
    /// keyword: a lambda, a builder's block included, with one in it is the lazy sequence of the values it yields, and
    /// calls no member (see `Seqs.consFunction`).
    let desugarWithDiagnostics (imported: Map<Identifier, KeywordKind>) (module': Module) : Module * Diagnostic list * Map<NodeId, SourceSpan * Identifier> * Map<NodeId, NodeId list> * Map<SourceSpan, SourceSpan * Identifier> =
        let isReturnKeyword keyword = keyword = WorkflowSyntax.returnKeyword
        let kinds = imported |> Attributes.withKeywordKinds (Attributes.keywordKinds module')
        let kindOf keyword =
            match kinds |> Map.tryFind keyword with
            | Some kind -> kind
            | None when keyword = WorkflowSyntax.yieldKeyword -> KeywordKind.Yield
            | None -> KeywordKind.Bind
        let isYield keyword = kindOf keyword = KeywordKind.Yield
        // A workflow bind whose keyword takes the rest of the block: one whose keyword is not `#delay`.
        let (|Bind|_|) (kind: ExpressionKind) =
            match kind with
            | WorkflowBindExpr(keyword, value) when kindOf keyword <> KeywordKind.Delay -> Some (keyword, value)
            | _ -> None
        let diagnostics = ResizeArray<Diagnostic>()
        let mutable memberPlaces: Map<NodeId, SourceSpan * Identifier> = Map.empty
        let mutable delayCalls: Map<SourceSpan, SourceSpan * Identifier> = Map.empty

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

        // The builder whose block is being desugared (`proc` in `proc { ... }`). A keyword `k` in the block calls the
        // member `k` of the context the builder runs its block under, `return` its member `return`.
        let mutable builder: string option = None

        // Whether the builder's block, or the lambda, being desugared is a sequence: one with a `#yield` keyword in it.
        let mutable yielding = false

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
                | InLoopBody ->
                    "in the body of a `for` loop that is part of an expression or walks several iterables",
                    "make the loop a statement of its own over one list, or handle the value with a `match` in the loop's body"
                | InWhileLoop when isYield keyword ->
                    "in a `while` loop", "walk a sequence with a `for` loop instead, or make the sequence with `seq.unfold`"
                | InWhileLoop -> "in a `while` loop", "handle the value with a `match` in the loop"
                | InConditionalPart -> "in a part of an expression that may not run", "make the `if` or `match` it is in a statement of its own"
            diagnostics.Add(
                Diagnostics.at DiagnosticCodes.InvalidWorkflow expr.Span
                    $"`{keyword}` is {where}, so the rest of the `{builderName}` block cannot follow it: {advice}")
        // The matches the parser made of a lambda's arms, as in `{ a, b -> | x, y -> ... }`, and of a loop's: each the
        // body of a lambda written where it is. Only a written `match` takes one pattern for all its values.
        let armsOfLambdas =
            module'
            |> List.collect SyntaxTree.ofItem
            |> List.collect SyntaxTree.expressionsWithin
            |> List.choose (fun expr ->
                match expr.Kind with
                | Lambda(_, (Kind (Match _) as body), _) when body.Span = expr.Span -> Some body.Id
                | _ -> None)
            |> Set.ofList
        // The values and arms of the match `expr` (see `PatternSpace.ofMatch`).
        let ofMatch (expr: Expression) (values: Expression list) (arms: MatchArm list) =
            if armsOfLambdas.Contains expr.Id then
                values, arms |> List.map (fun (patterns, body) -> PatternSpace.ofArm values.Length patterns, body)
            else
                PatternSpace.ofMatch values arms
        let mutable freshNames = 0
        let freshName prefix =
            freshNames <- freshNames + 1
            $"__{prefix}_{freshNames}"

        // `k(args)` for the workflow keyword `k`: a call to the member `k` of the context the builder the block was
        // written after runs it under, which the block's lambda requires, so it is in scope by its name (see
        // `Contexts.extendEnvForLambdaArgument`). Each call instantiates the member's own type variables afresh. The
        // call is reported at `source`, with the builder's name, if no context in scope there has a member `k`. Outside
        // a builder's block it is a call to a function named `k`, and an error if `k` is one of the language's own
        // keywords, `try`, `yield` or `return`, which no function can be named. A sequence has no `return`: it ends
        // where its block does.
        let memberCall (source: Expression) (builderName: string) (keyword: string) (args: Expression list) =
            let call = madeFor source (FunctionCall(keyword, None, args))
            memberPlaces <- memberPlaces |> Map.add call.Id (source.Span, builderName)
            call
        let workflowCall (source: Expression) (keyword: string) (args: Expression list) : Expression =
            match builder with
            | Some builderName when yielding && isReturnKeyword keyword ->
                diagnostics.Add(
                    Diagnostics.at DiagnosticCodes.InvalidWorkflow source.Span
                        $"`{keyword}` can't end the `{builderName}` block, which is a sequence because it uses `yield`: the sequence ends where the block does")
                madeFor source (FunctionCall(keyword, None, args))
            | Some builderName -> memberCall source builderName keyword args
            | None ->
                if List.contains keyword WorkflowSyntax.bareKeywords || isReturnKeyword keyword then
                    let keywordSpan = { source.Span with End = source.Span.Start + keyword.Length }
                    let where = if isYield keyword then "a lambda or a builder block, such as `seq { ... }`" else "a builder block, such as `proc { ... }`"
                    diagnostics.Add(
                        Diagnostics.at DiagnosticCodes.WorkflowKeywordOutsideWorkflow keywordSpan
                            $"`{keyword}` is a workflow keyword, so it can only be used inside {where}")
                madeFor source (FunctionCall(keyword, None, args))

        // Whether `expr` uses a workflow keyword of the block it is in, one for which `isKeyword` holds: one not inside
        // a lambda, or inside a builder block of its own.
        let rec usesKeywordWhere (isKeyword: ExpressionKind -> bool) (expr: Expression) : bool =
            let usesKeyword = usesKeywordWhere isKeyword
            let inValue (statement: Statement) =
                match statement.Kind with
                | DefStatement(_, _, _, value)
                | ExprStatement value
                | UseStatement value -> usesKeyword value
                | ImportStatement _
                | TypeDefStatement _
                | AttributeStatement _ -> false
            match expr.Kind with
            | kind when isKeyword kind -> true
            // A `#delay` keyword's expression is in a lambda.
            | WorkflowBindExpr _
            | WorkflowReturnExpr _
            | Lambda _
            | ContextNames _ -> false
            // A loop's body is a lambda, but a keyword in it is the block's (see `desugarLoop`).
            | ForSyntax.Call(iterables, _, _, body) -> List.exists usesKeyword iterables || usesKeyword body
            | BuilderBlock(_, others, _, _, _) -> List.exists usesKeyword others
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
            | TypeValue _
            | UnitExpr
            | LiteralExpr _
            | IdentifierExpr _ -> false
        let usesKeyword = usesKeywordWhere (function Bind _ | WorkflowReturnExpr _ -> true | _ -> false)
        // Whether `expr`, a lambda's body, makes the lambda a sequence: whether it uses a `#yield` keyword.
        let yields = usesKeywordWhere (function Bind(keyword, _) -> isYield keyword | _ -> false)

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
            | Bind(keyword, value) when not (isReturnKeyword keyword) ->
                let prefix, value' = liftBinds value
                let name = freshName keyword
                prefix @ [ Statement.from expr (DefStatement(false, name, None, { expr with Kind = WorkflowBindExpr(keyword, value') })) ],
                madeFor expr (IdentifierExpr name)
            | Bind(keyword, value) ->
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
            // A `#delay` keyword's expression is in a lambda.
            | WorkflowBindExpr _
            | TagExpr(_, None)
            | Lambda _
            | ContextNames _
            | Block _
            | UseIn _
            | ContextMemberAccess _
            | TypeValue _
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
                | Bind(keyword, bound) when not (isReturnKeyword keyword) ->
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
            | TypeDefStatement _
            | AttributeStatement _ -> [ statement ]

        // The end of a sequence: `seq.empty()`. It carries no contexts, and joins a branch that carries some (see
        // `BranchJoin.joinCarried`).
        let sequenceEnd () = Expression.synthetic (FunctionCall(Seqs.emptyFunction, None, []))
        // Whether `chain` is a sequence's end and nothing else: a `yield` or a `for` loop it follows is the last thing
        // the sequence gives, and needs no rest made for it.
        let isSequenceEnd (chain: Expression) =
            match chain.Kind with
            | FunctionCall(name, None, []) -> name = Seqs.emptyFunction
            | _ -> false

        // A workflow bind or return becomes a call to the workflow's members, whose type is not that of the bind as
        // written (a bind's call is the whole rest of the workflow), so that call is a node of its own, with a new id.
        // Everything else keeps its node's id.
        let rec desugarExpr (expr: Expression) : Expression =
            match expr.Kind with
            | WorkflowBindExpr _
            | WorkflowReturnExpr _ -> madeFor expr (desugarKind expr)
            | _ -> { expr with Kind = desugarKind expr }
        // `desugarIt ()` in the block of the builder `name`, whose keywords call its context's members, or which is a
        // sequence if `isSequence`. A builder block nested in it starts afresh.
        and inBlockOf (name: string) (isSequence: bool) (desugarIt: unit -> Expression) =
            let outerBuilder, outerUnreachable, outerYielding = builder, unreachable, yielding
            builder <- Some name
            unreachable <- None
            yielding <- isSequence
            let desugared = desugarIt ()
            builder <- outerBuilder
            unreachable <- outerUnreachable
            yielding <- outerYielding
            desugared
        // A builder's block, or a lambda's body, desugared. A body that is not a block, as in `proc { return f(try g(x)) }`,
        // is desugared as a block of one statement when it is a sequence or uses a workflow keyword, so that the keyword
        // has the end of the block after it, as it would on a line of its own.
        and desugarBlockBody (body: Expression) =
            match body.Kind with
            | Block _ -> desugarExpr body
            | _ when yielding || usesKeyword body -> madeFor body (Block(desugarBlock [ Statement.from body (ExprStatement body) ]))
            | _ -> desugarExpr body
        // `{ value }`, desugared: how a `#delay` or `#bindF` keyword passes the expression after it.
        and desugarThunk (value: Expression) = desugarExpr (madeFor value (Lambda([], value, false)))
        // The value a bind of `keyword` passes its member along with the continuation.
        and boundArgument (keyword: Identifier) (value: Expression) =
            if kindOf keyword = KeywordKind.BindF then desugarThunk value else desugarExpr value
        and desugarKind expr =
            match expr.Kind with
            // A block written after a builder's name, `proc { ... }` or `retrying(3) { ... }`: the call it is, with
            // the block's keywords calling the members of the context the builder runs it under (see `workflowCall`).
            // The builder's other arguments are outside the block.
            | BuilderBlock(name, others, lambda, body, takesUnit) ->
                let rangeOpt = match expr.Kind with FunctionCall(_, rangeOpt, _) -> rangeOpt | _ -> None
                let others' = others |> List.map desugarExpr
                let body' = inBlockOf name (yields body) (fun () -> desugarBlockBody body)
                FunctionCall(name, rangeOpt, others' @ [ { lambda with Kind = Lambda([], body', takesUnit) } ])
            // A lambda with a `#yield` keyword in it is a sequence, as a builder's block with one is.
            | Lambda(args, body, takesUnit) when yields body ->
                Lambda(args, inBlockOf Seqs.alias true (fun () -> desugarBlockBody body), takesUnit)
            // A `for` loop's body is a lambda of the elements, which the loop runs once per step.
            | ForSyntax.Call(iterables, loop, parameters, body) ->
                let body' = within InLoopBody (fun () -> desugarExpr body)
                FunctionCall(ForSyntax.builtIn, None, List.map desugarExpr iterables @ [ { loop with Kind = Lambda(parameters, body', false) } ])
            | IsSyntax.BindingWhile loop -> desugarKind { expr with Kind = loop }
            | WhileSyntax.Call(condition, body) ->
                FunctionCall(WhileSyntax.builtIn, None, within InWhileLoop (fun () -> [ desugarExpr condition; desugarExpr body ]))
            | FunctionCall(name, rangeOpt, args) ->
                FunctionCall(name, rangeOpt, args |> List.map desugarExpr)
            | Lambda(args, body, takesUnit) ->
                Lambda(args, within InLambda (fun () -> desugarExpr body), takesUnit)
            | ContextNames(names, body) -> ContextNames(names, desugarExpr body)
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
            // A lone `else` or `_` in a match on several values becomes one per value, and a match with an arm of one
            // pattern for all of them is one on their tuple (`PatternSpace.ofMatch`).
            | Match(exprs, arms) ->
                let exprs, arms = ofMatch expr exprs arms
                let mappedExprs = exprs |> List.map desugarExpr
                let mappedArms =
                    arms
                    |> List.map (fun (patterns, expr) ->
                        patterns |> List.map (Pattern.mapExpressions desugarExpr),
                        within InConditionalPart (fun () -> desugarExpr expr))
                Match(mappedExprs, mappedArms)
            | Except(value, arms) -> desugarKind { expr with Kind = Except.asMatch expr value arms }
            | IsSyntax.BindingIf asMatch -> desugarKind { expr with Kind = asMatch }
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
            | TypeValue written -> TypeValue written
            | UseIn(value, body) -> UseIn(desugarExpr value, desugarExpr body)
            // A `#delay` keyword's call, which takes no rest of the block, so it can be anywhere.
            | WorkflowBindExpr(keyword, value) when kindOf keyword = KeywordKind.Delay ->
                delayCalls <- delayCalls |> Map.add expr.Span ({ expr.Span with End = expr.Span.Start + keyword.Length + 1 }, keyword)
                FunctionCall(keyword, None, [ desugarThunk value ])
            // A keyword the rest of its builder's block cannot follow is an error; the value stands in for it, as
            // nothing is checked once desugaring fails.
            | WorkflowBindExpr(keyword, value) ->
                match builder, unreachable with
                | Some builderName, Some place ->
                    reportUnreachable expr keyword builderName place
                    (desugarExpr value).Kind
                | _ -> (workflowCall expr keyword [boundArgument keyword value]).Kind
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
            | TypeDefStatement _
            | AttributeStatement _ -> stmt
        // The statements of a block from its first workflow keyword on, as that keyword's call with the rest of the
        // block as its continuation, and so on. A block that falls off its end without `return` has the builder's
        // `zero` as its value there (`#some()` for `proc`), its last value only run; outside a builder's block, `()`. A
        // sequence's ends there: `seq.empty()`.
        and desugarWorkflowChain (statements: Statement list) =
            let ending () =
                match builder with
                | Some _ when yielding -> sequenceEnd ()
                | Some builderName ->
                    let zero = Expression.synthetic (IdentifierExpr WorkflowSyntax.zeroMember)
                    let place = statements |> List.tryLast |> Option.map _.Span |> Option.defaultValue SourceSpan.none
                    memberPlaces <- memberPlaces |> Map.add zero.Id (place, builderName)
                    zero
                | None -> Expression.synthetic UnitExpr
            // The chain may end in several places (see `desugarBranching`), each with an ending of its own.
            desugarChain statements (endWith ending)
        // The end of a chain, given the value of its last statement if that is an expression: that value, only run,
        // then `ending ()`.
        and endWith (ending: unit -> Expression) (last: Expression option) =
            let ending = ending ()
            match last with
            | Some value -> madeFor value (Block [ Statement.from value (ExprStatement value); Statement.synthetic (ExprStatement ending) ])
            | None -> ending
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
                    | Bind(keyword, bindExpr) when isYield keyword ->
                        diagnostics.Add(
                            Diagnostics.at DiagnosticCodes.InvalidWorkflow expr.Span
                                $"`{keyword}` gives the sequence a value and has none of its own, so `{name}` can't be bound to it: write `{keyword}` as a statement of its own")
                        yieldCall expr bindExpr rest next
                    | Bind(keyword, bindExpr) when isReturnKeyword keyword ->
                        workflowCall expr WorkflowSyntax.returnKeyword [desugarExpr bindExpr]
                    | Bind(keyword, bindExpr) ->
                        workflowCall expr keyword [boundArgument keyword bindExpr; madeFor expr (Lambda([name, None], restOfChain (), false))]
                    | _ when branchesUseKeywords expr -> desugarBranching expr (Some name) rest next
                    | _ ->
                        chainBlock statement (restOfChain ()) { statement with Kind = DefStatement(isExport, name, typeOpt, desugarExpr expr) }
                | ExprStatement expr ->
                    match expr.Kind with
                    | WorkflowReturnExpr value -> workflowCall expr WorkflowSyntax.returnKeyword [desugarExpr value]
                    | Bind(keyword, bindExpr) when isYield keyword -> yieldCall expr bindExpr rest next
                    | Bind(keyword, bindExpr) when isReturnKeyword keyword ->
                        workflowCall expr WorkflowSyntax.returnKeyword [desugarExpr bindExpr]
                    // The last statement's bound value is the chain's value.
                    | Bind(keyword, bindExpr) when rest.IsEmpty ->
                        let value = freshName "value"
                        let continuation = next (Some (madeFor expr (IdentifierExpr value)))
                        workflowCall expr keyword [boundArgument keyword bindExpr; madeFor expr (Lambda([value, None], continuation, false))]
                    | Bind(keyword, bindExpr) ->
                        workflowCall expr keyword [boundArgument keyword bindExpr; madeFor expr (Lambda([("_", None)], restOfChain (), false))]
                    | _ when branchesUseKeywords expr -> desugarBranching expr None rest next
                    | ForSyntax.Call([ iterable ], loop, parameters, body) when yielding && usesKeyword body ->
                        desugarSequenceLoop expr iterable loop parameters body rest next
                    | ForSyntax.Call([ iterable ], loop, parameters, body) when builder.IsSome && usesKeyword body ->
                        desugarLoop expr iterable loop parameters body rest next
                    | _ when rest.IsEmpty -> next (Some (desugarExpr expr))
                    | _ ->
                        chainBlock statement (restOfChain ()) { statement with Kind = ExprStatement (desugarExpr expr) }
                | UseStatement _
                | ImportStatement _
                | TypeDefStatement _
                | AttributeStatement _ ->
                    chainBlock statement (restOfChain ()) (desugarStatement statement)
        // An `if` or a `match` statement with a workflow keyword in a branch, `expr`, followed by `rest`. The rest of
        // the block, up to `next`, is bound once to a local function, and each branch is a chain of its own that goes
        // on to it: a keyword in a branch has the rest of the block as its continuation, as one outside does, and a
        // `return` in a branch ends the whole block. `name` is what a `def` binds the statement's value to.
        //
        //   def __rest_1 = { name -> <rest of the block> }
        //   if cond then <then branch, then __rest_1(its value)> else <else branch, then __rest_1(its value)>
        //
        // The last statement of a chain has no rest: each branch goes on to `next` with its value, as the statement
        // would if it had no keywords. So a `match` that ends a branch of another gives that branch its value.
        and desugarBranching (expr: Expression) (name: Identifier option) (rest: Statement list) (next: Expression option -> Expression) : Expression =
            freshNames <- freshNames + 1
            let restName = $"{WorkflowRests.prefix}{freshNames}"
            let parameter = name |> Option.defaultValue "_"
            let isLast = rest.IsEmpty && name.IsNone
            let restFunction = if isLast then None else Some (madeFor expr (Lambda([ parameter, None ], desugarChain rest next, false)))
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
                desugarChain (statements |> List.collect liftStatementBinds) (if isLast then next else goOn)
            let branched =
                let kind =
                    match expr.Kind with
                    | Except(value, arms) -> Except.asMatch expr value arms
                    | IsSyntax.BindingIf asMatch -> asMatch
                    | kind -> kind
                match kind with
                | IfExpr(cond, thenExpr, elseExpr) -> IfExpr(desugarExpr cond, branch thenExpr, branch elseExpr)
                | Match(values, arms) ->
                    let values, arms = ofMatch expr values arms
                    let desugarArm (patterns, body) = patterns |> List.map (Pattern.mapExpressions desugarExpr), branch body
                    Match(values |> List.map desugarExpr, arms |> List.map desugarArm)
                | other -> other
            match restFunction with
            | None -> { expr with Kind = branched }
            | Some restFunction ->
                madeFor expr (
                    Block [
                        Statement.from expr (DefStatement(false, restName, None, restFunction))
                        Statement.from expr (ExprStatement { expr with Kind = branched })
                    ])
        // A `for` loop statement over `iterable` whose body uses a workflow keyword, `expr`, followed by `rest`: a call
        // to the builder's `for` member with the iterable, the body and the rest of the block up to `next`. The body
        // takes the loop's element and gives back `#__nextItem` where its chain ends, for the member to go on to the next
        // item; a `try` that fails or a `return` in it gives back what ends the whole block, as one outside the loop
        // does. So the member walks the items in a loop of its own, and a body's call returns before the next (#2683).
        //
        //   for(items, { item -> <body, then #__nextItem> }, { <rest of the block> })
        and desugarLoop (expr: Expression) iterable (loop: Expression) parameters (body: Expression) (rest: Statement list) next =
            let goOn (last: Expression option) =
                let nextItem = madeFor expr (TagExpr(WorkflowSyntax.nextItemTag, None))
                match last with
                | Some value -> madeFor value (Block [ Statement.from value (ExprStatement value); Statement.from nextItem (ExprStatement nextItem) ])
                | None -> nextItem
            let statements =
                match body.Kind with
                | Block statements -> statements
                | _ -> [ Statement.from body (ExprStatement body) ]
            let bodyChain = desugarChain (statements |> List.collect liftStatementBinds) goOn
            let bodyFunction = { loop with Kind = Lambda(parameters, bodyChain, false) }
            let restFunction = madeFor expr (Lambda([], desugarChain rest next, false))
            memberCall expr (Option.get builder) WorkflowSyntax.forMember [ desugarExpr iterable; bodyFunction; restFunction ]
        // A `yield` of `value` in a sequence, `expr`, followed by `rest`: the sequence of `value` and then what the rest
        // of the block up to `next` gives, which runs only when the sequence is pulled that far. A `yield` the
        // sequence ends after has no rest to make.
        //
        //   seq.cons(value, { <rest of the block> })
        //   __seqLast(value)
        and yieldCall (expr: Expression) (value: Expression) (rest: Statement list) next =
            match desugarChain rest next with
            | restOfBlock when isSequenceEnd restOfBlock -> madeFor expr (FunctionCall(Seqs.lastFunction, None, [ desugarExpr value ]))
            | restOfBlock ->
                let tail = madeFor expr (Lambda([], restOfBlock, false))
                madeFor expr (FunctionCall(Seqs.consFunction, None, [ desugarExpr value; tail ]))
        // A `for` loop statement over `iterable` whose body yields, `expr`, in a sequence, followed by `rest`: the
        // sequences the body gives for each element, one after another, then what the rest of the block up to `next`
        // gives. Each is worked out when the sequence is pulled that far.
        //
        //   seq.append(seq.flatMap(__toSeq(items), { item -> <body, then seq.empty()> }), seq.delay({ <rest of the block> }))
        //
        // A loop the sequence ends after is the `seq.flatMap` alone. A body that is one `yield e` and nothing else is
        // `seq.map(__toSeq(items), { item -> e })`, which passes each element on through one sequence rather than
        // through the body's own and `seq.flatMap`'s (#2208).
        //
        // The items are anything `Iterable`, as a plain `for` loop's are: a sequence is walked as it is, and anything
        // else is read through its `Iterable` (see `Loops.walksAsSequence`).
        and desugarSequenceLoop (expr: Expression) iterable (loop: Expression) parameters (body: Expression) (rest: Statement list) next =
            let statements =
                match body.Kind with
                | Block statements -> statements
                | _ -> [ Statement.from body (ExprStatement body) ]
            let items = madeFor iterable (FunctionCall(ForSyntax.toSeq, None, [ desugarExpr iterable ]))
            let each =
                match statements with
                // The call is made at the body's first `yield`, where an error about the sequence is reported.
                | [ { Kind = ExprStatement single } ] when (yieldsOnce single).IsSome ->
                    let firstYield = SyntaxTree.expressionsWithin single |> List.tryFind (fun inner -> match inner.Kind with Bind(keyword, _) -> isYield keyword | _ -> false)
                    madeFor (firstYield |> Option.defaultValue expr) (FunctionCall(Seqs.mapFunction, None, [ items; { loop with Kind = Lambda(parameters, (yieldsOnce single).Value, false) } ]))
                | _ ->
                    let bodyChain = desugarChain (statements |> List.collect liftStatementBinds) (endWith sequenceEnd)
                    madeFor expr (FunctionCall(Seqs.flatMapFunction, None, [ items; { loop with Kind = Lambda(parameters, bodyChain, false) } ]))
            match desugarChain rest next with
            | restOfBlock when isSequenceEnd restOfBlock -> each
            | restOfBlock ->
                let delayed = madeFor expr (FunctionCall(Seqs.delayFunction, None, [ madeFor expr (Lambda([], restOfBlock, false)) ]))
                madeFor expr (FunctionCall(Seqs.appendFunction, None, [ each; delayed ]))
        // `body`, a loop's, with each `yield e` in it replaced by `e`, where it gives exactly one value each time round:
        // one `yield`, or an `if` or a `match` each of whose branches is one, the arms a `for` loop's parameters are
        // matched in among them. None where it can give none, or more than one, or uses another keyword.
        and yieldsOnce (body: Expression) : Expression option =
            match body.Kind with
            | Bind(keyword, value) when isYield keyword && not (usesKeyword value) -> Some (desugarExpr value)
            | Block [ { Kind = ExprStatement single } ] -> yieldsOnce single
            // An `if` with no `else` gives none when its condition is false: its `else` is a made-up `()`.
            | IfExpr(cond, thenExpr, elseExpr) when not (usesKeyword cond) ->
                match yieldsOnce thenExpr, yieldsOnce elseExpr with
                | Some thenValue, Some elseValue -> Some { body with Kind = IfExpr(desugarExpr cond, thenValue, elseValue) }
                | _ -> None
            | Match(values, arms) when not (values |> List.exists usesKeyword) ->
                let values, arms = ofMatch body values arms
                let bodies = arms |> List.map (snd >> yieldsOnce)
                if bodies |> List.forall Option.isSome then
                    let arms = List.map2 (fun (patterns, _) value -> patterns |> List.map (Pattern.mapExpressions desugarExpr), Option.get value) arms bodies
                    Some { body with Kind = Match(values |> List.map desugarExpr, arms) }
                else None
            | _ -> None
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
                    | DefStatement(_, _, _, Kind (Bind _))
                    | ExprStatement (Kind (Bind _))
                    | ExprStatement (Kind (WorkflowReturnExpr _)) -> true
                    | ExprStatement (Kind (ForSyntax.Call([ _ ], _, _, body))) -> builder.IsSome && usesKeyword body
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
        desugared, List.ofSeq diagnostics, memberPlaces, lowered, delayCalls

    /// `module'` desugared, with no keywords imported (see `desugarWithDiagnostics`).
    let desugar (module': Module) : Module =
        let desugared, _, _, _, _ = desugarWithDiagnostics Map.empty module'
        desugared
