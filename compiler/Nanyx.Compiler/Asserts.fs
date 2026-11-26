namespace NyxCompiler

open Parser.Program

/// `assert(condition)`, once the module is typed: each call of the built-in `assert` is written as a call to
/// `AssertSyntax.rewritten`, with what its message needs, which only the front end has: where it is, its source text, and
/// the values the message shows, each with its label. For a comparison, those are each side that is not a constant;
/// then each name the condition reads, but one bound inside it (a lambda's parameter, a match's binding). Each value is
/// the node the condition holds, so the backend evaluates it once, for the condition and the message alike, and prints
/// it as `dbg` does (see the backend's `CoreIrLower`).
module internal Asserts =
    let private comparisons = set [ "=="; "!="; "<"; "<="; ">"; ">=" ]

    /// Whether `expr` is written out in full, so that the message has nothing to show of it: a literal, a tag, or a
    /// tuple, list, record or constructor's call of such parts.
    let rec private isConstant (expr: Expression) =
        match expr.Kind with
        | LiteralExpr _
        | UnitExpr -> true
        | TagExpr(_, payload) -> payload |> Option.forall isConstant
        | TupleExpr items
        | ListExpr items -> List.forall isConstant items
        | RecordExpr fields -> fields |> List.forall (function NamedField(_, value) | PositionalField value -> isConstant value)
        | FunctionCall(name, _, args) when name.Length > 0 && System.Char.IsUpper name[name.LastIndexOf '.' + 1] -> List.forall isConstant args
        | _ -> false

    /// The names `expr` reads, each node once by its name, in the order they are written: not inside a lambda, a block or
    /// a match's arms, which bind names of their own.
    let rec private namesIn (expr: Expression) : (string * Expression) list =
        match expr.Kind with
        | IdentifierExpr name -> [ name, expr ]
        | Lambda _
        | Block _ -> []
        | Match(values, _) -> values |> List.collect namesIn
        | _ -> fst (SyntaxTree.childrenOf expr) |> List.collect namesIn

    /// The text `span` covers in `source`, if it is there.
    let private textOf (source: string) (span: SourceSpan) =
        if span.Start >= 0 && span.End <= source.Length && span.Start < span.End then Some (source.Substring(span.Start, span.End - span.Start))
        else None

    /// The values the message of `assert(condition)` shows, each with its label. `isFunction` says whether a name's
    /// node is of a function type, which has nothing to show.
    let private shown (source: string) (isFunction: NodeId -> bool) (condition: Expression) : (string * Expression) list =
        let sides =
            match condition.Kind with
            | BinaryOp(op, _, left, right) when comparisons.Contains op ->
                [ left; right ]
                |> List.filter (isConstant >> not)
                |> List.choose (fun side -> textOf source side.Span |> Option.map (fun text -> text, side))
            | _ -> []
        let sideNames = sides |> List.map fst |> Set.ofList
        let names =
            match condition.Kind with
            | IdentifierExpr _ -> []
            | _ ->
                namesIn condition
                |> List.filter (fun (name, node) -> not (sideNames.Contains name) && not (isFunction node.Id))
                |> List.distinctBy fst
        sides @ names

    /// `expr` with each call of the built-in `assert` in `asserts` written out (see the module's comment), its place's
    /// file named from `root` as a crash's is (see `PackageModules.placeOf`).
    let private writeOutIn (source: string) (root: string option) (asserts: Set<NodeId>) (isFunction: NodeId -> bool) (expr: Expression) : Expression =
        expr
        |> ExpressionRewrites.mapExpressionKinds (fun node kind ->
            match kind with
            | AssertSyntax.Call condition when asserts.Contains node.Id ->
                let written = textOf source node.Span |> Option.defaultValue $"{AssertSyntax.builtIn}(...)"
                let literal text = Expression.from node (LiteralExpr(StringLit text))
                let header = $"{PackageModules.placeOf root node.Span}{written}\n"
                let values = shown source isFunction condition |> List.collect (fun (label, value) -> [ literal label; value ])
                FunctionCall(AssertSyntax.rewritten, None, condition :: literal header :: values)
            | _ -> kind)

    /// `module'`, typed in `state` from `source`, with each call of the built-in `assert` written out (see the module's
    /// comment), in a program that names its files from `root`. `isFunction` says whether a node is of a function type.
    let writeOut (source: string) (root: string option) (state: InferState.InferState) (isFunction: NodeId -> bool) (module': Module) : Module =
        if state.Asserts.IsEmpty then
            module'
        else
            let rewrite = writeOutIn source root state.Asserts isFunction
            module'
            |> List.map (fun item ->
                match item.Kind with
                | Def (ValueDef(isExport, name, typeOpt, value)) -> { item with Kind = Def (ValueDef(isExport, name, typeOpt, rewrite value)) }
                | Expr value -> { item with Kind = Expr (rewrite value) }
                | _ -> item)
