/// Comparing syntax trees for the formatter: whether the formatted source still says the same thing.
module Nanyx.Formatter.Syntax

open System.Collections.Generic
open Parser.Program

/// The items with what formatting may change taken out: each destructuring temporary, whose name says where its def is written, is renamed by the order it first
/// appears in, imports, a field's attributes and a type's modifiers lose their spans (the only spans syntax equality looks at), imports in a row become one and are
/// sorted (see `ImportOrder`), and a
/// lambda whose body is a block of one expression has that expression as its body, as does the match that takes a
/// lambda's pattern parameters apart.
let normalize (items: TopLevelItem list) : TopLevelItem list =
    let temps = Dictionary<string, string>()

    let name (n: Identifier) =
        if DestructuringSyntax.isTemp n then
            match temps.TryGetValue n with
            | true, renamed -> renamed
            | false, _ ->
                let renamed = $"__tuple_{temps.Count + 1}"
                temps[n] <- renamed
                renamed
        else
            n

    let importItem (item: ImportItem) =
        { item with
            Selection = item.Selection |> Option.map (List.map (fun (name, _) -> name, SourceSpan.none))
            Span = SourceSpan.none }

    // `{ x -> e }` and `{ x ->` / `e` / `}` (a block of the one statement `e`) are the same lambda. A lambda with a
    // pattern parameter has its body in the arm of the match that takes the pattern apart (see `patternArgument`).
    let rec lambdaBody (parameters: (Identifier * TypeExpr option) list) (body: Expression) =
        let single (x: Expression) =
            match x.Kind with
            | Block [ { Kind = ExprStatement single } ] -> single
            | _ -> x
        let patternArguments = parameters |> List.mapi (fun index (n, _) -> n, index + 1) |> List.filter (fun (n, position) -> n = patternArgument position) |> List.map fst
        match body.Kind with
        | Match(scrutinees, [ patterns, arm ]) when
            scrutinees |> List.forall (fun s -> match s.Kind with IdentifierExpr n -> List.contains n patternArguments | _ -> false) ->
            { body with Kind = Match(scrutinees, [ patterns, single arm ]) }
        | ContextNames(names, inner) -> { body with Kind = ContextNames(names, lambdaBody parameters inner) }
        | _ -> single body

    let rec expr (e: Expression) : Expression =
        let kind =
            match e.Kind with
            | LiteralExpr _
            | UnitExpr -> e.Kind
            | IdentifierExpr n -> IdentifierExpr(name n)
            | FunctionCall(n, span, args) -> FunctionCall(n, span, List.map expr args)
            | Lambda(parameters, body, takesUnit) ->
                Lambda(parameters |> List.map (fun (n, t) -> n, Option.map typeExpr t), expr (lambdaBody parameters body), takesUnit)
            | ContextNames(names, body) ->
                ContextNames(names |> List.map (fun (n, t, _) -> name n, Option.map typeExpr t, SourceSpan.none), expr (lambdaBody [] body))
            | BinaryOp(op, opSpan, l, r) -> BinaryOp(op, opSpan, expr l, expr r)
            | Pipe(value, n, span, args) -> Pipe(expr value, n, span, List.map expr args)
            | Block statements -> Block(List.map statement statements)
            | Match(scrutinees, arms) -> Match(List.map expr scrutinees, arms |> List.map (fun (ps, body) -> List.map pattern ps, expr body))
            | Except(value, arms) -> Except(expr value, arms |> List.map (fun (ps, body) -> List.map pattern ps, expr body))
            | TupleExpr items -> TupleExpr(List.map expr items)
            | RecordExpr fields -> RecordExpr(List.map field fields)
            | ListExpr items -> ListExpr(List.map expr items)
            | TagExpr(n, payload) -> TagExpr(n, Option.map expr payload)
            | IfExpr(c, t, f) -> IfExpr(expr c, expr t, expr f)
            | MemberAccess(target, f, span) -> MemberAccess(expr target, f, span)
            | ContextMemberAccess(t, f) -> ContextMemberAccess(typeExpr t, f)
            | ContextMemberCall(t, f, args) -> ContextMemberCall(typeExpr t, f, List.map expr args)
            | TypeValue t -> TypeValue(typeExpr t)
            | UseIn(value, body) -> UseIn(expr value, expr body)
            | WorkflowBindExpr(n, value) -> WorkflowBindExpr(n, expr value)
            | WorkflowReturnExpr value -> WorkflowReturnExpr(expr value)
            | InterpolatedString parts ->
                InterpolatedString(parts |> List.map (function StringText s -> StringText s | StringExpr e -> StringExpr(expr e)))
        { e with Kind = kind }

    and field (f: RecordField) =
        match f with
        | NamedField(n, value) -> NamedField(n, expr value)
        | PositionalField value -> PositionalField(expr value)

    and pattern (p: Pattern) : Pattern =
        let kind =
            match p.Kind with
            | LiteralPattern _
            | IdentifierPattern _
            | WildcardPattern
            | UnitPattern
            | ElsePattern -> p.Kind
            | TuplePattern(ps, hasRest) -> TuplePattern(List.map pattern ps, hasRest)
            | AndPattern ps -> AndPattern(List.map pattern ps)
            | RecordPattern(n, ps) -> RecordPattern(n, List.map pattern ps)
            | RecordMemberPattern(fields, hasRest) -> RecordMemberPattern(fields |> List.map (fun (n, p) -> n, pattern p), hasRest)
            | ListPattern(ps, rest) -> ListPattern(List.map pattern ps, Option.map pattern rest)
            | ListSplatMiddle(before, after) -> ListSplatMiddle(List.map pattern before, List.map pattern after)
            | RangePattern(a, b) -> RangePattern(expr a, expr b)
            | GuardPattern predicate -> GuardPattern(expr predicate)
            | TagPattern(n, payload) -> TagPattern(n, Option.map pattern payload)
            | ArgumentTagPattern(n, args, payload) -> ArgumentTagPattern(n, expr args, Option.map pattern payload)
        { p with Kind = kind }

    and typeExpr (t: TypeExpr) : TypeExpr =
        let kind =
            match t.Kind with
            | TypeName _
            | TypeVar _
            | TypeHole
            | TypeUnit
            | TypeKeyword -> t.Kind
            | TypeNamedArgument(n, value) -> TypeNamedArgument(n, typeExpr value)
            | TypeAssociated(context, n) -> TypeAssociated(typeExpr context, n)
            | TypeArgument operand -> TypeArgument(typeExpr operand)
            | TypeTuple items -> TypeTuple(List.map typeExpr items)
            | TypeRecord fields -> TypeRecord(List.map typeField fields)
            | TypeOpenRecord(fields, row) -> TypeOpenRecord(List.map typeField fields, row)
            | TypeApply(n, args) -> TypeApply(n, List.map typeExpr args)
            | TypeFunc(a, b) -> TypeFunc(typeExpr a, typeExpr b)
            | TypeTag(n, payload) -> TypeTag(n, Option.map typeExpr payload)
            | TypeUnion items -> TypeUnion(List.map typeExpr items)
            | TypeIntersection items -> TypeIntersection(List.map typeExpr items)
            | TypeContext items -> TypeContext(List.map typeExpr items)
            | TypeWithContext(contexts, inner) -> TypeWithContext(List.map typeExpr contexts, typeExpr inner)
            | TypeConstraint(n, baseType, predicate) -> TypeConstraint(n, typeExpr baseType, expr predicate)
            | TypeWhere(baseType, predicate) -> TypeWhere(typeExpr baseType, expr predicate)
            | TypeOf value -> TypeOf(expr value)
        { t with Kind = kind }

    and typeField (f: TypeRecordField) =
        match f with
        | TypeField(n, isMutable, t, defaultValue) -> TypeField(n, isMutable, Option.map typeExpr t, Option.map expr defaultValue)
        | TypeFieldAttribute(n, argument, _) -> TypeFieldAttribute(n, Option.map expr argument, SourceSpan.none)

    and typeDef (n, modifiers, parameters, body) =
        let modifier m =
            match m with
            | KindAt _ -> KindAt SourceSpan.none
            | Scoped _ -> Scoped SourceSpan.none
            | InScopeOf contexts -> InScopeOf (contexts |> List.map (fun (context, _) -> context, SourceSpan.none))
            | Export | Context | Private | Protected | AssociatedTypes _ -> m
        n, List.map modifier modifiers, parameters |> List.map (fun (p, t) -> p, Option.map typeExpr t), typeExpr body

    and statement (s: Statement) : Statement =
        let kind =
            match s.Kind with
            | DefStatement(isExport, n, t, value) -> DefStatement(isExport, name n, Option.map typeExpr t, expr value)
            | TypeDefStatement(n, modifiers, parameters, body) -> TypeDefStatement(typeDef (n, modifiers, parameters, body))
            | ImportStatement items -> ImportStatement(items |> List.map importItem |> ImportOrder.sort)
            | UseStatement value -> UseStatement(expr value)
            | ExprStatement value -> ExprStatement(expr value)
            | AttributeStatement(n, argument) -> AttributeStatement(n, Option.map expr argument)
        { s with Kind = kind }

    let rec item (i: TopLevelItem) : TopLevelItem =
        let kind =
            match i.Kind with
            | ModuleDecl _ -> i.Kind
            | Attribute(n, argument) -> Attribute(n, Option.map expr argument)
            | ModuleBlock(n, items) -> ModuleBlock(n, items' items)
            | Def(ValueDef(isExport, n, t, value)) -> Def(ValueDef(isExport, name n, Option.map typeExpr t, expr value))
            | Def(TypeDef(n, modifiers, parameters, body)) -> Def(TypeDef(typeDef (n, modifiers, parameters, body)))
            | Import items -> Import(List.map importItem items)
            | ReExport item -> ReExport(importItem item)
            | Expr value -> Expr(expr value)
        { i with Kind = kind }

    // Imports in a row are one import of all their items, as the formatter writes them, sorted. The formatter sorts
    // only within a run that no comment interrupts, but its sort is stable, so sorting the whole of each side gives the
    // same list whenever the formatter neither dropped nor added an import. The formatter keeps imports apart where a
    // blank line separates them; merging them here regardless gives both sides the same items either way.
    and items' (items: TopLevelItem list) =
        items
        |> List.map item
        |> ImportOrder.mergeRuns
            (fun i -> match i.Kind with Import imports -> Some imports | _ -> None)
            (fun _ _ -> true)
            (fun run imports -> { List.head run with Kind = Import imports })
        |> List.map (fun i ->
            match i.Kind with
            | Import imports -> { i with Kind = Import(ImportOrder.sort imports) }
            | _ -> i)

    items' items

/// Whether two parses say the same thing, ignoring what formatting may change (see `normalize`).
let same (a: TopLevelItem list) (b: TopLevelItem list) = normalize a = normalize b
