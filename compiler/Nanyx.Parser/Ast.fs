namespace Parser.Program

open System

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

    /// Whether `name`, written in a type, is a type variable: a single lowercase letter. Longer lowercase names are
    /// reserved for associated types (see the types page).
    let isTypeVariable (name: string) = name.Length = 1 && System.Char.IsLower name[0]

    /// `name` as it is written where a name goes: an identifier as it is, and anything else, such as an operator or a
    /// sentence, in backticks. A keyword is an identifier here: it is a name where a name is expected, as a workflow
    /// builder's `try` and `return` fields are. Each part of a qualified name is written on its own: `` Point.`+` ``.
    let written (name: string) =
        name.Split '.'
        |> Array.map (fun part -> if isIdentifier part then part else $"`{part}`")
        |> String.concat "."

    /// The part of a name after its last dot: `shout` in `string.shout`, and the whole of a name without one.
    let ownPart (name: string) = name.Substring(name.LastIndexOf '.' + 1)

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

    /// The text of `span` in `source`, the text of its file.
    let textIn (source: string) (span: SourceSpan) = source.Substring(span.Start, span.End - span.Start)

/// Identifies a syntax node, so that analysis results can be kept in tables keyed by node rather than in a copy
/// of the tree. Ids are unique within a process, so nodes from different modules of a compilation never clash.
type NodeId = int64

module NodeId =
    let private last = ref 0L
    let next () : NodeId = System.Threading.Interlocked.Increment(&last.contents)

    /// Makes `first` the next id, if no id from it on has been given: for a process that writes nodes another process
    /// reads, so that their ids never meet those the reading process gives its own.
    let startAt (first: NodeId) = System.Threading.Interlocked.Exchange(&last.contents, max last.Value (first - 1L)) |> ignore

/// A number exactly as written, sign and all: `Digits * 10 ** Exponent`, so `0.001` is 1 and -3 and `1.5e3` is 15 and 2,
/// and whether it was written as a decimal, with a `.` or an exponent, rather than as a whole number.
type WrittenNumber = { Digits: bigint; Exponent: int; Decimal: bool }

module WrittenNumber =
    /// `number` written out: `1000`, `0.001`, `1.5e3` as `15e2`.
    let text (number: WrittenNumber) : string =
        let sign = if number.Digits.Sign < 0 then "-" else ""
        let digits = string (abs number.Digits)
        if number.Exponent >= 0 then
            sign + digits + (if number.Decimal then $"e{number.Exponent}" else System.String('0', number.Exponent))
        else
            let digits = digits.PadLeft(1 - number.Exponent, '0')
            let point = digits.Length + number.Exponent
            sign + digits.Substring(0, point) + "." + digits.Substring point

type Literal =
    | StringLit of string
    | IntLit of bigint
    | FloatLit of float
    | BoolLit of bool
    /// A number with a unit written straight after it, `500ms` or `0.5s`: the number exactly, and the unit's name (see
    /// `UnitSyntax`).
    | UnitLit of WrittenNumber * unit: Identifier

type TypeDefModifier =
    | Export
    | Context
    /// `type UserId = private int`: a nominal type whose shape is hidden outside its home module.
    | Private
    /// `type Email = protected string`: a nominal type whose shape is visible everywhere, though only its home module
    /// constructs one.
    | Protected
    /// `scoped context`: nothing that requires the context can leave the scope that supplies it. The span is the
    /// `scoped` keyword's.
    | Scoped of SourceSpan
    /// `type Cursor = [Tx] protected (...)`: a value of the type belongs to the scope of the scoped context it was made
    /// under. The contexts written, each where it is written.
    | InScopeOf of (Identifier * SourceSpan) list
    /// Where a type declaration's `private` or `protected` is written, so that a fix can write contexts before it.
    | KindAt of SourceSpan
    /// A context's associated types, which the compiler makes its last parameters, in the order its members declare
    /// them: `elem` in `context Iter(c) = (elem: type, ...)`, which it reads as `Iter(c, elem)`. A signature doesn't
    /// write them (see `NyxCompiler.AssociatedTypes`).
    | AssociatedTypes of Identifier list

module TypeDefModifier =
    let isScoped (modifier: TypeDefModifier) =
        match modifier with
        | Scoped _ -> true
        | Export
        | Context
        | Private
        | Protected
        | InScopeOf _
        | KindAt _
        | AssociatedTypes _ -> false

    /// The associated types the modifiers of a context's declaration name (see `AssociatedTypes`): none for any other.
    let associatedTypes (modifiers: TypeDefModifier list) =
        modifiers |> List.collect (function AssociatedTypes names -> names | _ -> [])

    /// Where the declaration with `modifiers` writes `private` or `protected`, if it does.
    let kindSpan (modifiers: TypeDefModifier list) =
        modifiers |> List.tryPick (function KindAt span -> Some span | _ -> None)

    /// The contexts a type's declaration says its values belong to the scope of (see `InScopeOf`), each where it is
    /// written; empty when it says none.
    let scopeContexts (modifiers: TypeDefModifier list) =
        modifiers |> List.collect (function InScopeOf contexts -> contexts | _ -> [])

    /// Whether a type declared with `modifiers` is nominal: protected or private, as opposed to an alias of its shape.
    let isNominal (modifiers: TypeDefModifier list) =
        modifiers |> List.exists (fun modifier -> modifier = Private || modifier = Protected)

    /// Where `scoped` is written among `modifiers`, if it is.
    let scopedSpan (modifiers: TypeDefModifier list) =
        modifiers |> List.tryPick (function Scoped span -> Some span | _ -> None)

type ImportSource =
    | ModuleImport of string
    | PathImport of string

type ImportItem =
    { Source: ImportSource
      Alias: Identifier option
      /// The names `as (a, b)` or `as m & (a, b)` selects, each where it is written. Only these come in unqualified.
      Selection: (Identifier * SourceSpan) list option
      /// The import as written: its source, alias and selection.
      Span: SourceSpan }

module ImportItem =
    /// An import of `source` under `alias`, with no selection and nowhere in the source.
    let synthetic (source: ImportSource) (alias: Identifier option) : ImportItem =
        { Source = source; Alias = alias; Selection = None; Span = SourceSpan.none }

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
    /// `(x: int, ...)` or `(x: int, ...r)`: a record with the fields it lists and any other named fields, and the name
    /// `...r` gives those others, if it gives one. Its positional fields are exactly the ones it lists.
    | TypeOpenRecord of TypeRecordField list * Identifier option
    | TypeApply of Identifier * TypeExpr list
    | TypeFunc of TypeExpr * TypeExpr
    | TypeTag of Identifier * TypeExpr option
    | TypeUnion of TypeExpr list
    | TypeIntersection of TypeExpr list
    | TypeContext of TypeExpr list
    | TypeWithContext of TypeExpr list * TypeExpr
    | TypeConstraint of Identifier * TypeExpr * Expression
    | TypeWhere of TypeExpr * Expression
    /// `typeof x`: the type of the value `x`, a name or a path of fields such as `config.server`.
    | TypeOf of Expression
    /// `type`, written as a context member's type: the member is an associated type, as `elem` is in
    /// `context Iter(c) = (elem: type, ...)`. As a function's named parameter, `into: type`, it is the old form of
    /// `into: Type(into)`, which still compiles, with a warning.
    | TypeKeyword
    /// `elem = string`, an argument of a context that fixes its associated type `elem`: `Iter(c, elem = string)`.
    | TypeNamedArgument of Identifier * TypeExpr
    /// `Iter(c).elem`: the associated type `elem` of the context `Iter(c)` a signature requires.
    | TypeAssociated of TypeExpr * Identifier
    /// `type a`, the old form of a function's positional parameter that takes a type argument, now written `Type(a)`:
    /// its value is of type `Type(a)`, and the type variable `a` is the type passed. It still compiles, with a warning.
    | TypeArgument of TypeExpr
    /// `Time * Time`, a derived dimension: the product of two dimensions (see `UnitSyntax`).
    | TypeProduct of TypeExpr * TypeExpr
    /// `Length / Time`, a derived dimension: one dimension divided by another (see `UnitSyntax`).
    | TypeQuotient of TypeExpr * TypeExpr

