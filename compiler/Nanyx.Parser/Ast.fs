namespace Parser.Program

open System
open FParsec

// AST Types - Simplified for basic parsing
type Identifier = string
type ModuleName = string

/// Where a syntax node came from: character offsets into its file's source (`End` is one past the last
/// character, and trailing whitespace is not included), plus the zero-based line and column of `Start`.
type SourceSpan =
    { File: string
      Start: int
      End: int
      StartLine: int
      StartColumn: int }

module SourceSpan =
    /// The span of a node the compiler made up rather than parsed.
    let none = { File = ""; Start = -1; End = -1; StartLine = -1; StartColumn = -1 }

    let isNone (span: SourceSpan) = span.Start < 0

    /// From the start of `first` to the end of `last`; a made-up span on either side gives the other one.
    let between (first: SourceSpan) (last: SourceSpan) =
        if isNone first then last
        elif isNone last then first
        else { first with End = last.End }

    /// Whether `inner` is written within `outer`. A made-up span is within nothing.
    let contains (outer: SourceSpan) (inner: SourceSpan) =
        not (isNone outer) && not (isNone inner) && outer.File = inner.File && outer.Start <= inner.Start && inner.End <= outer.End

/// Identifies a syntax node, so that analysis results can be kept in tables keyed by node rather than in a copy
/// of the tree. Ids are unique within a process, so nodes from different modules of a compilation never clash.
type NodeId = int64

module NodeId =
    let private last = ref 0L
    let next () : NodeId = System.Threading.Interlocked.Increment(&last.contents)

type Literal =
    | StringLit of string
    | IntLit of int
    | FloatLit of float
    | BoolLit of bool

type TypeDefModifier =
    | Export
    | Context
    | Private

type ImportSource =
    | ModuleImport of string
    | PathImport of string

type ImportItem =
    { Source: ImportSource
      Alias: Identifier option
      /// The import as written: its source and alias.
      Span: SourceSpan }

/// A type expression and where it came from. Like `Expression`, equality and comparison look only at `Kind`, and
/// it is printed (`%A`) as its kind alone. Like `Pattern`, it is declared before `Expression`.
[<CustomEquality; CustomComparison; StructuredFormatDisplay("{Kind}")>]
type TypeExpr =
    { Kind: TypeExprKind
      Span: SourceSpan
      Id: NodeId }

    override this.Equals(other) =
        match other with
        | :? TypeExpr as typeExpr -> this.Kind = typeExpr.Kind
        | _ -> false

    override this.GetHashCode() = hash this.Kind

    interface IComparable with
        member this.CompareTo(other) =
            match other with
            | :? TypeExpr as typeExpr -> compare this.Kind typeExpr.Kind
            | _ -> invalidArg "other" "a TypeExpr can only be compared with another TypeExpr"

and TypeExprKind =
    | TypeName of Identifier
    | TypeVar of Identifier
    | TypeUnit
    | TypeTuple of TypeExpr list
    | TypeRecord of TypeRecordField list
    | TypeApply of Identifier * TypeExpr list
    | TypeFunc of TypeExpr * TypeExpr
    | TypeTag of Identifier * TypeExpr option
    | TypeUnion of TypeExpr list
    | TypeIntersection of TypeExpr list
    | TypeOptional of TypeExpr
    | TypeContext of TypeExpr list
    | TypeWithContext of TypeExpr list * TypeExpr
    | TypeConstraint of Identifier * TypeExpr * Expression
    | TypeWhere of TypeExpr * Expression
    /// `typeof x`: the type of the value `x`, a name or a path of fields such as `config.server`.
    | TypeOf of Expression

and TypeRecordField =
    | TypeField of Identifier * bool * bool * TypeExpr option * Expression option
    | TypeMember of Identifier * TypeExpr option

/// A pattern and where it came from. Like `Expression`, equality and comparison look only at `Kind`, and it is
/// printed (`%A`) as its kind alone. It is declared before `Expression` so that a `.Kind` or `{ Kind = ... }` whose
/// type is not otherwise known is still taken to be an expression's.
and [<CustomEquality; CustomComparison; StructuredFormatDisplay("{Kind}")>] Pattern =
    { Kind: PatternKind
      Span: SourceSpan
      Id: NodeId }

    override this.Equals(other) =
        match other with
        | :? Pattern as pattern -> this.Kind = pattern.Kind
        | _ -> false

    override this.GetHashCode() = hash this.Kind

    interface IComparable with
        member this.CompareTo(other) =
            match other with
            | :? Pattern as pattern -> compare this.Kind pattern.Kind
            | _ -> invalidArg "other" "a Pattern can only be compared with another Pattern"

