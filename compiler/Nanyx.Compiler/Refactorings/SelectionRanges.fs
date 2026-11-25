/// Expand and shrink selection: the chain of syntax nodes around a position, smallest first, as an editor grows the
/// selection through them. It needs only a parse. Positions are character offsets into the source as the parser saw
/// it, with line endings normalised to `\n`, as for `Outline`.
module NyxCompiler.Refactorings.SelectionRanges

open Parser.Program
open NyxCompiler

/// The span from the start of `first` to the end of `last`, when both were written.
let private across (first: SourceSpan) (last: SourceSpan) =
    if SourceSpan.isNone first || SourceSpan.isNone last then [] else [ { first with End = last.End } ]

/// The span from the first of `spans` to the last, the written ones of them, if there are any.
let private group (spans: SourceSpan list) =
    match spans |> List.filter (SourceSpan.isNone >> not) with
    | [] -> []
    | written -> across (written |> List.minBy (fun span -> span.Start)) (written |> List.maxBy (fun span -> span.End))

/// Whether `span` holds `offset`, at either end or in between. A span the compiler made up holds every offset: what
/// it was made from may still have been written there.
let private holds (offset: int) (span: SourceSpan) =
    SourceSpan.isNone span || (span.Start <= offset && offset <= span.End)

/// The spans of `typeExpr` and every part of it.
let private inType (typeExpr: TypeExpr) : SourceSpan list =
    TypeExpr.within typeExpr |> List.map (fun part -> part.Span)

/// The spans written in `expr` around `offset`, from the expression as a whole down: each node, and the groups of
/// nodes that are not nodes of their own, such as a call's arguments, a match arm, a record's named field or `map(f)`
/// in `xs \map(f)`. Only the parts that hold `offset` are walked into.
let rec private inExpression (declared: DeclaredNames) (offset: int) (expr: Expression) : SourceSpan list =
    if not (holds offset expr.Span) then []
    else
        let names (holder: NodeId) declarer =
            declared |> Map.tryFind (holder, declarer) |> Option.defaultValue [] |> List.map snd
        // `name = value`, a named field of a record or a named argument, runs from its name to the end of its value.
        let field = names expr.Id NamedFieldValue |> List.collect (fun name -> name :: across name expr.Span)
        let own =
            match expr.Kind with
            | Lambda(parameters, _, _) ->
                let parameterNames = names expr.Id LambdaParameters
                let annotations = parameters |> List.choose snd
                parameterNames @ (annotations |> List.collect inType) @ group (parameterNames @ (annotations |> List.map (fun t -> t.Span)))
            | FunctionCall(_, nameSpan, args) -> Option.toList nameSpan @ group (args |> List.map (fun arg -> arg.Span))
            | Pipe(_, _, nameSpan, args) ->
                // `map(f)` in `xs \map(f)`: the call the pipe makes, from its name to the end of the pipe.
                let call = nameSpan |> Option.toList |> List.collect (fun name -> across name expr.Span)
                Option.toList nameSpan @ call @ group (args |> List.map (fun arg -> arg.Span))
            | ContextMemberCall(context, _, args) -> inType context @ group (args |> List.map (fun arg -> arg.Span))
            | ContextMemberAccess(context, _) -> inType context
            | TupleExpr items
            | ListExpr items -> group (items |> List.map (fun item -> item.Span))
            | RecordExpr fields ->
                fields
                |> List.collect (function
                    | NamedField(_, value) -> (names value.Id NamedFieldValue |> List.collect (fun name -> across name value.Span))
                    | PositionalField value -> [ value.Span ])
                |> group
            | MemberAccess(_, _, nameSpan) -> Option.toList nameSpan
            | BinaryOp(_, opSpan, _, _) -> Option.toList opSpan
            | ContextNames(contextNames, _) ->
                contextNames |> List.collect (fun (_, typeOpt, span) -> span :: (typeOpt |> Option.toList |> List.collect inType))
            | _ -> []
        let inner =
            match expr.Kind with
            // A match's arms are walked as arms, so that each has a span of its own; its values as expressions.
            | Match(values, arms) -> (values |> List.collect (inExpression declared offset)) @ (arms |> List.collect (inArm declared offset))
            | Except(value, arms) -> inExpression declared offset value @ (arms |> List.collect (inArm declared offset))
            | _ ->
                let children, statements = SyntaxTree.childrenOf expr
                (children |> List.collect (inExpression declared offset)) @ (statements |> List.collect (inStatement declared offset))
        expr.Span :: field @ own @ inner

