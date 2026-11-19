/// The recursive-descent parser checked against the FParsec parser, one part of the grammar at a time, until it reads
/// whole files (see the Work in progress page "The parser").
module Nanyx.Parser.DescentCheck

open Parser.Program
open Parser.Program.DescentCursor

/// How the new parser read one thing the FParsec parser read.
type Outcome =
    /// The same tree, with the same spans.
    | Same
    /// It gave up, for this reason, and the FParsec parser's reading stands.
    | GaveUp of reason: string
    /// A different tree: each one written out (see `SyntaxDump.dump`).
    | Differs of fparsec: string * descent: string

/// Which of the new parser's entry points reads a type the FParsec parser read.
type TypeEntry =
    /// A def's annotation, `typeSignature`.
    | Annotation
    /// A type definition's body, `typeExpr`.
    | TypeBody

/// The types in the FParsec parser's tree of `parsed` that a part of the grammar reads whole: each def's annotation and
/// each type definition's body, except one with a `where` constraint, whose constraint is an expression.
let typeRoots (parsed: SourceModule) : (TypeEntry * TypeExpr) list =
    let roots = ResizeArray()
    // A lambda's result type, `{ (x: int): int -> ... }`, is its def's annotation too, but written after the `=`: an
    // annotation is the def's own when no `=` comes between where the def starts and it.
    let add (defStart: int) (annotation: TypeExpr option) (body: TypeExpr option) =
        match annotation, body with
        | Some annotation, _ when annotation.Span.Start > defStart && not (parsed.Source.Substring(defStart, annotation.Span.Start - defStart).Contains '=') ->
            roots.Add((Annotation, annotation))
        | Some _, _ -> ()
        | _, Some { Kind = TypeWhere _ } -> ()
        | _, Some body -> roots.Add((TypeBody, body))
        | None, None -> ()
    let rec walk (value: obj) =
        match value with
        | :? TopLevelItem as item ->
            match item.Kind with
            | Def(ValueDef(_, _, annotation, _)) -> add item.Span.Start annotation None
            | Def(TypeDef(_, _, _, body)) -> add item.Span.Start None (Some body)
            | _ -> ()
        | :? Statement as statement ->
            match statement.Kind with
            | DefStatement(_, _, annotation, _) -> add statement.Span.Start annotation None
            | TypeDefStatement(_, _, _, body) -> add statement.Span.Start None (Some body)
            | _ -> ()
        | _ -> ()
        for child in BlockStructure.children value do
            walk child
    walk parsed.Items
    List.ofSeq roots

/// How the new parser reads each of `roots` in `parsed`, from the file at `file`: from the code token where the FParsec
/// parser's node, `node root`, starts, with `read`, written out and compared with that node written out.
let private checkEach (file: string) (parsed: SourceModule) (node: 'root -> 'node) (start: 'node -> int) (read: Cursor -> 'root -> 'node) (roots: 'root list) : Outcome list =
    match (try Ok(cursorOf file parsed.Source) with GiveUp reason -> Error reason) with
    | Error reason -> roots |> List.map (fun _ -> GaveUp reason)
    | Ok cursor ->
        roots
        |> List.map (fun root ->
            if not (seek cursor (start (node root))) then GaveUp "no token starts where the FParsec parser's node does"
            else
                try
                    let expected = SyntaxDump.dump (node root)
                    let actual = SyntaxDump.dump (read cursor root)
                    if expected = actual then Same else Differs(expected, actual)
                with
                | GiveUp reason -> GaveUp reason
                | NoMatch -> GaveUp "no reading matched")

/// How the new parser reads each of `roots`, the types `typeRoots` finds in `parsed`, from the file at `file`.
let checkTypes (file: string) (parsed: SourceModule) (roots: (TypeEntry * TypeExpr) list) : Outcome list =
    roots
    |> checkEach file parsed snd (fun (root: TypeExpr) -> root.Span.Start) (fun cursor (entry, _) ->
        match entry with
        | Annotation -> DescentTypes.typeSignature cursor
        | TypeBody -> DescentTypes.typeExpr cursor)

/// The patterns in the FParsec parser's tree of `parsed` that a part of the grammar reads whole: each pattern of each
/// arm of a match or an `except`, with the arms the parser makes up for an `if ... is`, a lambda's pattern parameter and
/// a destructuring def. A pattern it makes up is left out: one with no span, and the `_` of the arm an `if ... is`
/// with an `else` makes, which is spanned at what follows the `else`.
let patternRoots (parsed: SourceModule) : Pattern list =
    let roots = ResizeArray()
    let written (pattern: Pattern) =
        not (SourceSpan.isNone pattern.Span)
        && (pattern.Kind <> WildcardPattern || parsed.Source.Substring(pattern.Span.Start, pattern.Span.End - pattern.Span.Start) = "_")
    let rec walk (value: obj) =
        match value with
        | :? Expression as expr ->
            match expr.Kind with
            | Match(_, arms)
            | Except(_, arms) ->
                for patterns, _ in arms do
                    roots.AddRange(patterns |> List.filter written)
            | _ -> ()
        | _ -> ()
        for child in BlockStructure.children value do
            walk child
    walk parsed.Items
    List.ofSeq roots