and PatternKind =
    | LiteralPattern of Literal
    | IdentifierPattern of Identifier
    | WildcardPattern
    /// `()`: the unit value, the only value of its type, so it always matches what it is matched against.
    | UnitPattern
    | ElsePattern
    | TuplePattern of Pattern list
    | RecordPattern of Identifier * Pattern list  // Type name and patterns by position
    | RecordMemberPattern of (Identifier * Pattern) list  // Patterns by member name
    | ListPattern of Pattern list * Pattern option  // patterns, optional splat at end
    | ListSplatMiddle of Pattern list * Pattern list  // patterns before ..., patterns after
    | RangePattern of Expression * Expression  // start..end (inclusive)
    | GuardPattern of Expression  // a predicate lambda, `{ < 0 }` or `{ x -> x % 2 == 0 }`: matches when it returns true
    | TagPattern of Identifier * Pattern option  // #tagName or #tagName(pattern)

/// A statement in a block and where it came from. Like `Expression`, equality and comparison look only at `Kind`,
/// and it is printed (`%A`) as its kind alone. Like `Pattern`, it is declared before `Expression`.
and [<CustomEquality; CustomComparison; StructuredFormatDisplay("{Kind}")>] Statement =
    { Kind: StatementKind
      Span: SourceSpan
      Id: NodeId }

    override this.Equals(other) =
        match other with
        | :? Statement as statement -> this.Kind = statement.Kind
        | _ -> false

    override this.GetHashCode() = hash this.Kind

    interface IComparable with
        member this.CompareTo(other) =
            match other with
            | :? Statement as statement -> compare this.Kind statement.Kind
            | _ -> invalidArg "other" "a Statement can only be compared with another Statement"

and StatementKind =
    | DefStatement of bool * Identifier * TypeExpr option * Expression
    | TypeDefStatement of Identifier * TypeDefModifier list * (Identifier * TypeExpr option) list * TypeExpr
    | ImportStatement of ImportItem list
    | UseStatement of UseBinding
    | ExprStatement of Expression

and Definition =
    | ValueDef of bool * Identifier * TypeExpr option * Expression
    | TypeDef of Identifier * TypeDefModifier list * (Identifier * TypeExpr option) list * TypeExpr

/// An item at the top level of a module and where it came from. Like Expression, equality and comparison look only
/// at Kind, and it is printed (%A) as its kind alone. Like Pattern, it is declared before Expression.
and [<CustomEquality; CustomComparison; StructuredFormatDisplay("{Kind}")>] TopLevelItem =
    { Kind: TopLevelItemKind
      Span: SourceSpan
      Id: NodeId }

    override this.Equals(other) =
        match other with
        | :? TopLevelItem as item -> this.Kind = item.Kind
        | _ -> false

    override this.GetHashCode() = hash this.Kind

    interface IComparable with
        member this.CompareTo(other) =
            match other with
            | :? TopLevelItem as item -> compare this.Kind item.Kind
            | _ -> invalidArg "other" "a TopLevelItem can only be compared with another TopLevelItem"

and TopLevelItemKind =
    | ModuleDecl of ModuleName
    | ModuleBlock of ModuleName * TopLevelItem list
    | Def of Definition
    | Import of ImportItem list
    | Expr of Expression
    /// An `@name` attribute line; it applies to the top-level item that follows it (e.g. `@entry`).
    | Attribute of Identifier

/// An expression and where it came from. Equality and comparison look only at `Kind`, leaving out the name spans
/// some kinds carry: two expressions are the same when they are the same code, wherever it was written. It is
/// printed (`%A`) as its kind alone.
and [<CustomEquality; CustomComparison; StructuredFormatDisplay("{Kind}")>] Expression =
    { Kind: ExpressionKind
      Span: SourceSpan
      Id: NodeId }

    /// The kind without its name span. Only the top level needs it: child expressions compare by this same rule.
    member private this.Code =
        match this.Kind with
        | FunctionCall(name, Some _, args) -> FunctionCall(name, None, args)
        | Pipe(value, name, Some _, args) -> Pipe(value, name, None, args)
        | MemberAccess(target, field, Some _) -> MemberAccess(target, field, None)
        | kind -> kind

    override this.Equals(other) =
        match other with
        | :? Expression as expr -> this.Code = expr.Code
        | _ -> false

    override this.GetHashCode() = hash this.Code

    interface IComparable with
        member this.CompareTo(other) =
            match other with
            | :? Expression as expr -> compare this.Code expr.Code
            | _ -> invalidArg "other" "an Expression can only be compared with another Expression"