and TypeRecordField =
    /// A named field: its name, whether it is `mut`, its type and its default value.
    | TypeField of Identifier * bool * TypeExpr option * Expression option
    /// An `@name` attribute before a named field, written where it is: it applies to the field that follows it, as
    /// `@customKeywordType(#bind)` does to a member of a `context` declaration. Its argument is written as a top-level
    /// attribute's is.
    | TypeFieldAttribute of Identifier * Expression option * SourceSpan

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
    /// `(p1, p2)`, a tuple's elements by position, or a record's fields, a name alone matching the field it names (see
    /// `Pattern.tupleElementField`). Where `hasRest`, it ends in `...` to leave the other fields out: `(a, ...)`.
    | TuplePattern of items: Pattern list * hasRest: bool
    /// `Email(s)`, a nominal value taken apart by its type's name and the patterns in brackets, which match its
    /// representation (see `Pattern.constructorFields`). Brackets that end in `...` or name fields are one pattern, as
    /// a tag's payload's are: `P(a, ...)` holds a tuple pattern with `hasRest`, and `P(a = x)` a record member pattern.
    | RecordPattern of Identifier * Pattern list
    /// Patterns by member name, `(name = n, age = a)`. A record type is closed, so the pattern names every field
    /// unless `hasRest`, when it ends in `...` to leave the others out: `(name = "Alice", ...)`.
    | RecordMemberPattern of members: (Identifier * Pattern) list * hasRest: bool
    | ListPattern of Pattern list * Pattern option  // patterns, optional splat at end
    | ListSplatMiddle of Pattern list * Pattern list  // patterns before ..., patterns after
    | RangePattern of Expression * Expression  // start..end (inclusive)
    | GuardPattern of Expression  // a predicate lambda, `{ < 0 }` or `{ x -> x % 2 == 0 }`: matches when it returns true
    | TagPattern of Identifier * Pattern option  // #tagName or #tagName(pattern)
    /// `#SplitOn(",")(a, b)`: a custom pattern's tag with the arguments it is given, then the pattern its payload is
    /// matched against, if it is written. Several arguments are a tuple. The parser writes it only where the brackets
    /// can't be a payload: two groups, or one that is an expression but not a pattern, as `#DivisibleBy(n + 1)`.
    /// `#DivisibleBy(3)` is a `TagPattern` whose payload the compiler reads as the arguments of a pattern that takes
    /// them (see `PatternSyntax`).
    | ArgumentTagPattern of tag: Identifier * arguments: Expression * payload: Pattern option
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
    /// `use value`: `value`, a context instance, is loaded for the rest of the block.
    | UseStatement of Expression
    | ExprStatement of Expression
    /// An `@name` attribute line in a block; it applies to the local def that follows it, as `@allow(...)` does. Its
    /// argument is written as a top-level attribute's is (see `Attribute`).
    | AttributeStatement of Identifier * Expression option

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
    /// `module name`, or `export module name`, which makes the module public: another package can import it.
    | ModuleDecl of isExport: bool * ModuleName
    | ModuleBlock of ModuleName * TopLevelItem list
    | Def of Definition
    | Import of ImportItem list
    /// `export m (a, b)`: the module exports `a` and `b`, which `m` exports, as if they were its own. The item is an
    /// import of `m` that selects them.
    | ReExport of ImportItem
    | Expr of Expression
    /// An `@name` attribute line; it applies to the top-level item that follows it (e.g. `@entry`). An argument is
    /// written in parentheses straight after the name, like a tag's payload: `@test(#io)`.
    | Attribute of Identifier * Expression option
    /// `dimension Time`, a kind of quantity, a type generic in the number that stores it, or `dimension Bytes(int)`,
    /// constrained to one storage type, or `dimension Speed = Length / Time`, a name for a derived dimension (see
    /// `UnitSyntax`).
    | DimensionDecl of isExport: bool * Identifier * storage: TypeExpr option * derived: TypeExpr option
    /// `unit ms: Time = 1_000us`, a unit of a dimension that is a constant multiple of another unit, a `UnitLit`, or
    /// `unit ns: Time`, with no multiple, the one unit of its dimension that the others are defined from (see
    /// `UnitSyntax`). Its dimension is written as a type names it, a `TypeName`.
    | UnitDecl of isExport: bool * Identifier * dimension: TypeExpr * multiple: Expression option

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
        | BinaryOp(op, Some _, left, right) -> BinaryOp(op, None, left, right)
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

/// The span on `FunctionCall`, `Pipe` and `MemberAccess` is that of the name (the function or the field), and the one on
/// `BinaryOp` that of the operator, which is not where the expression as a whole starts.
and ExpressionKind =
    | LiteralExpr of Literal
    | UnitExpr
    | IdentifierExpr of Identifier
    | FunctionCall of Identifier * SourceSpan option * Expression list
    /// `takesUnit` is true for a lambda written `{ () -> ... }`, which takes unit, and false for one written without
    /// parameters, `{ ... }`, which ignores a parameter the type it is checked against gives it (both have no
    /// parameters here).
    | Lambda of parameters: (Identifier * TypeExpr option) list * body: Expression * takesUnit: bool
    /// A lambda's body, `body`, under the names `{ context l: Log, x -> ... }` writes before its parameters for the
    /// instances of contexts the lambda is given. Each is the name, the context it names if written, and the span of
    /// the name. Only the lambda's body is one of these: it is not a parameter, and a `use` is all a name can do.
    | ContextNames of names: (Identifier * TypeExpr option * SourceSpan) list * body: Expression
    | BinaryOp of string * SourceSpan option * Expression * Expression  // left op right
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
    /// `type Point`: the type written after `type`, passed as a type argument. Its value is of type `Type(Point)`.
    | TypeValue of TypeExpr
    | UseIn of Expression * Expression  // use value in body
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

/// `if c then a` with no `else` (see the control-flow page): its else branch is `()`, made up where the then branch is
/// written, so the `if` gives `()` when its condition is false.
module IfSyntax =
    /// The else branch a left-out `else` is, for the then branch `thenExpr`.
    let leftOutElse (thenExpr: Expression) = Expression.from thenExpr UnitExpr

    /// Whether `elseExpr`, the else branch of an `if` whose then branch is `thenExpr`, is one a left-out `else` made up.
    let isLeftOut (thenExpr: Expression) (elseExpr: Expression) = elseExpr.Kind = UnitExpr && elseExpr.Span = thenExpr.Span

/// `for xs, ys | arms` (see the loops page): a call to the built-in "__for" with the iterables and then a lambda that
/// takes one element of each, in order. The loop walks the iterables in step and stops when the shortest ends.
module ForSyntax =
    let builtIn = "__for"

    /// What an iterable that is not a list, a sequence, a map or a set is read as: the prelude's function that gives its
    /// sequence through its `Iterable`. A `for` loop in a sequence's block walks its iterable written as a call to it.
    let toSeq = "__toSeq"

    /// What a sequence a `for` loop in a sequence's block walks, which is written `__toSeq(xs)`, is read as instead: the
    /// prelude's function that gives the sequence as it is, its steps' contexts and all.
    let sequenceItself = "__seqItself"

    /// The loop lambda's parameter for an element of the `position`th iterable (counted from 1). Like a shorthand
    /// lambda's parameter, no code can write it.
    let element (position: int) = if position = 1 then "__for_element" else $"__for_element{position}"

    /// A `for` loop's iterables and its loop lambda, with that lambda's parameters and body.
    let (|Call|_|) (kind: ExpressionKind) =
        match kind with
        | FunctionCall(name, _, args) when name = builtIn && args.Length >= 2 ->
            match List.last args with
            | { Kind = Lambda(parameters, body, _) } as loop -> Some (List.take (args.Length - 1) args, loop, parameters, body)
            | _ -> None
        | _ -> None

