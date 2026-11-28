/// What `nanyx run --show-values` shows: each value the program's own code binds or gives, made a call of
/// `DbgSyntax.shown` around it, which is lowered as a `dbg` is but reported to the runtime's `value`, not its `dbg`.
/// A value is shown where it is bound or given: a local `def` that isn't a function, at the `def`, and a top-level one,
/// at its value; a statement whose value isn't `()`, the last of a block among them; a lambda's body that isn't a
/// block; and a script's top-level expression. A function is left out, as is a value of no type worth reading (`()`,
/// an empty record or a function), a written `dbg`, and what the compiler made up: a node with no file, or a name that
/// starts `__`, and the whole of a definition of such a name. Only the program's own files are shown (`files`): a
/// library's code is left as it is.
module internal Transpiler.Wasm.ShownValues

open Parser.Program

open NyxCompiler

/// Whether a value of `ty` is worth showing: not `()`, an empty record or a function.
let rec private worthShowing (ty: Ty) : bool =
    match ty with
    | TyAlias(_, _, aliased) -> worthShowing aliased
    | TyPrimitive "unit"
    | TyFunc _ -> false
    | TyRecord(fields, _) -> not fields.IsEmpty
    | _ -> true

/// Whether `name` is one the compiler made up, which code can't write.
let private madeUp (name: Identifier) : bool = name.StartsWith "__"

/// Whether `expr` is one the compiler made up, or a written `dbg`, which is shown as it is.
let private leftAlone (expr: Expression) : bool =
    SourceSpan.isNone expr.Span
    || (match expr.Kind with
        | FunctionCall(name, _, _) -> name = DbgSyntax.builtIn || madeUp name
        | Pipe(_, name, _, _) -> name = DbgSyntax.builtIn
        | IdentifierExpr name -> madeUp name
        | WorkflowBindExpr _
        | WorkflowReturnExpr _ -> true
        | _ -> false)

/// Rewrites the shown values of the files in `files`, recording each call it makes at the type of its value.
type private Rewriter(files: Set<string>, types: Map<NodeId, Ty>) =
    let mutable types = types

    member _.Types = types

    /// `value`, shown at `span` when it is in one of the files and of a type worth showing.
    member _.Show (span: SourceSpan) (value: Expression) : Expression =
        match types |> Map.tryFind value.Id with
        | Some ty when files.Contains span.File && not (leftAlone value) && worthShowing ty ->
            let shown = Expression.at value.Span (FunctionCall(DbgSyntax.shown, Some span, [ value ]))
            types <- types |> Map.add shown.Id ty
            shown
        | _ -> value

    member this.Statement (statement: Statement) : Statement =
        match statement.Kind with
        | DefStatement(isMutable, name, typeOpt, value) when not (madeUp name) ->
            { statement with Kind = DefStatement(isMutable, name, typeOpt, this.Show statement.Span value) }
        | ExprStatement value -> { statement with Kind = ExprStatement (this.Show value.Span value) }
        | _ -> statement

    /// `expr` with the values bound and given inside it shown.
    /// A script's statements are in a block the compiler made up (see `Scripts.asEntry`), so the walk goes into every
    /// expression, and `Show` keeps to the files.
    member this.Expression (expr: Expression) : Expression =
        let rec visit (current: Expression) : Expression =
            let current = SyntaxTree.mapChildren visit current
            { current with Kind = this.Kind current.Kind }
        visit expr

    /// A node's kind, its children rewritten already, with the values it binds and gives shown.
    member this.Kind (kind: ExpressionKind) : ExpressionKind =
        match kind with
        | Block statements -> Block (statements |> List.map this.Statement)
        | Lambda(parameters, body, takesUnit) when not (body.Kind.IsBlock || body.Kind.IsContextNames) ->
            Lambda(parameters, this.Show body.Span body, takesUnit)
        | ContextNames(names, body) when not body.Kind.IsBlock -> ContextNames(names, this.Show body.Span body)
        | other -> other

    member this.Item (item: TopLevelItem) : TopLevelItem =
        match item.Kind with
        | ModuleBlock(name, items) -> { item with Kind = ModuleBlock(name, items |> List.map this.Item) }
        // A definition the compiler made up, such as a derived instance's, is written at the code that needed it; a
        // script's statements are the one made up of the program's own code.
        | Def (ValueDef(_, name, _, _)) when madeUp name && name <> Scripts.definitionName -> item
        | Def (ValueDef(isExport, name, typeOpt, value)) ->
            // At the value: a top-level item comes back from the type checker with no span of its own.
            { item with Kind = Def (ValueDef(isExport, name, typeOpt, this.Show value.Span (this.Expression value))) }
        | Expr value -> { item with Kind = Expr (this.Show value.Span (this.Expression value)) }
        | _ -> item

/// `module'` with the values its code in `files` binds and gives shown, and `types` with the type of each call that
/// shows one. With no files, both as they are.
let wrap (files: Set<string>) (types: Map<NodeId, Ty>) (module': Module) : Module * Map<NodeId, Ty> =
    if files.IsEmpty then
        module', types
    else
        let rewriter = Rewriter(files, types)
        let rewritten = module' |> List.map rewriter.Item
        rewritten, rewriter.Types
