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
        | ContextMemberAccess _
        | TypeValue _ -> [], []
        | FunctionCall(_, _, args)
        | ContextMemberCall(_, _, args)
        | TupleExpr args
        | ListExpr args -> args, []
        | Lambda(_, body, _)
        | ContextNames(_, body) -> [ body ], []
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
        | ImportStatement _
        | AttributeStatement _ -> []

    /// The arguments of a call, with a call of one tuple, `f((a, b))` as the backend sees `f(a, b)`, taken as its items.
    let flattenCallArgs (args: Expression list) =
        match args with
        | [ Kind (TupleExpr items) ] -> items
        | _ -> args

    /// `expr` with `f` applied to each expression directly within it: those `childrenOf` gives, the values of its
    /// statements among them. An expression with none is returned as it is.
    let mapChildren (f: Expression -> Expression) (expr: Expression) : Expression =
        let inStatement (statement: Statement) =
            match statement.Kind with
            | DefStatement(isExport, name, typeOpt, value) -> { statement with Kind = DefStatement(isExport, name, typeOpt, f value) }
            | ExprStatement value -> { statement with Kind = ExprStatement(f value) }
            | UseStatement value -> { statement with Kind = UseStatement(f value) }
            | TypeDefStatement _
            | ImportStatement _
            | AttributeStatement _ -> statement
        let inArms (arms: MatchArm list) = arms |> List.map (fun (patterns, body) -> patterns |> List.map (Pattern.mapExpressions f), f body)
        let rebuild kind = { expr with Kind = kind }
        match expr.Kind with
        | LiteralExpr _
        | UnitExpr
        | IdentifierExpr _
        | ContextMemberAccess _
        | TypeValue _ -> expr
        | FunctionCall(name, range, args) -> rebuild (FunctionCall(name, range, List.map f args))
        | ContextMemberCall(context, name, args) -> rebuild (ContextMemberCall(context, name, List.map f args))
        | TupleExpr items -> rebuild (TupleExpr(List.map f items))
        | ListExpr items -> rebuild (ListExpr(List.map f items))
        | Lambda(parameters, body, takesUnit) -> rebuild (Lambda(parameters, f body, takesUnit))
        | ContextNames(names, body) -> rebuild (ContextNames(names, f body))
        | BinaryOp(op, opSpan, left, right) -> rebuild (BinaryOp(op, opSpan, f left, f right))
        | Pipe(value, name, range, args) -> rebuild (Pipe(f value, name, range, List.map f args))
        | Block statements -> rebuild (Block(List.map inStatement statements))
        | Match(values, arms) -> rebuild (Match(List.map f values, inArms arms))
        | Except(value, arms) -> rebuild (Except(f value, inArms arms))
        | RecordExpr fields ->
            fields
            |> List.map (function
                | NamedField(name, value) -> NamedField(name, f value)
                | PositionalField value -> PositionalField(f value))
            |> RecordExpr
            |> rebuild
        | TagExpr(name, payload) -> rebuild (TagExpr(name, Option.map f payload))
        | IfExpr(condition, ifTrue, ifFalse) -> rebuild (IfExpr(f condition, f ifTrue, f ifFalse))
        | MemberAccess(target, field, range) -> rebuild (MemberAccess(f target, field, range))
        | UseIn(value, body) -> rebuild (UseIn(f value, f body))
        | WorkflowBindExpr(keyword, value) -> rebuild (WorkflowBindExpr(keyword, f value))
        | WorkflowReturnExpr value -> rebuild (WorkflowReturnExpr(f value))
        | InterpolatedString parts ->
            parts
            |> List.map (function
                | StringExpr value -> StringExpr(f value)
                | text -> text)
            |> InterpolatedString
            |> rebuild

    /// `expr` and every expression within it as `children` gives those directly within one, each before those within
    /// it, and those within one before the next of its siblings. It takes time in proportion to their number, however
    /// deeply they nest, and no stack (#1460).
    let expressionsWithinBy (children: Expression -> Expression list) (expr: Expression) : Expression list =
        let found = ResizeArray<Expression>()
        let pending = System.Collections.Generic.Stack<Expression>()
        pending.Push expr
        while pending.Count > 0 do
            let next = pending.Pop()
            found.Add next
            for child in List.rev (children next) do
                pending.Push child
        List.ofSeq found

    /// `expr` and every expression within it, each before those within it (see `expressionsWithinBy`).
    let expressionsWithin (expr: Expression) : Expression list =
        expr
        |> expressionsWithinBy (fun within ->
            let children, statements = childrenOf within
            children @ (statements |> List.collect ofStatement))

    /// The expressions a top-level item holds, and the items of a module block.
    /// The arguments `args` of a call as it was written: `f(a, b)` passes its arguments as one tuple, which starts
    /// where its first item does, but `f((a, b))` passes a tuple, whose span takes in its brackets.
    let writtenArguments (args: Expression list) =
        match args with
        | [ Kind (TupleExpr (first :: _ as items)) as tuple ] when first.Span.Start = tuple.Span.Start -> items
        | _ -> args

    let rec ofItem (item: TopLevelItem) : Expression list =
        match item.Kind with
        | Def (ValueDef(_, _, _, value))
        | Expr value
        | UnitDecl(_, _, _, Some value) -> [ value ]
        | ModuleBlock(_, items) -> items |> List.collect ofItem
        | Def (TypeDef _)
        | ModuleDecl _
        | Import _
        | ReExport _
        | Attribute _
        | DimensionDecl _
        | UnitDecl(_, _, _, None) -> []

    /// What a type written in a module is written as.
    type WrittenAs =
        /// The annotation of a def, top-level or local, named `name`, which is written at `definition`.
        | DefAnnotation of name: Identifier * definition: SourceSpan
        /// The body of the type definition, top-level or local, named `name`.
        | TypeBody of name: Identifier
        /// The constraint on a type definition's parameter.
        | ParameterConstraint
        /// A lambda parameter's annotation.
        | LambdaParameter
        /// A context an expression names: in a list of contexts it brings into scope, or the one whose member it reads.
        | ContextNamed
        /// The type `type Point` passes as a type argument.
        | PassedType

    /// A type written in a module, as `writtenTypes` finds it: what it is written as, and the top-level item it is
    /// written in, an item of a module block for one in a block.
    type WrittenType =
        { Type: TypeExpr
          As: WrittenAs
          Item: TopLevelItem }

    /// `t` as a message writes it, for a type variable's name or a dimension's storage: a name, the arguments it is
    /// applied to, and an associated type, as in `Num(a).fraction`.
    let rec typeText (t: TypeExpr) : string =
        match t.Kind with
        | TypeName name
        | TypeVar name -> name
        | TypeApply(name, args) -> $"""{name}({args |> List.map typeText |> String.concat ", "})"""
        | TypeNamedArgument(name, value) -> $"{name} = {typeText value}"
        | TypeAssociated(context, name) -> $"{typeText context}.{name}"
        | TypeHole -> "_"
        | TypeUnit -> "()"
        | _ -> "…"

    /// The types a type definition named `name` writes: its parameters' constraints, then its body.
    let private typeDefinitionTypes (name: Identifier) (parameters: (string * TypeExpr option) list) (body: TypeExpr) =
        (parameters |> List.choose snd |> List.map (fun constraint' -> constraint', ParameterConstraint)) @ [ body, TypeBody name ]

    /// The types written in `item`'s definition, at `span`: its annotation, or a type definition's parameters'
    /// constraints and body.
    let private definitionWrittenTypes (span: SourceSpan) (definition: Definition) =
        match definition with
        | ValueDef(_, name, typeOpt, _) -> typeOpt |> Option.map (fun annotation -> annotation, DefAnnotation(name, span)) |> Option.toList
        | TypeDef(name, _, parameters, body) -> typeDefinitionTypes name parameters body

    /// The types written directly in `expr`, as `expressionTypes` gives them, with what each is written as.
    let private expressionWrittenTypes (expr: Expression) : (TypeExpr * WrittenAs) list =
        match expr.Kind with
        | Lambda(parameters, _, _) -> parameters |> List.choose snd |> List.map (fun annotation -> annotation, LambdaParameter)
        | ContextNames(names, _) -> names |> List.choose (fun (_, context, _) -> context) |> List.map (fun context -> context, ContextNamed)
        | Block statements ->
            statements
            |> List.collect (fun statement ->
                match statement.Kind with
                | DefStatement(_, name, typeOpt, _) -> typeOpt |> Option.map (fun annotation -> annotation, DefAnnotation(name, statement.Span)) |> Option.toList
                | TypeDefStatement(name, _, parameters, body) -> typeDefinitionTypes name parameters body
                | UseStatement _
                | ExprStatement _
                | ImportStatement _
                | AttributeStatement _ -> [])
        | ContextMemberAccess(context, _)
        | ContextMemberCall(context, _, _) -> [ context, ContextNamed ]
        | TypeValue written -> [ written, PassedType ]
        | _ -> []

    /// The types written directly in `expr` (not in the expressions inside it): its lambda's parameters' annotations,
    /// its block's defs' annotations and type definitions, the context it names.
    let expressionTypes (expr: Expression) : TypeExpr list = expressionWrittenTypes expr |> List.map fst

    let rec topLevelTypes (item: TopLevelItem) : TypeExpr list =
        match item.Kind with
        | Def definition -> definitionWrittenTypes item.Span definition |> List.map fst
        | ModuleBlock(_, items) -> items |> List.collect topLevelTypes
        // A dimension's storage and what a derived one names, and a unit's dimension.
        | DimensionDecl(_, _, storage, derived) -> Option.toList storage @ Option.toList derived
        | UnitDecl(_, _, dimension, _) -> [ dimension ]
        | Expr _
        | ModuleDecl _
        | Import _
        | ReExport _
        | Attribute _ -> []

    /// Every type written in `module'`, item by item, in module blocks too, each with what it is written as: an item's
    /// own, then those written in the expressions of its value, in the order `expressionsWithin` gives them.
    let rec writtenTypes (module': Module) : WrittenType list =
        module'
        |> List.collect (fun item ->
            match item.Kind with
            | ModuleBlock(_, items) -> writtenTypes items
            | _ ->
                let own =
                    match item.Kind with
                    | Def definition -> definitionWrittenTypes item.Span definition
                    | _ -> []
                let inValue = ofItem item |> List.collect expressionsWithin |> List.collect expressionWrittenTypes
                (own @ inValue) |> List.map (fun (written, writtenAs) -> { Type = written; As = writtenAs; Item = item }))

    /// The names of the values `module'` exports: each `export def`.
    let exportedValueNames (module': Module) : Set<Identifier> =
        module'
        |> List.choose (function
            | Kind (Def (ValueDef(true, name, _, _))) -> Some name
            | _ -> None)
        |> Set.ofList

    /// The names of the types and contexts `module'` exports: each `export type` and `export context`.
    let exportedTypeNames (module': Module) : Set<Identifier> =
        module'
        |> List.choose (function
            | Kind (Def (TypeDef(name, modifiers, _, _))) when List.contains Export modifiers -> Some name
            | _ -> None)
        |> Set.ofList

    /// The type definitions of `module'`: its top-level ones, then those declared in the blocks of its definitions.
    let typeDefinitions (module': Module) : Definition list =
        let topLevel =
            module'
            |> List.choose (function
                | Kind (Def (TypeDef _ as definition)) -> Some definition
                | _ -> None)
        let local =
            module'
            |> List.collect ofItem
            |> List.collect expressionsWithin
            |> List.collect (fun expr ->
                match expr.Kind with
                | Block statements ->
                    statements
                    |> List.choose (fun statement ->
                        match statement.Kind with
                        | TypeDefStatement(name, modifiers, parameters, body) -> Some (TypeDef(name, modifiers, parameters, body))
                        | _ -> None)
                | _ -> [])
        topLevel @ local

    /// The value of the block of `statements`: the expression its last statement is, if it is one.
    let blockValue (statements: Statement list) : Expression option =
        match List.tryLast statements with
        | Some { Kind = ExprStatement value } -> Some value
        | _ -> None

    /// The blocks of `expr` and of everything within it, each as its statements.
    let rec blocksWithin (expr: Expression) : Statement list list =
        let children, statements = childrenOf expr
        (if statements.IsEmpty then [] else [ statements ])
        @ (children |> List.collect blocksWithin)
        @ (statements |> List.collect ofStatement |> List.collect blocksWithin)

    /// The prelude function the binary operator `op` is a call to, when it is one: `__pow` for `**`, and the
    /// composition functions for `>>` and `<<`.
    let operatorFunction (op: string) =
        if op = ArithmeticOperators.power then Some ArithmeticOperators.powerFunction else CompositionOperators.functionFor op

    /// Where the name of a call, pipe or member access is written (`spanOpt`), or the whole expression if not known.
    let nameSpan (spanOpt: SourceSpan option) (expr: Expression) : SourceSpan =
        spanOpt |> Option.defaultValue expr.Span

    /// The dotted name `expr` is written as, when it is a name or a chain of member accesses on one: `t.Encoder.string`.
    let rec dottedName (expr: Expression) : string option =
        match expr.Kind with
        | IdentifierExpr name -> Some name
        | MemberAccess(inner, memberName, _) -> dottedName inner |> Option.map (fun prefix -> $"{prefix}.{memberName}")
        | _ -> None

    /// Every name an expression may refer to: identifiers, callees and pipe targets, the prelude function an operator
    /// is a call to, and for a member access `a.b.c` both the dotted name and the member as `.c`, since the backend
    /// may call a definition named either way. Shadowing is ignored, so this can only over-count.
    let rec referencedNames (expr: Expression) : string list =
        match expr.Kind with
        | IdentifierExpr name -> [ name ]
        | FunctionCall(name, _, args) -> name :: (args |> List.collect referencedNames)
        | Pipe(value, name, _, args) -> name :: referencedNames value @ (args |> List.collect referencedNames)
        | Lambda(_, body, _)
        | ContextNames(_, body) -> referencedNames body
        | BinaryOp(op, _, left, right) -> Option.toList (operatorFunction op) @ referencedNames left @ referencedNames right
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
        | TypeValue _
        | UnitExpr
        | LiteralExpr _ -> []

    and referencedNamesInStatement (statement: Statement) : string list =
        match statement.Kind with
        | DefStatement(_, _, _, value) -> referencedNames value
        | ExprStatement value -> referencedNames value
        | UseStatement value -> referencedNames value
        | ImportStatement _
        | TypeDefStatement _
        | AttributeStatement _ -> []

    and referencedNamesInPattern (pattern: Pattern) : string list =
        match pattern.Kind with
        | GuardPattern predicate -> referencedNames predicate
        | RangePattern(startValue, endValue) -> referencedNames startValue @ referencedNames endValue
        | TagPattern(_, Some payload) -> referencedNamesInPattern payload
        | ArgumentTagPattern(_, arguments, payload) -> referencedNames arguments @ (payload |> Option.toList |> List.collect referencedNamesInPattern)
        | TuplePattern(patterns, _)
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
            | IsSyntax.BindingWhile loop -> free bound { expr with Kind = loop }
            | FunctionCall(name, _, args)
            | ContextMemberCall(_, name, args) -> named name @ (args |> List.collect (free bound))
            | Pipe(value, name, _, args) -> named name @ ((value :: args) |> List.collect (free bound))
            | Lambda(parameters, body, _) -> free (parameters |> List.fold (fun acc (name, _) -> Set.add name acc) bound) body
            | Match(values, arms) -> (values |> List.collect (free bound)) @ inArms arms
            | Except(value, arms) -> free bound value @ inArms arms
            | IsSyntax.BindingIf asMatch -> free bound { expr with Kind = asMatch }
            // Each statement's names are kept apart and joined once at the end: adding each to all those before made
            // a block of n statements take n² time (#2408).
            | Block statements ->
                statements
                |> List.fold (fun (bound: Set<Identifier>, found) statement ->
                    match statement.Kind with
                    | DefStatement(_, name, _, value) ->
                        let inValue = match value.Kind with RecSyntax.Call _ -> bound.Add name | _ -> bound
                        bound.Add name, free inValue value :: found
                    | _ -> bound, (ofStatement statement |> List.collect (free bound)) :: found) (bound, [])
                |> snd
                |> List.rev
                |> List.concat
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

    /// Every import in `module'`, a re-export's among them: at the top level and in module blocks, in the order they are
    /// written, then those in blocks.
    let allImports (module': Module) : ImportItem list =
        let rec ofTopLevel (item: TopLevelItem) =
            match item.Kind with
            | ModuleBlock(_, items) -> items |> List.collect ofTopLevel
            | _ -> collectImports [ item ]
        let inBlocks =
            module'
            |> List.collect ofItem
            |> List.collect expressionsWithin
            |> List.collect (fun expr ->
                match expr.Kind with
                | Block statements -> statements |> List.collect (fun statement -> match statement.Kind with ImportStatement items -> items | _ -> [])
                | _ -> [])
        (module' |> List.collect ofTopLevel) @ inBlocks
