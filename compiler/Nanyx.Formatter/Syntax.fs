/// Comparing syntax trees for the formatter: whether the formatted source still says the same thing.
module Nanyx.Formatter.Syntax

open System.Collections.Generic
open System.Text.RegularExpressions
open Parser.Program

/// The name a destructuring `def (a, b) = ...` binds its whole value to is made from where the def is written
/// (`__tuple_3_1`), so it changes when the def moves.
let private tupleTemp = Regex(@"^__tuple_\d+_\d+$")

/// The items with what formatting may change taken out: each destructuring temporary is renamed by the order it first
/// appears in, imports lose their spans (the only spans syntax equality looks at), imports in a row become one, and a
/// lambda whose body is a block of one expression has that expression as its body.
let normalize (items: TopLevelItem list) : TopLevelItem list =
    let temps = Dictionary<string, string>()

    let name (n: Identifier) =
        if tupleTemp.IsMatch n then
            match temps.TryGetValue n with
            | true, renamed -> renamed
            | false, _ ->
                let renamed = $"__tuple_{temps.Count + 1}"
                temps[n] <- renamed
                renamed
        else
            n

    let importItem (item: ImportItem) = { item with Span = SourceSpan.none }

    let rec expr (e: Expression) : Expression =
        let kind =
            match e.Kind with
            | LiteralExpr _
            | UnitExpr -> e.Kind
            | IdentifierExpr n -> IdentifierExpr(name n)
            | FunctionCall(n, span, args) -> FunctionCall(n, span, List.map expr args)
            | Lambda(parameters, body) ->
                // `{ x -> e }` and `{ x ->` / `e` / `}` (a block of the one statement `e`) are the same lambda.
                let body =
                    match body.Kind with
                    | Block [ { Kind = ExprStatement single } ] -> single
                    | _ -> body
                Lambda(parameters |> List.map (fun (n, t) -> n, Option.map typeExpr t), expr body)
            | BinaryOp(op, l, r) -> BinaryOp(op, expr l, expr r)
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
            | TuplePattern ps -> TuplePattern(List.map pattern ps)
            | AndPattern ps -> AndPattern(List.map pattern ps)
            | RecordPattern(n, ps) -> RecordPattern(n, List.map pattern ps)
            | RecordMemberPattern(fields, hasRest) -> RecordMemberPattern(fields |> List.map (fun (n, p) -> n, pattern p), hasRest)
            | ListPattern(ps, rest) -> ListPattern(List.map pattern ps, Option.map pattern rest)
            | ListSplatMiddle(before, after) -> ListSplatMiddle(List.map pattern before, List.map pattern after)
            | RangePattern(a, b) -> RangePattern(expr a, expr b)
            | GuardPattern predicate -> GuardPattern(expr predicate)
            | TagPattern(n, payload) -> TagPattern(n, Option.map pattern payload)
        { p with Kind = kind }

    and typeExpr (t: TypeExpr) : TypeExpr =
        let kind =
            match t.Kind with
            | TypeName _
            | TypeVar _
            | TypeHole
            | TypeUnit -> t.Kind
            | TypeTuple items -> TypeTuple(List.map typeExpr items)
            | TypeRecord fields -> TypeRecord(List.map typeField fields)
            | TypeApply(n, args) -> TypeApply(n, List.map typeExpr args)
            | TypeFunc(a, b) -> TypeFunc(typeExpr a, typeExpr b)
            | TypeTag(n, payload) -> TypeTag(n, Option.map typeExpr payload)
            | TypeUnion items -> TypeUnion(List.map typeExpr items)
            | TypeIntersection items -> TypeIntersection(List.map typeExpr items)
            | TypeOptional inner -> TypeOptional(typeExpr inner)
            | TypeContext items -> TypeContext(List.map typeExpr items)
            | TypeWithContext(contexts, inner) -> TypeWithContext(List.map typeExpr contexts, typeExpr inner)
            | TypeConstraint(n, baseType, predicate) -> TypeConstraint(n, typeExpr baseType, expr predicate)
            | TypeWhere(baseType, predicate) -> TypeWhere(typeExpr baseType, expr predicate)
            | TypeOf value -> TypeOf(expr value)
        { t with Kind = kind }

    and typeField (f: TypeRecordField) =
        match f with
        | TypeField(n, isMutable, isOptional, t, defaultValue) -> TypeField(n, isMutable, isOptional, Option.map typeExpr t, Option.map expr defaultValue)
        | TypeMember(n, t) -> TypeMember(n, Option.map typeExpr t)

    and typeDef (n, modifiers, parameters, body) =
        n, modifiers, parameters |> List.map (fun (p, t) -> p, Option.map typeExpr t), typeExpr body

    and statement (s: Statement) : Statement =
        let kind =
            match s.Kind with
            | DefStatement(isExport, n, t, value) -> DefStatement(isExport, name n, Option.map typeExpr t, expr value)
            | TypeDefStatement(n, modifiers, parameters, body) -> TypeDefStatement(typeDef (n, modifiers, parameters, body))
            | ImportStatement items -> ImportStatement(List.map importItem items)
            | UseStatement value -> UseStatement(expr value)
            | ExprStatement value -> ExprStatement(expr value)
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
            | Expr value -> Expr(expr value)
        { i with Kind = kind }

    // Imports in a row are one import of all their items, as the formatter writes them.
    and items' (items: TopLevelItem list) =
        items
        |> List.map item
        |> List.fold
            (fun (merged: TopLevelItem list) (i: TopLevelItem) ->
                match i.Kind, merged with
                | Import imports, previous :: rest ->
                    match previous.Kind with
                    | Import run -> { previous with Kind = Import(run @ imports) } :: rest
                    | _ -> i :: merged
                | _ -> i :: merged)
            []
        |> List.rev

    items' items

/// Whether two parses say the same thing, ignoring what formatting may change (see `normalize`).
let same (a: TopLevelItem list) (b: TopLevelItem list) = normalize a = normalize b