/// The span on `FunctionCall`, `Pipe` and `MemberAccess` is that of the name (the function or the field), which
/// is not where the expression as a whole starts.
and ExpressionKind =
    | LiteralExpr of Literal
    | UnitExpr
    | IdentifierExpr of Identifier
    | FunctionCall of Identifier * SourceSpan option * Expression list
    | Lambda of (Identifier * TypeExpr option) list * Expression
    | BinaryOp of string * Expression * Expression
    | Pipe of Expression * Identifier * SourceSpan option * Expression list  // expr \func or expr \func(args)
    | Block of Statement list
    | Match of Expression list * MatchArm list  // Can match on multiple values
    /// `value except | pattern -> replacement ...`: the cases of `value` the arms match are replaced by their arms'
    /// values, and every other case passes through unchanged. Desugaring makes it a `match` (see `Except.fs`).
    | Except of Expression * MatchArm list
    | TupleExpr of Expression list  // Positional-only tuple (for backward compat with existing tests)
    | RecordExpr of RecordField list  // Mixed tuple/record with named and/or positional fields
    | ListExpr of Expression list
    | TagExpr of Identifier * Expression option  // #tagName or #tagName(expr)
    | IfExpr of Expression * Expression * Expression  // if condition then trueExpr else falseExpr
    | MemberAccess of Expression * Identifier * SourceSpan option  // expr.field
    | ContextMemberAccess of TypeExpr * Identifier  // C(T).field - qualified context access (as value)
    | ContextMemberCall of TypeExpr * Identifier * Expression list  // C(T).field(args) - qualified context call
    | UseIn of UseBinding * Expression
    | WorkflowBindExpr of Identifier * Expression
    | WorkflowReturnExpr of Expression
    | InterpolatedString of StringPart list

and StringPart =
    | StringText of string
    | StringExpr of Expression

// Record field: either named (x = expr) or positional (expr)
and RecordField =
    | NamedField of Identifier * Expression
    | PositionalField of Expression

and MatchArm = Pattern list * Expression  // Multiple patterns for multi-value match

and UseBinding =
    | UseValue of Expression
    | UseContextInstance of TypeExpr * Expression  // use C(T) = expr

// Helper type for parsing list patterns
type ListPatternElement =
    | RegularPattern of Pattern
    | Splat of Pattern  // the rest: an IdentifierPattern for `...rest`, a WildcardPattern (spanned at the `...`) for `...`


type Module = TopLevelItem list

[<AutoOpen>]
module KindPattern =
    /// Matches a node (an expression, a pattern, a statement, a type expression or a top-level item) by its kind inside
    /// a larger pattern:
    /// `FunctionCall(_, _, [ Kind (IdentifierExpr name) ])`, `TagPattern(_, Some (Kind (IdentifierPattern name)))`.
    let inline (|Kind|) (node: ^Node) = (^Node: (member Kind: ^Kind) node)

module Expression =
    /// An expression written at `span`.
    let at (span: SourceSpan) (kind: ExpressionKind) : Expression =
        { Kind = kind; Span = span; Id = NodeId.next () }

    /// An expression the compiler made up in place of `origin`; it is reported where `origin` was written.
    let from (origin: Expression) (kind: ExpressionKind) = at origin.Span kind

    /// An expression the compiler made up that has no source of its own.
    let synthetic (kind: ExpressionKind) = at SourceSpan.none kind

module TypeExpr =
    /// A type expression written at `span`.
    let at (span: SourceSpan) (kind: TypeExprKind) : TypeExpr =
        { TypeExpr.Kind = kind; Span = span; Id = NodeId.next () }

    /// A type expression the compiler made up that has no source of its own.
    let synthetic (kind: TypeExprKind) = at SourceSpan.none kind