/// The spans of a match arm: the arm as a whole, from its first pattern to the end of its body, its patterns
/// together, each pattern and the parts of it, a guard's predicate among them, and its body.
and private inArm (declared: DeclaredNames) (offset: int) ((patterns, body): MatchArm) : SourceSpan list =
    let inPattern (pattern: Pattern) =
        (Pattern.within pattern |> List.map (fun part -> part.Span)) @ (Pattern.expressions pattern |> List.collect (inExpression declared offset))
    let arm =
        match patterns with
        | [] -> []
        | first :: _ -> across first.Span body.Span @ across first.Span (List.last patterns).Span
    arm @ (patterns |> List.collect inPattern) @ inExpression declared offset body

/// The spans of a statement in a block: the statement as a whole and the parts of it.
and private inStatement (declared: DeclaredNames) (offset: int) (statement: Statement) : SourceSpan list =
    if not (holds offset statement.Span) then []
    else
        let parts =
            match statement.Kind with
            | DefStatement(_, _, typeOpt, value) -> inDef declared offset typeOpt value
            | TypeDefStatement(_, _, _, body) -> inType body
            | UseStatement value
            | ExprStatement value -> inExpression declared offset value
            | ImportStatement items -> items |> List.map (fun item -> item.Span)
            | AttributeStatement(_, argument) -> argument |> Option.map (inExpression declared offset) |> Option.defaultValue []
        statement.Span :: parts

/// The spans of a def's name, its annotation and its value.
and private inDef (declared: DeclaredNames) (offset: int) (typeOpt: TypeExpr option) (value: Expression) : SourceSpan list =
    let name = declared |> Map.tryFind (value.Id, DefValue) |> Option.defaultValue [] |> List.map snd
    name @ (typeOpt |> Option.toList |> List.collect inType) @ inExpression declared offset value

let private inItem (declared: DeclaredNames) (offset: int) (item: TopLevelItem) : SourceSpan list =
    let parts =
        match item.Kind with
        | Def (ValueDef(_, _, typeOpt, value)) -> inDef declared offset typeOpt value
        | Def (TypeDef(_, _, _, body)) -> inType body
        | Expr value -> inExpression declared offset value
        | Attribute(_, argument) -> argument |> Option.toList |> List.collect (inExpression declared offset)
        | Import items -> items |> List.map (fun item -> item.Span)
        | ReExport item -> [ item.Span ]
        | DimensionDecl(_, _, storage) -> storage |> Option.toList |> List.collect inType
        | UnitDecl(_, _, _, multiple) -> multiple |> Option.toList |> List.collect (inExpression declared offset)
        | ModuleDecl _
        | ModuleBlock _ -> []
    item.Span :: parts

/// The selection ranges at `offset` in `source`, the text of the file at `file`: the spans of the syntax around it,
/// smallest first, each holding the one before it and larger than it, up to the whole file. A node that spans
/// exactly what its child spans is left out, so every step grows the selection. Whitespace and comments belong to
/// no node: at one, the chain starts at the smallest node around it. At the boundary of two nodes, as after `x` in
/// `f(x)`, it starts at the smaller one. `None` when the source does not parse.
let at (file: string) (source: string) (offset: int) : SourceSpan list option =
    match parseModuleFileWithNames file source with
    | Result.Error _ -> None
    | Result.Ok (module', declared, blocks) ->
        let normalized = source.Replace("\r\n", "\n").Replace("\r", "\n")
        let whole = { File = file; Start = 0; End = normalized.Length; StartLine = 0; StartColumn = 0 }
        let around =
            (module' |> List.collect (inItem declared offset)) @ (blocks |> List.map snd) @ [ whole ]
            |> List.filter (fun span -> not (SourceSpan.isNone span) && holds offset span)
            |> List.sortBy (fun span -> span.End - span.Start, span.Start)
        let grows (inner: SourceSpan) (outer: SourceSpan) =
            outer.Start <= inner.Start && inner.End <= outer.End && outer.End - outer.Start > inner.End - inner.Start
        around
        |> List.fold
            (fun chain span ->
                match chain with
                | inner :: _ when not (grows inner span) -> chain
                | _ -> span :: chain)
            []
        |> List.rev
        |> Some
