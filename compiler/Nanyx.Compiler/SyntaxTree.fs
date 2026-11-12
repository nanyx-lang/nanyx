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
        | Lambda(_, body, _) -> [ body ], []
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

    /// Every name an expression may refer to: identifiers, callees and pipe targets, and for a member access
    /// `a.b.c` both the dotted name and the member as `.c`, since the backend may call a definition named either
    /// way. Shadowing is ignored, so this can only over-count.
    let rec referencedNames (expr: Expression) : string list =
        let rec dottedName (current: Expression) =
            match current.Kind with
            | IdentifierExpr name -> Some name
            | MemberAccess(inner, memberName, _) -> dottedName inner |> Option.map (fun prefix -> $"{prefix}.{memberName}")
            | _ -> None
        match expr.Kind with
        | IdentifierExpr name -> [ name ]
        | FunctionCall(name, _, args) -> name :: (args |> List.collect referencedNames)
        | Pipe(value, name, _, args) -> name :: referencedNames value @ (args |> List.collect referencedNames)
        | Lambda(_, body, _) -> referencedNames body
        | BinaryOp(_, left, right) -> referencedNames left @ referencedNames right
        | IfExpr(cond, thenExpr, elseExpr) -> referencedNames cond @ referencedNames thenExpr @ referencedNames elseExpr
        | Block statements -> statements |> List.collect referencedNamesInStatement
        | Match(values, arms) ->
            (values |> List.collect referencedNames)
            @ (arms |> List.collect (fun (patterns, body) -> (patterns |> List.collect referencedNamesInPattern) @ referencedNames body))
        | Except(value, arms) -> referencedNames { expr with Kind = Match([ value ], arms) }
        | TupleExpr items
        | ListExpr items -> items |> List.collect referencedNames
        | RecordExpr fields ->
            fields |> List.collect (function NamedField(_, value) | PositionalField value -> referencedNames value)
        | TagExpr(_, payload) -> payload |> Option.map referencedNames |> Option.defaultValue []
        | MemberAccess(inner, memberName, _) ->
            $".{memberName}" :: (dottedName expr |> Option.toList) @ referencedNames inner
        | UseIn(value, body) -> referencedNames value @ referencedNames body
        | WorkflowBindExpr(_, value)
        | WorkflowReturnExpr value -> referencedNames value
        | InterpolatedString parts ->
            parts |> List.collect (function StringExpr value -> referencedNames value | StringText _ -> [])
        | ContextMemberCall(_, memberName, args) -> memberName :: (args |> List.collect referencedNames)
        | ContextMemberAccess _
        | UnitExpr
        | LiteralExpr _ -> []

    and referencedNamesInStatement (statement: Statement) : string list =
        match statement.Kind with
        | DefStatement(_, _, _, value) -> referencedNames value
        | ExprStatement value -> referencedNames value
        | UseStatement value -> referencedNames value
        | ImportStatement _
        | TypeDefStatement _ -> []

    and referencedNamesInPattern (pattern: Pattern) : string list =
        match pattern.Kind with
        | GuardPattern predicate -> referencedNames predicate
        | RangePattern(startValue, endValue) -> referencedNames startValue @ referencedNames endValue
        | TagPattern(_, Some payload) -> referencedNamesInPattern payload
        | TuplePattern patterns
        | AndPattern patterns
        | ListPattern(patterns, _) -> patterns |> List.collect referencedNamesInPattern
        | ListSplatMiddle(before, after) -> (before @ after) |> List.collect referencedNamesInPattern
        | RecordPattern(_, patterns) -> patterns |> List.collect referencedNamesInPattern
        | RecordMemberPattern(members, _) -> members |> List.collect (snd >> referencedNamesInPattern)
        | TagPattern(_, None)
        | LiteralPattern _
        | IdentifierPattern _
        | WildcardPattern
        | UnitPattern
        | ElsePattern -> []