module Pattern =
    /// A pattern written at `span`.
    let at (span: SourceSpan) (kind: PatternKind) : Pattern =
        { Pattern.Kind = kind; Span = span; Id = NodeId.next () }

    /// The expressions written in `pattern` and the patterns inside it: each guard's predicate and each range's ends.
    let rec expressions (pattern: Pattern) : Expression list =
        match pattern.Kind with
        | GuardPattern predicate -> [ predicate ]
        | RangePattern(low, high) -> [ low; high ]
        | TuplePattern patterns
        | RecordPattern(_, patterns) -> patterns |> List.collect expressions
        | RecordMemberPattern members -> members |> List.collect (snd >> expressions)
        | ListPattern(patterns, rest) -> (patterns @ Option.toList rest) |> List.collect expressions
        | ListSplatMiddle(before, after) -> (before @ after) |> List.collect expressions
        | TagPattern(_, payload) -> payload |> Option.toList |> List.collect expressions
        | LiteralPattern _
        | IdentifierPattern _
        | WildcardPattern
        | UnitPattern
        | ElsePattern -> []

    /// The expressions written in the patterns of match arms, as `expressions` gives them.
    let armExpressions (arms: (Pattern list * Expression) list) : Expression list =
        arms |> List.collect (fst >> List.collect expressions)

    /// `pattern` with `f` applied to each of the expressions written in it, as `expressions` gives them.
    let rec mapExpressions (f: Expression -> Expression) (pattern: Pattern) : Pattern =
        let recur = mapExpressions f
        let kind =
            match pattern.Kind with
            | GuardPattern predicate -> GuardPattern(f predicate)
            | RangePattern(low, high) -> RangePattern(f low, f high)
            | TuplePattern patterns -> TuplePattern(List.map recur patterns)
            | RecordPattern(name, patterns) -> RecordPattern(name, List.map recur patterns)
            | RecordMemberPattern members -> RecordMemberPattern(members |> List.map (fun (name, p) -> name, recur p))
            | ListPattern(patterns, rest) -> ListPattern(List.map recur patterns, Option.map recur rest)
            | ListSplatMiddle(before, after) -> ListSplatMiddle(List.map recur before, List.map recur after)
            | TagPattern(name, payload) -> TagPattern(name, Option.map recur payload)
            | LiteralPattern _
            | IdentifierPattern _
            | WildcardPattern
            | UnitPattern
            | ElsePattern -> pattern.Kind
        { pattern with Kind = kind }

module Statement =
    /// A statement written at `span`.
    let at (span: SourceSpan) (kind: StatementKind) : Statement =
        { Statement.Kind = kind; Span = span; Id = NodeId.next () }

    /// A statement the compiler made up in place of `origin`; it is reported where `origin` was written.
    let from (origin: Expression) (kind: StatementKind) = at origin.Span kind

    /// A statement the compiler made up that has no source of its own.
    let synthetic (kind: StatementKind) = at SourceSpan.none kind

    /// The expression a statement ends with, if it ends with one.
    let finalExpression (kind: StatementKind) : Expression option =
        match kind with
        | ExprStatement expr
        | DefStatement(_, _, _, expr)
        | UseStatement (UseValue expr)
        | UseStatement (UseContextInstance(_, expr)) -> Some expr
        | ImportStatement _
        | TypeDefStatement _ -> None

[<AutoOpen>]
module PipeCall =
    /// The arguments of the call that the pipe `value \f(args)` stands for. The piped value goes in front of the
    /// written arguments rather than beside them as one more value: `x \f(a, b)` is `f(x, a, b)`, and
    /// `x \f(a, y = 1)` is `f(x, a, y = 1)`. A trailing lambda is the last positional argument.
    let pipeCallArguments (value: Expression) (args: Expression list) : Expression list =
        match args with
        | Kind (TupleExpr items) :: rest -> value :: items @ rest
        | (Kind (RecordExpr fields) as record) :: rest ->
            let positional, named = fields |> List.partition (function PositionalField _ -> true | NamedField _ -> false)
            let spliced = PositionalField value :: positional @ (rest |> List.map PositionalField) @ named
            // The record keeps its node, so every phase that rebuilds this call agrees on it.
            [ { record with Kind = RecordExpr spliced } ]
        | _ -> value :: args

module TopLevelItem =
    /// A top-level item written at `span`.
    let at (span: SourceSpan) (kind: TopLevelItemKind) : TopLevelItem =
        { TopLevelItem.Kind = kind; Span = span; Id = NodeId.next () }

    /// A top-level item the compiler made up that has no source of its own.
    let synthetic (kind: TopLevelItemKind) = at SourceSpan.none kind

/// How a node declares names. One node can declare in more than one way: `def f = { x -> x }` declares `f` by
/// the lambda as a def's value, and `x` by the lambda's parameters.
type Declarer =
    /// The value of a def declares the def's name.
    | DefValue
    /// A lambda declares its parameters.
    | LambdaParameters
    /// The body of a match arm declares the variables its patterns bind.
    | ArmBody
    /// The body of a type definition (or a context's) declares the type's name.
    | TypeDefBody
    /// A record type declares its named fields: a context's are its members.
    | RecordTypeFields

/// The names nodes declare, each with the span it is written at. Declarations have no nodes of their own yet
/// (#96), so their spans are kept here, keyed by the node they belong to.
type DeclaredNames = Map<NodeId * Declarer, (Identifier * SourceSpan) list>