/// How the new parser reads each of `roots`, the patterns `patternRoots` finds in `parsed`, from the file at `file`.
let checkPatterns (file: string) (parsed: SourceModule) (roots: Pattern list) : Outcome list =
    checkEach file parsed id (fun (root: Pattern) -> root.Span.Start) (fun cursor _ -> DescentControl.fill (); DescentPatterns.pattern cursor) roots

/// Where an expression `expressionRoots` finds was read: what reads it.
type ExpressionEntry =
    /// A def's value on its line, `DescentExpressions.defValue`.
    | DefValue
    /// A def's value written as an indented block on the lines after it, `DescentBlocks.indentedBlock`.
    | DefBlock
    /// A statement, or a script's top-level expression, `expression Inline`.
    | StatementValue

/// Whether `name` is one the parser makes up for a def (see `CallResultSyntax`, `LambdaResultSyntax`,
/// `UseInstanceSyntax` and `DestructuringSyntax`).
let private isMadeUpName (name: Identifier) =
    name = CallResultSyntax.name || name = LambdaResultSyntax.name || name = UseInstanceSyntax.name || DestructuringSyntax.isTemp name

/// Whether `value` is one the parser makes up for a def: the made-up name, or a field of it, a destructuring def's.
let rec private isMadeUp (value: Expression) =
    match value.Kind with
    | IdentifierExpr name -> isMadeUpName name
    | MemberAccess(inner, _, None) -> isMadeUp inner
    | FunctionCall(name, None, _) -> isMadeUpName name || name = RecSyntax.builtIn || name = MutSyntax.builtIn
    | _ -> false

/// The expressions in the FParsec parser's tree of `parsed` that a part of the grammar reads whole: each def's value
/// written on its line, each statement's expression and each script's top-level expression. A value the parser makes up
/// is left out (a declaration's, which starts where its def does, or a destructuring def's), and so is a block other
/// than a def's value on the lines after it or the block a call on a call's result is.
let expressionRoots (parsed: SourceModule) : (ExpressionEntry * Expression) list =
    let roots = ResizeArray()
    let add entry (owner: SourceSpan) (value: Expression) =
        match value.Kind with
        // A call on a call's result is a block the parser makes up (see `CallResultSyntax`), which reads as a call.
        | Block({ Kind = DefStatement(_, name, _, _) } :: _) when name = CallResultSyntax.name -> roots.Add((entry, value))
        | Block({ Kind = DefStatement(_, name, _, _) } :: _) when isMadeUpName name -> ()
        | Block _ when entry = DefValue && not (SourceSpan.isNone value.Span) && value.Span.Start > owner.Start -> roots.Add((DefBlock, value))
        | Block _ -> ()
        | _ when SourceSpan.isNone value.Span || isMadeUp value || value.Span.Start <= owner.Start -> ()
        | Match _ when entry = DefValue -> ()
        | _ -> roots.Add((entry, value))
    let rec walk (value: obj) =
        match value with
        | :? TopLevelItem as item ->
            match item.Kind with
            | Def(ValueDef(_, name, _, value)) when not (isMadeUpName name) -> add DefValue item.Span value
            | Expr value -> add StatementValue SourceSpan.none value
            | _ -> ()
        | :? Statement as statement ->
            match statement.Kind with
            | DefStatement(_, name, _, value) when not (isMadeUpName name) -> add DefValue statement.Span value
            | ExprStatement value -> add StatementValue SourceSpan.none value
            | _ -> ()
        | _ -> ()
        for child in BlockStructure.children value do
            walk child
    walk parsed.Items
    List.ofSeq roots

/// How the new parser reads each of `roots`, the expressions `expressionRoots` finds in `parsed`, from the file at
/// `file`.
let checkExpressions (file: string) (parsed: SourceModule) (roots: (ExpressionEntry * Expression) list) : Outcome list =
    roots
    |> checkEach file parsed snd (fun (root: Expression) -> root.Span.Start) (fun cursor (entry, _) ->
        match entry with
        | DefValue -> DescentControl.defValueAfterEquals cursor
        | DefBlock ->
            match cursor.At -1 with
            | Layout.Indent _ -> DescentControl.defValueAfterEquals cursor
            | _ -> raise (GiveUp "a def's block the layout pass doesn't open")
        | StatementValue -> DescentControl.statementValue cursor)
