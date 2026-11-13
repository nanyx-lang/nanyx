namespace NyxCompiler

open Parser.Program

/// Walking the module as written, for the rules of the official linter, the editor's lookups and the backend's lambda
/// lifting.
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
        | BinaryOp(_, _, left, right) -> [ left; right ], []
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
        | ReExport _
        | Attribute _ -> []

    /// The types written in a definition: its annotation, or a type definition's parameters' constraints and body.
    let private definitionTypes (definition: Definition) : TypeExpr list =
        match definition with
        | ValueDef(_, _, typeOpt, _) -> Option.toList typeOpt
        | TypeDef(_, _, parameters, body) -> (parameters |> List.choose snd) @ [ body ]

    /// The types written directly in `expr` (not in the expressions inside it): its lambda's parameters' annotations,
    /// its block's defs' annotations and type definitions, the context it names.
    let expressionTypes (expr: Expression) : TypeExpr list =
        match expr.Kind with
        | Lambda(parameters, _, _) -> parameters |> List.choose snd
        | Block statements ->
            statements
            |> List.collect (fun statement ->
                match statement.Kind with
                | DefStatement(_, _, typeOpt, _) -> Option.toList typeOpt
                | TypeDefStatement(_, _, parameters, body) -> (parameters |> List.choose snd) @ [ body ]
                | UseStatement _
                | ExprStatement _
                | ImportStatement _ -> [])
        | ContextMemberAccess(context, _)
        | ContextMemberCall(context, _, _) -> [ context ]
        | _ -> []

    let rec topLevelTypes (item: TopLevelItem) : TypeExpr list =
        match item.Kind with
        | Def definition -> definitionTypes definition
        | ModuleBlock(_, items) -> items |> List.collect topLevelTypes
        | Expr _
        | ModuleDecl _
        | Import _
        | ReExport _
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
        | BinaryOp(_, _, left, right) -> referencedNames left @ referencedNames right
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

    /// The names `expr` refers to that nothing within it binds, each with the expression that names it, as
    /// `referencedNames` gives them but without those a lambda's parameters, a match arm's patterns or a def of an
    /// enclosing block bind. A block's def binds its name in the statements after it, and in its own value only when it
    /// is `rec`.
    let freeReferences (expr: Expression) : (string * Expression) list =
        let rec free (bound: Set<Identifier>) (expr: Expression) : (string * Expression) list =
            let named name = if bound.Contains name then [] else [ name, expr ]
            let inArms (arms: MatchArm list) =
                arms
                |> List.collect (fun (patterns, body) ->
                    let bound = patterns |> List.collect Pattern.boundNames |> List.fold (fun acc name -> Set.add name acc) bound
                    (patterns |> List.collect Pattern.expressions |> List.collect (free bound)) @ free bound body)
            match expr.Kind with
            | IdentifierExpr name -> named name
            | FunctionCall(name, _, args)
            | ContextMemberCall(_, name, args) -> named name @ (args |> List.collect (free bound))
            | Pipe(value, name, _, args) -> named name @ ((value :: args) |> List.collect (free bound))
            | Lambda(parameters, body, _) -> free (parameters |> List.fold (fun acc (name, _) -> Set.add name acc) bound) body
            | Match(values, arms) -> (values |> List.collect (free bound)) @ inArms arms
            | Except(value, arms) -> free bound value @ inArms arms
            | Block statements ->
                statements
                |> List.fold (fun (bound: Set<Identifier>, names) statement ->
                    match statement.Kind with
                    | DefStatement(_, name, _, value) ->
                        let inValue = match value.Kind with RecSyntax.Call _ -> bound.Add name | _ -> bound
                        bound.Add name, names @ free inValue value
                    | _ -> bound, names @ (ofStatement statement |> List.collect (free bound))) (bound, [])
                |> snd
            | MemberAccess(inner, _, _) ->
                // `a.b.c` may name a definition `a.b.c` of a module block, unless `a` is bound here.
                let rec dotted (current: Expression) =
                    match current.Kind with
                    | IdentifierExpr name -> Some(name, name)
                    | MemberAccess(inner, memberName, _) -> dotted inner |> Option.map (fun (root, prefix) -> root, $"{prefix}.{memberName}")
                    | _ -> None
                match dotted expr with
                | Some(root, path) when not (bound.Contains root) -> (path, expr) :: free bound inner
                | _ -> free bound inner
            | _ -> childrenOf expr |> fst |> List.collect (free bound)
        free Set.empty expr

    /// The names `expr` refers to that nothing within it binds (see `freeReferences`).
    let freeNames (expr: Expression) : string list = freeReferences expr |> List.map fst

    /// The module's imports, a re-export's among them: a re-export imports what it exports again.
    let internal collectImports (module': Module) =
        module'
        |> List.choose (function
            | Kind (Import items) -> Some items
            | Kind (ReExport item) -> Some [ item ]
            | _ -> None)
        |> List.collect id
