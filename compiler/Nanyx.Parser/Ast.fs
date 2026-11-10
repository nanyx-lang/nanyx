namespace Parser.Program

open System
open FParsec

// AST Types - Simplified for basic parsing
type Identifier = string
type ModuleName = string

/// Names that are operators, such as `==` in `` def Point.`==` = ... `` (see the operators page of the docs).
module Names =
    /// The characters an operator's name is made of.
    let isOperatorChar (c: char) = "+-*/%^~<>=!".IndexOf c >= 0

    /// Spellings made of operator characters that are syntax, not operators: `=`, `->`, `=>`, and anything
    /// starting `--`, which is a comment.
    let isReservedOperatorSpelling (name: string) =
        name = "=" || name = "->" || name = "=>" || name.StartsWith "--"

    /// Whether `name` is an operator's name, which is written in backticks where a name goes.
    let isOperator (name: string) = name <> "" && String.forall isOperatorChar name

    /// Whether `c` can be part of a name in backticks: anything but a backtick, a line break, or a dot, which
    /// separates the parts of a qualified name.
    let isBacktickedChar (c: char) = c <> '`' && c <> '\n' && c <> '\r' && c <> '.'

    /// Whether `name` is an identifier, which is written without backticks.
    let isIdentifier (name: string) =
        name <> ""
        && (System.Char.IsLetter name[0] || name[0] = '_')
        && String.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_') name

    /// `name` as it is written where a name goes: an identifier or a nominal type's name (`$Email`) as it is, and
    /// anything else, such as an operator or a sentence, in backticks. A keyword is an identifier here: it is a name
    /// where a name is expected, as a workflow builder's `try` and `return` fields are. Each part of a qualified name
    /// is written on its own: `` Point.`+` ``.
    let written (name: string) =
        let isBare (part: string) = isIdentifier part || (part.StartsWith "$" && isIdentifier part[1..])
        name.Split '.'
        |> Array.map (fun part -> if isBare part then part else $"`{part}`")
        |> String.concat "."

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
    /// `_`: a part of the type left for inference to fill in, as `list(_)`. Each one is a type of its own.
    | TypeHole
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
    /// `p1 & p2 & ...`: matches when every one of its patterns matches the same value, and binds the names of all
    /// of them. It has two parts or more.
    | AndPattern of Pattern list

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
    /// An `@name` attribute line; it applies to the top-level item that follows it (e.g. `@entry`). An argument is
    /// written in parentheses straight after the name, like a tag's payload: `@test(#io)`.
    | Attribute of Identifier * Expression option

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

/// `for xs, ys | arms` (see the loops page): a call to the built-in "__for" with the iterables and then a lambda that
/// takes one element of each, in order. The loop walks the iterables in step and stops when the shortest ends.
module ForSyntax =
    let builtIn = "__for"

    /// The loop lambda's parameter for an element of the `position`th iterable (counted from 1). Like a shorthand
    /// lambda's parameter, no code can write it.
    let element (position: int) = if position = 1 then "__for_element" else $"__for_element{position}"

    /// A `for` loop's iterables and its loop lambda, with that lambda's parameters and body.
    let (|Call|_|) (kind: ExpressionKind) =
        match kind with
        | FunctionCall(name, _, args) when name = builtIn && args.Length >= 2 ->
            match List.last args with
            | { Kind = Lambda(parameters, body) } as loop -> Some (List.take (args.Length - 1) args, loop, parameters, body)
            | _ -> None
        | _ -> None

/// `a..b` (see the ranges page): a call to the built-in "__range" with its bounds, or with only its start for the
/// unbounded `a..`.
module RangeSyntax =
    let builtIn = "__range"

    /// A range's start, and its end when it has one.
    let (|Call|_|) (kind: ExpressionKind) =
        match kind with
        | FunctionCall(name, _, [ first; last ]) when name = builtIn -> Some (first, Some last)
        | FunctionCall(name, _, [ first ]) when name = builtIn -> Some (first, None)
        | _ -> None

