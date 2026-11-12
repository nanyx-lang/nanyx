namespace NyxCompiler

open Parser.Program

/// Walking the module as written, for the rules of the official linter and the backend's lambda lifting.
module SyntaxTree =
    /// The expressions directly within `expr`, and the statements of it if it is a block: a guard's predicate among
    /// them. What types hold is left out: they hold no blocks.
    let childrenOf (expr: Expression) : Expression list * Statement list =
        match expr.Kind with
        | LiteralExpr _
        | UnitExpr
        | IdentifierExpr _
        | ContextMemberAccess _ -> [], []
        | FunctionCall(_, _, args)
        | ContextMemberCall(_, _, args)
        | TupleExpr args
        | ListExpr args -> args, []
        | Lambda(_, body) -> [ body ], []
        | BinaryOp(_, left, right) -> [ left; right ], []
        | Pipe(value, _, _, args) -> value :: args, []
        | Block statements -> [], statements
        | Match(values, arms) -> values @ Pattern.armExpressions arms @ (arms |> List.map snd), []
        | Except(value, arms) -> value :: Pattern.armExpressions arms @ (arms |> List.map snd), []
        | RecordExpr fields -> fields |> List.map (function NamedField(_, value) | PositionalField value -> value), []
        | TagExpr(_, payload) -> Option.toList payload, []
        | IfExpr(condition, ifTrue, ifFalse) -> [ condition; ifTrue; ifFalse ], []
        | MemberAccess(target, _, _) -> [ target ], []
        | UseIn(value, body) -> [ value; body ], []
        | WorkflowBindExpr(_, value)
        | WorkflowReturnExpr value -> [ value ], []
        | InterpolatedString parts -> parts |> List.choose (function StringExpr value -> Some value | StringText _ -> None), []

    /// The expressions a statement holds.
    let ofStatement (statement: Statement) : Expression list =
        match statement.Kind with
        | DefStatement(_, _, _, value)
        | ExprStatement value
        | UseStatement value -> [ value ]
        | TypeDefStatement _
        | ImportStatement _ -> []

    /// `expr` and every expression within it.
    let rec expressionsWithin (expr: Expression) : Expression list =
        let children, statements = childrenOf expr
        expr :: ((children @ (statements |> List.collect ofStatement)) |> List.collect expressionsWithin)

    /// The expressions a top-level item holds, and the items of a module block.
    let rec ofItem (item: TopLevelItem) : Expression list =
        match item.Kind with
        | Def (ValueDef(_, _, _, value))
        | Expr value -> [ value ]
        | ModuleBlock(_, items) -> items |> List.collect ofItem
        | Def (TypeDef _)
        | ModuleDecl _
        | Import _
        | Attribute _ -> []

    /// The names a module writes as values of their own rather than calls: `add` in `fold(0, add)`, not in
    /// `add(1, 2)` or `1 \add(2)`.
    let namedAsValues (items: Module) : Set<Identifier> =
        items
        |> List.collect ofItem
        |> List.collect expressionsWithin
        |> List.choose (fun expr ->
            match expr.Kind with
            | IdentifierExpr name -> Some name
            | _ -> None)
        |> Set.ofList

    /// The blocks of `expr` and of everything within it, each as its statements.
    let rec blocksWithin (expr: Expression) : Statement list list =
        let children, statements = childrenOf expr
        (if statements.IsEmpty then [] else [ statements ])
        @ (children |> List.collect blocksWithin)
        @ (statements |> List.collect ofStatement |> List.collect blocksWithin)