/// `while condition -> body` (see the loops page): a call to the built-in "__while" with the condition and the body,
/// both evaluated again each time round. The loop ends when the condition is false, and is unit.
module WhileSyntax =
    let builtIn = "__while"

    /// A `while` loop's condition and body.
    let (|Call|_|) (kind: ExpressionKind) =
        match kind with
        | FunctionCall(name, _, [ condition; body ]) when name = builtIn -> Some (condition, body)
        | _ -> None

/// What a record literal with a spread is made of, in the order written.
type SpreadPart =
    /// `...r`: every field of the record `r`.
    | SpreadRecord of Expression
    /// `name = value`.
    | SpreadField of Identifier * Expression

/// `rec name = value` (see the functions page): a local def whose value is wrapped in a call to the built-in "__rec",
/// which makes the name visible in its own value. A local def without it can't see itself.
module RecSyntax =
    let builtIn = "__rec"

    /// The value of a `rec` def.
    let (|Call|_|) (kind: ExpressionKind) =
        match kind with
        | FunctionCall(name, _, [ value ]) when name = builtIn -> Some value
        | _ -> None

/// `use C(T)(members)` (see the contexts page): the value `use` loads is a block that defines the instance under the
/// made-up name "__instance", typed `C(T)`, and gives it back, as `def name: C(T) = (members)` then `use name` would.
module UseInstanceSyntax =
    let name = "__instance"

    /// The context at its types, `C(T)`, and the members, of a `use C(T)(members)`.
    let (|Instance|_|) (kind: ExpressionKind) =
        match kind with
        | Block [ { Statement.Kind = DefStatement(false, defined, Some context, members) }
                  { Statement.Kind = ExprStatement { Expression.Kind = IdentifierExpr result } } ] when defined = name && result = name ->
            Some (context, members)
        | _ -> None

/// `{ (x: int): int -> body }` (see the functions page): a lambda's result annotation. The lambda's body is a block that
/// defines its value under the made-up name "__result", typed as written, and gives it back, as
/// `{ (x: int) -> def r: int = body; r }` would.
module LambdaResultSyntax =
    let name = "__result"

    /// The result type and the body as written, of a lambda body whose result is annotated.
    let (|Annotated|_|) (kind: ExpressionKind) =
        match kind with
        | Block [ { Statement.Kind = DefStatement(false, defined, Some resultType, body) }
                  { Statement.Kind = ExprStatement { Expression.Kind = IdentifierExpr result } } ] when defined = name && result = name ->
            Some (resultType, body)
        | _ -> None

/// `f()(args)` (see the functions page): a call on the result of a call is a block that defines that result under the
/// made-up name "__callee" and calls it, as `def g = f()` then `g(args)` would. `f()()()` nests: the callee of the
/// last call is the block for `f()()`.
module CallResultSyntax =
    let name = "__callee"

    /// The expression called, and the call of it by the made-up name, of `callee(args)` where `callee` is not a name.
    let (|Call|_|) (kind: ExpressionKind) =
        match kind with
        | Block [ { Statement.Kind = DefStatement(false, defined, None, callee) }
                  { Statement.Kind = ExprStatement ({ Expression.Kind = FunctionCall(called, None, _) } as call) } ] when
            defined = name && called = name ->
            Some (callee, call)
        | _ -> None

/// `record with f = v, ...` (see the records page): a call to the built-in "__with" with the record and a record of the
/// updated fields.
module WithSyntax =
    let builtIn = "__with"

    /// The record an update copies, and the record of its updated fields.
    let (|Call|_|) (kind: ExpressionKind) =
        match kind with
        | FunctionCall(name, _, [ record; updates ]) when name = builtIn -> Some (record, updates)
        | _ -> None

/// `(...a, f = v, ...b)` (see the records page): a call to the built-in "__spread" with a record of its parts, in the
/// order written: each spread record a positional field, each field written out a named one. A later part's field
/// replaces an earlier one's.
module SpreadSyntax =
    let builtIn = "__spread"

    /// The parts of a record literal with a spread.
    let (|Call|_|) (kind: ExpressionKind) =
        match kind with
        | FunctionCall(name, _, [ Kind (RecordExpr fields) ]) when name = builtIn ->
            fields
            |> List.map (function
                | PositionalField record -> SpreadRecord record
                | NamedField(field, value) -> SpreadField(field, value))
            |> Some
        | _ -> None

/// `a..b` (see the ranges page): a call to the built-in "__range" with its bounds, or with only its start for the
/// unbounded `a..`.
module RangeSyntax =
    let builtIn = "__range"

    /// The name the type checker gives a range's bound, the first (`0`) or the last (`1`), once it is inferred, to call
    /// the function the range is read as with it (see `InferCalls`).
    let bound (index: int) = $"__rangeBound{index}"

    /// A range's start, and its end when it has one.
    let (|Call|_|) (kind: ExpressionKind) =
        match kind with
        | FunctionCall(name, _, [ first; last ]) when name = builtIn -> Some (first, Some last)
        | FunctionCall(name, _, [ first ]) when name = builtIn -> Some (first, None)
        | _ -> None

/// `[k => v, ...]` (see the collections page): a call to the prelude's "__map" with a list of the entries, each a
/// tuple of its key and its value, in the order they are written.
module MapSyntax =
    let builtIn = "__map"

    /// A map literal's entries, each its key and its value.
    let (|Call|_|) (kind: ExpressionKind) =
        match kind with
        | FunctionCall(name, _, [ Kind (ListExpr entries) ]) when name = builtIn ->
            entries
            |> List.map (function
                | Kind (TupleExpr [ key; value ]) -> Some (key, value)
                | _ -> None)
            |> fun pairs -> if pairs |> List.forall Option.isSome then Some (pairs |> List.choose id) else None
        | _ -> None