module TypeExpr =
    /// A type expression written at `span`.
    let at (span: SourceSpan) (kind: TypeExprKind) : TypeExpr =
        { TypeExpr.Kind = kind; Span = span; Id = NodeId.next () }

    /// A type expression the compiler made up that has no source of its own.
    let synthetic (kind: TypeExprKind) = at SourceSpan.none kind

    /// The type expressions directly inside `typeExpr`.
    let children (typeExpr: TypeExpr) : TypeExpr list =
        match typeExpr.Kind with
        | TypeName _
        | TypeVar _
        | TypeHole
        | TypeUnit
        | TypeOf _ -> []
        | TypeTuple items
        | TypeUnion items
        | TypeIntersection items
        | TypeContext items
        | TypeApply(_, items) -> items
        | TypeRecord fields ->
            fields |> List.choose (function TypeField(_, _, _, typeOpt, _) | TypeMember(_, typeOpt) -> typeOpt)
        | TypeFunc(arg, ret) -> [ arg; ret ]
        | TypeTag(_, payload) -> Option.toList payload
        | TypeWithContext(contexts, inner) -> contexts @ [ inner ]
        | TypeOptional inner
        | TypeConstraint(_, inner, _)
        | TypeWhere(inner, _) -> [ inner ]

    /// Each `_` written in `typeExpr`, in order.
    let rec holes (typeExpr: TypeExpr) : TypeExpr list =
        match typeExpr.Kind with
        | TypeHole -> [ typeExpr ]
        | _ -> children typeExpr |> List.collect holes

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
        | AndPattern patterns
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

    /// The names `pattern` binds, in the order they are written: its identifier patterns and its named list rests.
    let rec boundNames (pattern: Pattern) : Identifier list =
        match pattern.Kind with
        | IdentifierPattern name -> [ name ]
        | TuplePattern patterns
        | AndPattern patterns
        | RecordPattern(_, patterns) -> patterns |> List.collect boundNames
        | RecordMemberPattern members -> members |> List.collect (snd >> boundNames)
        | ListPattern(patterns, rest) -> (patterns @ Option.toList rest) |> List.collect boundNames
        | ListSplatMiddle(before, after) -> (before @ after) |> List.collect boundNames
        | TagPattern(_, payload) -> payload |> Option.toList |> List.collect boundNames
        | GuardPattern _
        | RangePattern _
        | LiteralPattern _
        | WildcardPattern
        | UnitPattern
        | ElsePattern -> []

    /// The field of a record with the fields `fields` that the `index`th element of a tuple pattern matches. A name
    /// alone is shorthand for `name = name` where the record has a field of that name, so `(name, age)` takes apart
    /// `(name: string, age: int)`, and `#some(x, y)` the payload of `#some(x: string, y: string)`. Any other element
    /// matches the field at its position, as in a tuple.
    let tupleElementField (fields: Set<string>) (index: int) (element: Pattern) : string =
        match element.Kind with
        | IdentifierPattern name when fields.Contains name -> name
        | _ -> string index

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
            | AndPattern patterns -> AndPattern(List.map recur patterns)
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

module MatchHead =
    /// The head of the match `matchExpr` on `scrutinees`: what is reported for the match as a whole, rather than
    /// for one arm. That depends on how the match was written:
    /// - `match a | ...` is from `match` to the end of the last value matched on, so `match a`, or
    ///   `match getData() \mapOption { ... }`.
    /// - `{ | pattern -> ... }`, and a `for` loop's arms, match on values made up over the whole match, arms and
    ///   all. Their head is the first character, the `{` or the first `|`.
    /// - A lambda's pattern parameters (`{ #some(n) -> n }`) match on values made up where the patterns are
    ///   written, so they are the head, from the first to the last.
    /// - A match made up over its value (`def #some(x) = value`, `value except | ...`) has the value as its head.
    let span (matchExpr: Expression) (scrutinees: Expression list) (arms: MatchArm list) : SourceSpan =
        let firstPatterns = arms |> List.tryHead |> Option.map fst |> Option.defaultValue []
        match scrutinees with
        | [] -> matchExpr.Span
        | first :: _ ->
            let last = List.last scrutinees
            let madeUpOverArms =
                first.Span = matchExpr.Span && firstPatterns |> List.exists (fun pattern -> SourceSpan.contains first.Span pattern.Span)
            let madeUpAtPatterns =
                not (SourceSpan.isNone first.Span)
                && firstPatterns.Length = scrutinees.Length
                && List.forall2 (fun (scrutinee: Expression) (pattern: Pattern) -> scrutinee.Span = pattern.Span) scrutinees firstPatterns
            if madeUpOverArms then { matchExpr.Span with End = matchExpr.Span.Start + 1 }
            elif madeUpAtPatterns then SourceSpan.between first.Span last.Span
            elif SourceSpan.contains matchExpr.Span last.Span then SourceSpan.between matchExpr.Span last.Span
            else matchExpr.Span

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