/// A square-bracket literal of a type that says how to be built from one (see the collections page): `Array[1, 2]`,
/// or `[1, 2]` where an `Array(int)` is expected. It is a call to the type's attached `fromArray` with an array of the
/// items, `Array.fromArray(__arrayOf([1, 2]))`: the prelude's `__arrayOf` makes the array from a list of the items, in
/// which each spread, `...xs`, is a call to the built-in "__spreadItems", which takes a list or an array.
module CollectionSyntax =
    /// The function a type attaches to build a value from an array of a literal's items.
    let fromArray = "fromArray"
    let arrayOf = "__arrayOf"
    let spread = "__spreadItems"
    /// What a spread is read as, by its operand's type: the prelude's functions that make a list of an array's elements,
    /// of a list's, of a sequence's, and of anything else `Iterable`'s.
    let spreadArray = "__spreadArray"
    let spreadList = "__spreadList"
    let spreadSeq = "__spreadSeq"
    let spreadIterable = "__spreadIterable"

    /// The items of a literal as the parser writes it, each a run of elements or a spread: `[a, b]` is one run,
    /// `[a, ...xs]` is `[a] ++ xs`, whose list runs span the whole literal, and `[k => v]` is a call to `__map` with a
    /// list of the entries as tuples (see `MapSyntax`). None for any other expression.
    let segments (literal: Expression) : Choice<Expression list, Expression> list option =
        let segment (x: Expression) =
            match x.Kind with
            | ListExpr items when x.Span = literal.Span -> Choice1Of2 items
            | _ -> Choice2Of2 x
        let rec flatten (x: Expression) =
            match x.Kind with
            | BinaryOp("++", None, a, b) when x.Span = literal.Span -> segment a :: flatten b
            | _ -> [ segment x ]
        match literal.Kind with
        | ListExpr items -> Some [ Choice1Of2 items ]
        | BinaryOp("++", None, _, _) -> Some (flatten literal)
        | FunctionCall(name, _, [ Kind (ListExpr entries) ]) when name = MapSyntax.builtIn -> Some [ Choice1Of2 entries ]
        | _ -> None

    /// The list of a literal's items, written at `span`: each run of elements a list, and each spread a call to
    /// `__spreadItems`, joined with `++`.
    let listOf (span: SourceSpan) (segments: Choice<Expression list, Expression> list) : Expression =
        let part segment =
            match segment with
            | Choice1Of2 elements -> Expression.at span (ListExpr elements)
            | Choice2Of2 spread' -> Expression.from spread' (FunctionCall(spread, None, [ spread' ]))
        match segments with
        | [] -> Expression.at span (ListExpr [])
        | _ -> segments |> List.map part |> List.reduceBack (fun left right -> Expression.at span (BinaryOp("++", None, left, right)))

    /// The call that builds a `typeName` from the items `segments`, written at `span`, whose name is written at
    /// `nameSpan`.
    let build (typeName: Identifier) (nameSpan: SourceSpan option) (span: SourceSpan) (segments: Choice<Expression list, Expression> list) =
        let array = Expression.at span (FunctionCall(arrayOf, None, [ listOf span segments ]))
        FunctionCall($"{typeName}.{fromArray}", nameSpan, [ array ])

    /// `list`, the list of a literal's items `listOf` builds, with its runs and spreads replaced, in order, by
    /// `segments`, keeping its nodes.
    let withSegments (list: Expression) (segments: Choice<Expression list, Expression> list) : Expression =
        let mutable remaining = segments
        let next () =
            match remaining with
            | head :: rest ->
                remaining <- rest
                Some head
            | [] -> None
        let rec rebuild (x: Expression) =
            match x.Kind with
            | BinaryOp("++", None, a, b) ->
                let a = rebuild a
                { x with Kind = BinaryOp("++", None, a, rebuild b) }
            | ListExpr _ ->
                match next () with
                | Some (Choice1Of2 elements) -> { x with Kind = ListExpr elements }
                | _ -> x
            | FunctionCall(name, span, [ _ ]) when name = spread ->
                match next () with
                | Some (Choice2Of2 operand) -> { x with Kind = FunctionCall(name, span, [ operand ]) }
                | _ -> x
            | _ -> x
        rebuild list

    /// The runs and spreads of `list`, a list of a literal's items as `listOf` builds it.
    let rec listSegments (list: Expression) : Choice<Expression list, Expression> list =
        match list.Kind with
        | BinaryOp("++", None, a, b) -> listSegments a @ listSegments b
        | ListExpr items -> [ Choice1Of2 items ]
        | FunctionCall(name, _, [ operand ]) when name = spread -> [ Choice2Of2 operand ]
        | _ -> [ Choice2Of2 list ]

    /// Whether `item`, an item of a literal, is an entry, `k => v`, which the parser writes as a tuple spanning the key.
    let isEntry (item: Expression) =
        match item.Kind with
        | TupleExpr [ key; _ ] -> item.Span = key.Span
        | _ -> false

    /// The type a literal written `T[...]` builds, where it is written, and the list of its items.
    let (|Construct|_|) (kind: ExpressionKind) =
        match kind with
        | FunctionCall(name, nameSpan, [ Kind (FunctionCall(array, None, [ items ])) ]) when
            array = arrayOf && name.EndsWith($".{fromArray}") ->
            Some (name.Substring(0, name.Length - fromArray.Length - 1), nameSpan, items)
        | _ -> None

/// `not x`: a call to the built-in "not" with its operand.
module NotSyntax =
    let builtIn = "not"

/// `-x`: a call to the built-in "__negate" with its operand. On a built-in number it is the number's negation; on any
/// other type it calls that type's `negate`, a member of the prelude's `Sub` (see "Sub(a)" in the docs' Contexts
/// page).
module NegateSyntax =
    let builtIn = "__negate"

/// `dbg(x)` or `x \dbg` (see the io page): a call to the built-in "dbg", which prints a value and gives it back. A
/// name in scope called `dbg`, such as a def of the module's own, hides it.
module DbgSyntax =
    let builtIn = "dbg"

/// `assert(condition)`: built in, as `dbg` is, and called by that name where nothing else is named `assert`. Once the
/// condition is typed, the compiler writes each call as a call to `rewritten`, which carries what its message needs:
/// where it is and its source text, then, for each value the message shows, its label and the expression, which is the
/// same node as in the condition, so that the backend evaluates it once (see the compiler's `Asserts`).
module AssertSyntax =
    let builtIn = "assert"
    let rewritten = "__assert"

    /// The condition of an `assert` as written.
    let (|Call|_|) (kind: ExpressionKind) =
        match kind with
        | FunctionCall(name, _, [ condition ]) when name = builtIn -> Some condition
        | _ -> None

    /// The condition, the header of the message, and each value it shows with its label, of an `assert` the compiler
    /// has written as a call to `rewritten`.
    let (|Checked|_|) (kind: ExpressionKind) =
        let rec shown (args: Expression list) =
            match args with
            | { Kind = LiteralExpr(StringLit label) } :: value :: rest -> shown rest |> Option.map (fun others -> (label, value) :: others)
            | [] -> Some []
            | _ -> None
        match kind with
        | FunctionCall(name, _, condition :: { Kind = LiteralExpr(StringLit header) } :: values) when name = rewritten ->
            shown values |> Option.map (fun values -> condition, header, values)
        | _ -> None

/// `def (x, y) = value` or `def Money(pence) = m` (see the parser's `Destructuring`): the whole value is bound to a
/// temporary named from where the def is written, as `__tuple_3_1`, and each name the pattern binds to its part of it.
module DestructuringSyntax =
    let private tempName = System.Text.RegularExpressions.Regex(@"^__tuple_\d+_\d+$")

    /// The temporary of a destructuring def written at `line` and `column`.
    let temp (line: int64) (column: int64) = $"__tuple_{line}_{column}"

    /// Whether `name` is the temporary of a destructuring def.
    let isTemp (name: Identifier) = tempName.IsMatch name

/// `pattern int -> #Even | #Odd = { n -> ... }` (see the custom patterns page): a top-level def of the pattern's
/// function, typed as written, under a name made up from what its type says, which no code can write. `export pattern`
/// is an exported def. A pattern is identified by its tags, which are capitalised: a capitalised tag is always a
/// pattern's. A `_` among the tags makes the pattern partial, and `_` as an expression in its body is "no match".
module PatternSyntax =
    let keyword = "pattern"
    let private prefix = "__pattern_"

    /// `_`, written in a pattern's body: the input doesn't match.
    let noMatch = "_"

    /// The tag a partial pattern's function returns for `_`, which no code can write. A partial pattern with one tag and
    /// no payload returns a `bool` instead: `true` for its tag, `false` for `_`.
    let noMatchTag = "__noMatch"

    /// Whether `tag` is a pattern's: it is capitalised.
    let isPatternTag (tag: Identifier) = tag <> "" && Char.IsUpper tag[0]

    /// What a pattern's def name says of it, which is what a match that uses it needs to know: the tags it returns,
    /// those of them that carry a payload, whether it takes arguments, whether it is partial, and whether its function
    /// returns a `bool`, as a partial pattern with one tag and no payload does.
    type Shape =
        { Tags: Identifier list
          WithPayload: Identifier list
          TakesArguments: bool
          IsPartial: bool
          ReturnsBool: bool }

    let private flags = [ 'A', (fun shape -> shape.TakesArguments); 'P', (fun shape -> shape.IsPartial); 'B', (fun shape -> shape.ReturnsBool) ]

    /// What follows a tag that carries a payload in a pattern's def name. A tag is read by its length, and the next
    /// starts with a digit, so this can't be taken for either.
    let private payloadMark = 'p'

    /// The def name of the pattern of shape `shape`: its flags, then each tag after its length, so that the tags can be
    /// read back whatever they are made of, with `p` after each that carries a payload. `#Even | #Odd` is
    /// `__pattern__4Even3Odd`, `#Zero | _` is `__pattern_PB_4Zero`, and `#Hsl(float, float, float)` is
    /// `__pattern__3Hslp`.
    let defName (shape: Shape) =
        let written = flags |> List.filter (fun (_, isSet) -> isSet shape) |> List.map (fst >> string) |> String.concat ""
        let tag (name: Identifier) =
            let mark = if List.contains name shape.WithPayload then string payloadMark else ""
            $"{name.Length}{name}{mark}"
        prefix + written + "_" + (shape.Tags |> List.map tag |> String.concat "")

    /// The shape of the pattern whose def is named `name`, the last part of a qualified name, if it is a pattern's.
    let shapeOf (name: Identifier) : Shape option =
        let local = name.Substring(name.LastIndexOf '.' + 1)
        let separator = if local.StartsWith prefix then local.IndexOf('_', prefix.Length) else -1
        if separator < 0 then
            None
        else
            let written = local.Substring(prefix.Length, separator - prefix.Length)
            // Each tag, and whether it carries a payload.
            let rec read (at: int) (tags: (Identifier * bool) list) =
                if at = local.Length then
                    Some (List.rev tags)
                else
                    let digits = Seq.length (Seq.takeWhile Char.IsDigit (local.Substring at))
                    if digits = 0 then
                        None
                    else
                        let length = int (local.Substring(at, digits))
                        let start = at + digits
                        if start + length > local.Length then
                            None
                        else
                            let after = start + length
                            let hasPayload = after < local.Length && local[after] = payloadMark
                            read (if hasPayload then after + 1 else after) ((local.Substring(start, length), hasPayload) :: tags)
            read (separator + 1) []
            |> Option.filter (fun tags -> not tags.IsEmpty)
            |> Option.map (fun tags ->
                { Tags = tags |> List.map fst
                  WithPayload = tags |> List.filter snd |> List.map fst
                  TakesArguments = written.Contains 'A'
                  IsPartial = written.Contains 'P'
                  ReturnsBool = written.Contains 'B' })

    /// What a pattern's type says: the arguments it takes, if it takes any; its input; the tags it returns, each with
    /// its payload's type; and whether it is partial, written with `_` among its tags.
    type Signature =
        { Arguments: TypeExpr option
          Input: TypeExpr
          Results: (Identifier * TypeExpr option) list
          IsPartial: bool }

    /// The shape of a pattern with the signature `signature`.
    let shape (signature: Signature) : Shape =
        { Tags = signature.Results |> List.map fst
          WithPayload = signature.Results |> List.filter (snd >> Option.isSome) |> List.map fst
          TakesArguments = signature.Arguments.IsSome
          IsPartial = signature.IsPartial
          ReturnsBool = signature.IsPartial && (match signature.Results with [ _, None ] -> true | _ -> false) }

    /// The tags `results` writes, and whether they include `_`, if it is only tags and `_`.
    let private resultsOf (results: TypeExpr) =
        let items = match results.Kind with TypeUnion items -> items | _ -> [ results ]
        let tags = items |> List.choose (function { Kind = TypeTag(name, payload) } -> Some (name, payload) | _ -> None)
        let holes = items |> List.filter (function { Kind = TypeHole } -> true | _ -> false)
        if tags.IsEmpty || tags.Length + holes.Length <> items.Length || holes.Length > 1 then None
        else Some (tags, not holes.IsEmpty)

    /// What a pattern's type, `input -> tags` or `arguments -> input -> tags`, says.
    let (|Signature|_|) (typeExpr: TypeExpr) : Signature option =
        match typeExpr.Kind with
        | TypeFunc(arguments, { Kind = TypeFunc(input, results) }) when (resultsOf results).IsSome ->
            let tags, isPartial = (resultsOf results).Value
            Some { Arguments = Some arguments; Input = input; Results = tags; IsPartial = isPartial }
        | TypeFunc(input, results) ->
            resultsOf results
            |> Option.map (fun (tags, isPartial) -> { Arguments = None; Input = input; Results = tags; IsPartial = isPartial })
        | _ -> None

    /// A match that uses patterns is a block that binds each value the patterns run on that is not already a name, and
    /// what each pattern returns for it, then matches on those results as more values (see `NyxCompiler.CustomPatterns`).
    /// These are the names of those, made up from the match's node and the value's, or the result's, position.
    let subjectName (matchId: NodeId) (position: int) = $"__patternSubject_{matchId}_{position}"

    let private resultPrefix = "__patternResult_"
    let resultName (matchId: NodeId) (column: int) = $"{resultPrefix}{matchId}_{column}"

    /// Whether `name` is what a pattern returns, bound for a match (see `resultName`).
    let isResultName (name: Identifier) = name.StartsWith resultPrefix

    /// The pattern a def declares, at the top level or in a module block, as the parser writes it: its signature and its
    /// body. A function's block declares one as a def statement of the same name.
    let (|Declaration|_|) (definition: Definition) =
        match definition with
        | ValueDef(_, name, Some (Signature signature), body) when (shapeOf name).IsSome -> Some (signature, body)
        | _ -> None
/// The names the compiler makes up: the built-ins syntax is desugared into (`__for`, `__with`) and the temporaries
/// desugared code binds (`__tuple_3_1`) all start with "__".
module MadeUpNames =
    let isMadeUp (name: Identifier) = name.StartsWith "__"

/// An error deferred to run time (see the deferred errors page): a call to the built-in "__crash" with the message it
/// writes before it stops the program. The compiler makes it in place of code that failed to check; no code can write it.
module CrashSyntax =
    let builtIn = "__crash"

    /// The message a deferred error writes.
    let (|Call|_|) (kind: ExpressionKind) =
        match kind with
        | FunctionCall(name, _, [ Kind (LiteralExpr(StringLit message)) ]) when name = builtIn -> Some message
        | _ -> None

/// `mut x = e` (see the memory page) declares a cell that holds the value of `e`: a def of `x` whose value is a call to
/// the built-in "__mut" with `e`, spanning the declaration. Nothing else makes such a call: `mut` is not an expression.
module MutSyntax =
    let builtIn = "__mut"

    /// The value a cell is made with.
    let (|Call|_|) (kind: ExpressionKind) =
        match kind with
        | FunctionCall(name, _, [ value ]) when name = builtIn -> Some value
        | _ -> None

/// A foreign function (see the foreign functions page): `def name: T` at the top level with no value, which an
/// `@extern("module", "name")` before it makes an import of the WebAssembly module. The parser makes its value a call
/// to the built-in "__extern" with no arguments, spanning the declaration; once the attribute is applied, the call has
/// the import's module and name as its arguments. Nothing else makes such a call.
module ExternSyntax =
    let builtIn = "__extern"

    /// The value of a def written without one, before an `@extern` names its import.
    let (|Bodiless|_|) (kind: ExpressionKind) =
        match kind with
        | FunctionCall(name, _, []) when name = builtIn -> Some ()
        | _ -> None

    /// The module and name of the import a foreign function is.
    let (|Import|_|) (kind: ExpressionKind) =
        match kind with
        | FunctionCall(name, _, [ Kind (LiteralExpr(StringLit importModule)); Kind (LiteralExpr(StringLit importName)) ]) when name = builtIn ->
            Some (importModule, importName)
        | _ -> None

    /// `value`, the bodiless value of a def, as the value of a foreign function that imports `importName` from
    /// `importModule`. It is the same node, so the def's name is still declared by it.
    let import (importModule: string) (importName: string) (value: Expression) =
        let literal text = Expression.from value (LiteralExpr(StringLit text))
        { value with Kind = FunctionCall(builtIn, None, [ literal importModule; literal importName ]) }

/// The value of a def marked `@import`, a function another component exports, which the component built from the program
/// imports: a call of `builtIn` with the WIT interface's full name and the function's.
module ComponentImportSyntax =
    let builtIn = "__componentImport"

    /// The WIT interface and the function a component import names, and the names it gives the function's parameters,
    /// none if it gives none.
    let (|Import|_|) (kind: ExpressionKind) =
        match kind with
        | FunctionCall(name, _, Kind (LiteralExpr(StringLit witInterface)) :: Kind (LiteralExpr(StringLit functionName)) :: parameters) when name = builtIn ->
            let names = parameters |> List.choose (function Kind (LiteralExpr(StringLit parameter)) -> Some parameter | _ -> None)
            if names.Length = parameters.Length then Some (witInterface, functionName, names) else None
        | _ -> None

    /// `value`, the bodiless value of a def, as the value of a component import of `functionName` from `witInterface`,
    /// whose parameters are named `parameters`, or not named if it is empty. It is the same node, so the def's name is
    /// still declared by it.
    let import (witInterface: string) (functionName: string) (parameters: string list) (value: Expression) =
        let literal text = Expression.from value (LiteralExpr(StringLit text))
        { value with Kind = FunctionCall(builtIn, None, literal witInterface :: literal functionName :: List.map literal parameters) }

/// A linter rule as an attribute's argument names it, `default/unusedBindings`: the linter's name and the rule's,
/// written with no spaces around the `/`, which the parser reads as a division of the two names. `@allow` takes rules
/// separated by `,`, which are a tuple of them.
module LinterRuleSyntax =
    let separator = "/"

    /// The linter and the rule `expr` names, if it names one.
    let (|Rule|_|) (expr: Expression) =
        match expr.Kind with
        | BinaryOp(op, _, Kind (IdentifierExpr linter), Kind (IdentifierExpr rule)) when op = separator -> Some (linter, rule)
        | _ -> None

    /// The rules an attribute's argument names, each with the expression that names it, if it names only rules.
    let (|Rules|_|) (argument: Expression) =
        let items = match argument.Kind with TupleExpr items -> items | _ -> [ argument ]
        let rules = items |> List.choose (fun item -> match item with Rule(linter, rule) -> Some (linter, rule, item) | _ -> None)
        if rules.Length = items.Length then Some rules else None

/// `x := e` (see the memory page): a call to the built-in "__assign" with the cell and its new value. Its name span is
/// the `:=`.
module AssignSyntax =
    let builtIn = "__assign"

    /// What is assigned to, and the value assigned.
    let (|Call|_|) (kind: ExpressionKind) =
        match kind with
        | FunctionCall(name, _, [ target; value ]) when name = builtIn -> Some (target, value)
        | _ -> None

/// The cells of a `memory` block (see `MutSyntax`) as the backend is given them, once the type checker has found them:
/// calls to built-ins no code can write. A cell no lambda captures is a local, still made with `__mut` and changed
/// with `__assign`, and each read of it is a call to `read`. A cell a lambda captures is a box the lambdas share, made
/// with `box`, read with `unbox` and changed with `setBox`. A `for` loop's body is not such a lambda: it runs where it is.
module CellSyntax =
    let read = "__cell_read"
    let box = "__cell_box"
    let unbox = "__cell_unbox"
    let setBox = "__cell_set"

/// How a workflow keyword takes the expression after it, as `@customKeywordType(#kind)` on its member says (see the
/// workflows page). `k! e` is `k(e, { rest of the block })` for `Bind`, `k({ e }, { rest of the block })` for `BindF`,
/// and `k({ e })` for `Delay`, which rewrites nothing else and needs no builder. A lambda with a `Yield` keyword in it
/// is a lazy sequence, `k! e` with the rest of its block `seq.cons(e, { rest of the block })`, and calls no member.
[<RequireQualifiedAccess>]
type KeywordKind =
    | Bind
    | BindF
    | Delay
    | Yield

/// The workflow keywords the language reserves (see the workflows page): `try x` and `yield x`, `WorkflowBindExpr`s of
/// `tryKeyword` and `yieldKeyword`, and `return x`, a `WorkflowReturnExpr`. In a builder's block `try` and `return` call
/// the builder's member of the same name; `yield` is of kind `Yield`.
module WorkflowSyntax =
    /// The kinds `@customKeywordType` takes, each by the tag that names it: `@customKeywordType(#delay)`.
    let keywordKinds = [ "bind", KeywordKind.Bind; "bindF", KeywordKind.BindF; "delay", KeywordKind.Delay; "yield", KeywordKind.Yield ]

    let tryKeyword = "try"
    let yieldKeyword = "yield"
    let returnKeyword = "return"
    /// The keywords written before their value without `!`, as `try x` and `yield x` are.
    let bareKeywords = [ tryKeyword; yieldKeyword ]
    /// The member that is the value of a builder's block that ends without `return`.
    let zeroMember = "zero"
    /// The member that runs a `for` loop whose body uses a keyword: `for(items, { item -> ... }, { ... })`.
    let forMember = "for"
    /// The tag a `for` loop's body gives back when it is done with an item and the loop goes on to the next, as it does
    /// where the body ends: the body's other results, a failure or a `return`'s value, end the loop (see `forMember`).
    let nextItemTag = "__nextItem"

module TypeRecordField =
    /// The attributes of a record type's fields: each field, with the attributes written before it (see
    /// `TypeFieldAttribute`) as their names, arguments and spans. The parser reads an attribute only before a field.
    let withAttributes (fields: TypeRecordField list) : ((Identifier * Expression option * SourceSpan) list * TypeRecordField) list =
        let rec loop pending fields =
            match fields with
            | [] -> []
            | TypeFieldAttribute(name, argument, span) :: rest -> loop ((name, argument, span) :: pending) rest
            | field :: rest -> (List.rev pending, field) :: loop [] rest
        loop [] fields

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
        | TypeKeyword
        | TypeOf _ -> []
        | TypeNamedArgument(_, inner)
        | TypeAssociated(inner, _)
        | TypeArgument inner -> [ inner ]
        | TypeTuple items
        | TypeUnion items
        | TypeIntersection items
        | TypeContext items
        | TypeApply(_, items) -> items
        | TypeRecord fields
        | TypeOpenRecord(fields, _) ->
            fields |> List.choose (function TypeField(_, _, typeOpt, _) -> typeOpt | TypeFieldAttribute _ -> None)
        | TypeFunc(arg, ret)
        | TypeProduct(arg, ret)
        | TypeQuotient(arg, ret) -> [ arg; ret ]
        | TypeTag(_, payload) -> Option.toList payload
        | TypeWithContext(contexts, inner) -> contexts @ [ inner ]
        | TypeConstraint(_, inner, _)
        | TypeWhere(inner, _) -> [ inner ]

    let private mapField (f: TypeExpr -> TypeExpr) (field: TypeRecordField) =
        match field with
        | TypeField(name, isMutable, typeOpt, defaultOpt) -> TypeField(name, isMutable, Option.map f typeOpt, defaultOpt)
        | TypeFieldAttribute _ -> field
    /// `typeExpr` with `f` applied to each type expression directly inside it: those `children` gives. One with none,
    /// a `typeof` among them, is returned as it is.
    let mapChildren (f: TypeExpr -> TypeExpr) (typeExpr: TypeExpr) : TypeExpr =
        let kind =
            match typeExpr.Kind with
            | TypeName _
            | TypeVar _
            | TypeHole
            | TypeUnit
            | TypeKeyword
            | TypeOf _ -> typeExpr.Kind
            | TypeNamedArgument(name, inner) -> TypeNamedArgument(name, f inner)
            | TypeAssociated(context, name) -> TypeAssociated(f context, name)
            | TypeArgument inner -> TypeArgument(f inner)
            | TypeTuple items -> TypeTuple(List.map f items)
            | TypeUnion items -> TypeUnion(List.map f items)
            | TypeIntersection items -> TypeIntersection(List.map f items)
            | TypeContext items -> TypeContext(List.map f items)
            | TypeApply(name, items) -> TypeApply(name, List.map f items)
            | TypeRecord fields -> TypeRecord(List.map (mapField f) fields)
            | TypeOpenRecord(fields, row) -> TypeOpenRecord(List.map (mapField f) fields, row)
            | TypeFunc(arg, ret) -> TypeFunc(f arg, f ret)
            | TypeProduct(left, right) -> TypeProduct(f left, f right)
            | TypeQuotient(left, right) -> TypeQuotient(f left, f right)
            | TypeTag(name, payload) -> TypeTag(name, Option.map f payload)
            | TypeWithContext(contexts, inner) -> TypeWithContext(List.map f contexts, f inner)
            | TypeConstraint(name, inner, predicate) -> TypeConstraint(name, f inner, predicate)
            | TypeWhere(inner, predicate) -> TypeWhere(f inner, predicate)
        { typeExpr with Kind = kind }

    /// `typeExpr` and every type expression inside it, in order.
    let rec within (typeExpr: TypeExpr) : TypeExpr list =
        typeExpr :: (children typeExpr |> List.collect within)

    /// `typeExpr` and every type expression inside it, in order, each with whether a function type written in its
    /// place needs brackets: everywhere but the result of a function, the type after a list of contexts, and
    /// `typeExpr` itself when `takesFunction`.
    let rec withBracketing (takesFunction: bool) (typeExpr: TypeExpr) : (TypeExpr * bool) list =
        let inner =
            match typeExpr.Kind with
            | TypeFunc(input, result) -> withBracketing false input @ withBracketing true result
            | TypeWithContext(contexts, body) -> (contexts |> List.collect (withBracketing false)) @ withBracketing takesFunction body
            | _ -> children typeExpr |> List.collect (withBracketing false)
        (typeExpr, not takesFunction) :: inner

    /// The names of the type variables `typeExpr` writes, in the order they first appear.
    let variablesOf (typeExpr: TypeExpr) =
        within typeExpr |> List.choose (fun part -> match part.Kind with TypeVar name -> Some name | _ -> None) |> List.distinct

    /// Each `_` written in `typeExpr`, in order.
    let rec holes (typeExpr: TypeExpr) : TypeExpr list =
        match typeExpr.Kind with
        | TypeHole -> [ typeExpr ]
        | _ -> children typeExpr |> List.collect holes

/// Passing a cell to a function (see the memory page). `mut T` is the type of a cell that holds a `T`, a `TypeApply` of
/// `typeName`. `{ mut cell -> ... }` takes one: a parameter annotated `mut _`, or `mut T` for `mut cell: T`, with no
/// source of its own, so that it is written back as it was. `f(mut x)` passes the cell `x`: a call to the built-in
/// "__mut_arg" with the cell's name, spanning both.
module CellPassingSyntax =
    let typeName = "mut"
    let argument = "__mut_arg"

    /// The annotation of a parameter written `mut name`, or `mut name: T` when `held` is `T`.
    let parameterType (held: TypeExpr option) : TypeExpr =
        TypeExpr.synthetic (TypeApply(typeName, [ held |> Option.defaultWith (fun () -> TypeExpr.synthetic TypeHole) ]))

    /// What a parameter's annotation says when it is written `mut name` (`None`) or `mut name: T` (`Some T`).
    let (|CellParameter|_|) (typeOpt: TypeExpr option) : TypeExpr option option =
        match typeOpt with
        | Some { Kind = TypeApply(name, [ held ]); Span = span } when name = typeName && SourceSpan.isNone span ->
            match held.Kind with
            | TypeHole when SourceSpan.isNone held.Span -> Some None
            | _ -> Some (Some held)
        | _ -> None

    /// The cell an argument passes.
    let (|Argument|_|) (kind: ExpressionKind) =
        match kind with
        | FunctionCall(name, _, [ cell ]) when name = argument -> Some cell
        | _ -> None

module Pattern =
    /// A pattern written at `span`.
    let at (span: SourceSpan) (kind: PatternKind) : Pattern =
        { Pattern.Kind = kind; Span = span; Id = NodeId.next () }

    /// The expressions written in `pattern` and the patterns inside it: each guard's predicate and each range's ends.
    let rec expressions (pattern: Pattern) : Expression list =
        match pattern.Kind with
        | GuardPattern predicate -> [ predicate ]
        | RangePattern(low, high) -> [ low; high ]
        | TuplePattern(patterns, _)
        | AndPattern patterns
        | RecordPattern(_, patterns) -> patterns |> List.collect expressions
        | RecordMemberPattern(members, _) -> members |> List.collect (snd >> expressions)
        | ListPattern(patterns, rest) -> (patterns @ Option.toList rest) |> List.collect expressions
        | ListSplatMiddle(before, after) -> (before @ after) |> List.collect expressions
        | TagPattern(_, payload) -> payload |> Option.toList |> List.collect expressions
        | ArgumentTagPattern(_, arguments, payload) -> arguments :: (payload |> Option.toList |> List.collect expressions)
        | LiteralPattern _
        | IdentifierPattern _
        | WildcardPattern
        | UnitPattern
        | ElsePattern -> []

    /// `pattern` and every pattern inside it, in order.
    let rec within (pattern: Pattern) : Pattern list =
        let inner =
            match pattern.Kind with
            | TuplePattern(patterns, _)
            | AndPattern patterns
            | RecordPattern(_, patterns) -> patterns
            | RecordMemberPattern(members, _) -> members |> List.map snd
            | ListPattern(patterns, rest) -> patterns @ Option.toList rest
            | ListSplatMiddle(before, after) -> before @ after
            | TagPattern(_, payload)
            | ArgumentTagPattern(_, _, payload) -> Option.toList payload
            | GuardPattern _
            | RangePattern _
            | LiteralPattern _
            | IdentifierPattern _
            | WildcardPattern
            | UnitPattern
            | ElsePattern -> []
        pattern :: (inner |> List.collect within)

    /// The names `pattern` binds, in the order they are written: its identifier patterns and its named list rests.
    let rec boundNames (pattern: Pattern) : Identifier list =
        match pattern.Kind with
        | IdentifierPattern name -> [ name ]
        | TuplePattern(patterns, _)
        | AndPattern patterns
        | RecordPattern(_, patterns) -> patterns |> List.collect boundNames
        | RecordMemberPattern(members, _) -> members |> List.collect (snd >> boundNames)
        | ListPattern(patterns, rest) -> (patterns @ Option.toList rest) |> List.collect boundNames
        | ListSplatMiddle(before, after) -> (before @ after) |> List.collect boundNames
        | TagPattern(_, payload)
        | ArgumentTagPattern(_, _, payload) -> payload |> Option.toList |> List.collect boundNames
        | GuardPattern _
        | RangePattern _
        | LiteralPattern _
        | WildcardPattern
        | UnitPattern
        | ElsePattern -> []

    /// The field of a record with the fields `fields` that the `index`th element of a tuple pattern matches. A name
    /// alone is shorthand for `name = name` where the record has a field of that name, so `(name, age)` takes apart
    /// `(name: string, age: int)`, and `#some(x, y)` the payload of `#some(x: string, y: string)`. Any other element
    /// matches the field at its position, as in a tuple. A pattern that ends in `...` reads its elements the same way,
    /// so `(name, ...)` takes the field `name` and leaves the others out, and `(x, ...)` a tuple's first element.
    let tupleElementField (fields: Set<string>) (index: int) (element: Pattern) : string =
        match element.Kind with
        | IdentifierPattern name when fields.Contains name -> name
        | _ -> string index

    /// What a constructor pattern, `T(p1, p2)` at `constructor`, matches the representation of `T` against. One
    /// pattern matches the whole of it, `Box(r)`; brackets that end in `...` or name fields are one pattern too, which
    /// reads a record's fields, `Box(v, ...)` and `Box(v = x)` (see `RecordPattern`). Several patterns are a tuple
    /// pattern of them, which reads a record's fields as one does (see `tupleElementField`): `Pair(a, b)`.
    let constructorPayload (constructor: Pattern) (patterns: Pattern list) : Pattern =
        match patterns with
        | [ single ] -> single
        | _ -> at constructor.Span (TuplePattern(patterns, false))

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
            | TuplePattern(patterns, hasRest) -> TuplePattern(List.map recur patterns, hasRest)
            | AndPattern patterns -> AndPattern(List.map recur patterns)
            | RecordPattern(name, patterns) -> RecordPattern(name, List.map recur patterns)
            | RecordMemberPattern(members, hasRest) ->
                RecordMemberPattern(members |> List.map (fun (name, p) -> name, recur p), hasRest)
            | ListPattern(patterns, rest) -> ListPattern(List.map recur patterns, Option.map recur rest)
            | ListSplatMiddle(before, after) -> ListSplatMiddle(List.map recur before, List.map recur after)
            | TagPattern(name, payload) -> TagPattern(name, Option.map recur payload)
            | ArgumentTagPattern(name, arguments, payload) -> ArgumentTagPattern(name, f arguments, Option.map recur payload)
            | LiteralPattern _
            | IdentifierPattern _
            | WildcardPattern
            | UnitPattern
            | ElsePattern -> pattern.Kind
        { pattern with Kind = kind }

    /// `pattern` with `f` applied to the type name of each pattern in it that takes a nominal value apart, `UserId(n)`.
    let rec mapTypeNames (f: Identifier -> Identifier) (pattern: Pattern) : Pattern =
        let recur = mapTypeNames f
        let kind =
            match pattern.Kind with
            | RecordPattern(name, patterns) -> RecordPattern(f name, List.map recur patterns)
            | TuplePattern(patterns, hasRest) -> TuplePattern(List.map recur patterns, hasRest)
            | AndPattern patterns -> AndPattern(List.map recur patterns)
            | RecordMemberPattern(members, hasRest) ->
                RecordMemberPattern(members |> List.map (fun (name, p) -> name, recur p), hasRest)
            | ListPattern(patterns, rest) -> ListPattern(List.map recur patterns, Option.map recur rest)
            | ListSplatMiddle(before, after) -> ListSplatMiddle(List.map recur before, List.map recur after)
            | TagPattern(name, payload) -> TagPattern(name, Option.map recur payload)
            | ArgumentTagPattern(name, arguments, payload) -> ArgumentTagPattern(name, arguments, Option.map recur payload)
            | GuardPattern _
            | RangePattern _
            | LiteralPattern _
            | IdentifierPattern _
            | WildcardPattern
            | UnitPattern
            | ElsePattern -> pattern.Kind
        { pattern with Kind = kind }

    /// The names `pattern` binds, as `boundNames` gives them, each with the span of the pattern that binds it.
    let boundNamesWithSpans (pattern: Pattern) : (Identifier * SourceSpan) list =
        within pattern
        |> List.choose (fun inner ->
            match inner.Kind with
            | IdentifierPattern name -> Some(name, inner.Span)
            | _ -> None)

module MatchHead =
    /// The head of the match `matchExpr` on `scrutinees`: what is reported for the match as a whole, rather than
    /// for one arm. That depends on how the match was written:
    /// - `match a | ...` is from `match` to the end of the last value matched on, so `match a`, or
    ///   `match getData() \mapSome { ... }`.
    /// - `{ | pattern -> ... }`, and a `for` loop's arms, match on values made up over the whole match, arms and
    ///   all. Their head is the first character, the `{` or the first `|`.
    /// - A lambda's pattern parameters (`{ #some(n) -> n }`) match on values made up where the patterns are
    ///   written, so they are the head, from the first to the last.
    /// - A match made up over its value (`def #some(x) = value`, `value except | ...`), or written after it
    ///   (`value \match | ...`), has the value as its head.
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
        | UseStatement expr -> Some expr
        | ImportStatement _
        | TypeDefStatement _
        | AttributeStatement _ -> None

[<AutoOpen>]
module PipeCall =
    /// The arguments of the call that the pipe `value \f(args)` stands for. The piped value goes in front of the
    /// written arguments rather than beside them as one more value: `x \f(a, b)` is `f(x, a, b)`, and
    /// `x \f(a, y = 1)` is `f(x, a, y = 1)`. A trailing lambda is the last positional argument. Each positional
    /// argument is one of `args`, so a tuple among them is passed as one: `x \f((a, b))` is `f(x, (a, b))`.
    let pipeCallArguments (value: Expression) (args: Expression list) : Expression list =
        match args with
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

    /// The attribute that makes a file's module declaration one fragment of a module split across files, written on
    /// the declaration's line: `@partial module utils`.
    let partialAttribute = "partial"

    /// Whether `attribute` is `@partial` and `next`, the item after it, a module declaration.
    let isPartialDeclaration (attribute: TopLevelItem) (next: TopLevelItem) =
        match attribute.Kind, next.Kind with
        | Attribute(name, None), ModuleDecl _ -> name = partialAttribute
        | _ -> false

/// Units of measure: `dimension Time`, `@storageUnit unit ns: Time` and `unit ms: Time = 1_000us`. The words are not
/// reserved: each begins a declaration only at the start of a top-level item, with a name after it on its line, so code
/// that uses them as names keeps working.
module UnitSyntax =
    /// The word that begins a dimension's declaration.
    let dimensionKeyword = "dimension"

    /// The word that begins a unit's declaration.
    let unitKeyword = "unit"

    /// The attribute that marks a dimension's default display unit, the unit its values are written in when no other
    /// unit is chosen, written on the unit's line: `@baseUnit unit s: Time = 1_000ms`.
    let baseUnitAttribute = "baseUnit"

    /// The attribute that sets a dimension's default storage unit by hand, the unit its values are stored as a number
    /// of, written on the unit's line: `@storageUnit unit ns: Time`.
    let storageUnitAttribute = "storageUnit"

    /// Whether `item` is an attribute that marks a unit, `@baseUnit` or `@storageUnit`.
    let isUnitAttribute (item: TopLevelItem) =
        match item.Kind with
        | Attribute(name, None) -> name = baseUnitAttribute || name = storageUnitAttribute
        | _ -> false

    /// Whether `attribute` marks a unit and `next`, the item after it, is the unit's declaration or another attribute
    /// that marks it, as in `@storageUnit @baseUnit unit ns: Time`.
    let isUnitAttributeDeclaration (attribute: TopLevelItem) (next: TopLevelItem) =
        isUnitAttribute attribute
        && match next.Kind with
           | UnitDecl _ -> true
           | _ -> isUnitAttribute next

    /// Each unit's declaration among `items`, with the names of the attributes right before it that mark it.
    let unitsWithAttributes (items: TopLevelItem list) : (TopLevelItem * string list) list =
        items
        |> List.fold
            (fun (marks, units) item ->
                match item.Kind with
                | Attribute(name, None) when isUnitAttribute item -> marks @ [ name ], units
                | UnitDecl _ -> [], (item, marks) :: units
                | _ -> [], units)
            ([], [])
        |> snd
        |> List.rev

    /// The operators that join dimensions in a derived dimension, `Length / Time` and `Time * Time` (`TypeProduct`,
    /// `TypeQuotient`), as they are written.
    let times = "*"

    let over = "/"

    /// A number with a unit as it is written: `500ms`.
    let written (number: WrittenNumber) (unitName: string) = WrittenNumber.text number + unitName

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
    /// What a named field is written with declares the field's name: its value in a record or a call's named
    /// arguments, or among a `with`'s updates; its type in a record type; its pattern in a record pattern by member.
    /// The record's own list of names keeps one of each, so this is how a name written twice is found (see
    /// `NyxCompiler.DuplicateFields`).
    | NamedFieldValue
    /// A `dimension` or `unit` line declares the dimension's or the unit's name, by the line's item.
    | MeasureDeclaration

/// The names nodes declare, each with the span it is written at. Declarations have no nodes of their own yet
/// (#96), so their spans are kept here, keyed by the node they belong to.
type DeclaredNames = Map<NodeId * Declarer, (Identifier * SourceSpan) list>
