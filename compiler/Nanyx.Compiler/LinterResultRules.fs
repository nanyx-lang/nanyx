namespace NyxCompiler

open System
open Parser.Program

/// Whether an expression is pure: it can do nothing but give its value, because neither its type nor any call in it
/// requires a context. A call requires one when something provides a context to it, when it finds an instance of one,
/// or when the function it calls requires one, as the callee's type at the call or the def's own contexts say. A
/// context variable counts, since it may stand for any context.
module private Purity =
    let private hasContexts (ty: Ty) =
        match Unifier.unalias ty with
        | TyFunc(_, _, contexts)
        | TyContexts contexts -> not contexts.IsEmpty
        | _ -> false

    let rec private typeRequiresContext (ty: Ty) =
        hasContexts ty || Ty.children (Unifier.unalias ty) |> List.exists typeRequiresContext

    let private callRequiresContext (model: SemanticModel) (id: NodeId) =
        let nonEmpty (found: Map<NodeId, 'a list>) = found |> Map.tryFind id |> Option.exists (List.isEmpty >> not)
        let ofDef =
            match model.References |> Map.tryFind id with
            | Some symbol ->
                match model.ContextTypes |> Map.tryFind symbol, model.ImportedContextTypes |> Map.tryFind symbol with
                | Some contexts, _
                | None, Some (_, contexts) -> not contexts.IsEmpty
                | None, None -> false
            | None -> false
        nonEmpty model.ContextProviders
        || nonEmpty model.ContextInstances
        || model.CalleeTypes |> Map.tryFind id |> Option.exists hasContexts
        || ofDef

    let isPure (model: SemanticModel) (expr: Expression) =
        SyntaxTree.expressionsWithin expr
        |> List.forall (fun within ->
            let nodes = within.Id :: (model.Lowered |> Map.tryFind within.Id |> Option.defaultValue [])
            let typeIsPure = model.Types |> Map.tryFind within.Id |> Option.forall (typeRequiresContext >> not)
            match within.Kind with
            | ContextMemberCall _ -> false
            | _ -> typeIsPure && not (nodes |> List.exists (callRequiresContext model)))

/// The official linter's `unusedResults` rule: a statement of a block, other than its last, whose value is thrown away
/// though it is not `()`: `readLine()` on a line of its own. Left alone: a statement whose type is `()`, a type variable
/// (it may be `()`) or `undefined` (it never gives a value, which `unreachableCode` is about); a workflow keyword's
/// statement, whose value the workflow takes; and a block's last statement, which is the block's value. A `for` loop
/// is `()` and throws away what its body gives, so a value its body ends with is reported as a statement's is: in each
/// arm of an `if` or a `match` the body ends with. Not reported: a call of the built-in `dbg`, written on a line of its
/// own to print a value, and a pure statement (see `Purity`), which `uselessExpressions` reports. Its fix passes the
/// value to `ignore`.
module UnusedResults =
    let rule =
        { Id = "unusedResults"
          Title = "Unused result"
          DefaultSeverity = Severity.Error
          Unnecessary = false
          Description =
            "A statement of a block, other than its last, whose value is not `()` and is thrown away. Bind it to a name, or pass it to `ignore` to say it is not needed." }

    /// Whether `expr` calls the built-in `dbg` (`dbg(x)` or `x \dbg`), which gives back the value it prints: a call of
    /// a def of that name refers to the def, and the built-in to nothing.
    let private callsBuiltInDbg (model: SemanticModel) (expr: Expression) =
        match expr.Kind with
        | FunctionCall(name, _, _)
        | Pipe(_, name, _, _) -> name = DbgSyntax.builtIn && not (model.References.ContainsKey expr.Id)
        | _ -> false

    /// The type of `expr` if it is a value that a statement throws away: known, and neither `()` nor `undefined`.
    let private discardedType (model: SemanticModel) (expr: Expression) : Ty option =
        match expr.Kind with
        | WorkflowBindExpr _
        | WorkflowReturnExpr _ -> None
        | _ when callsBuiltInDbg model expr -> None
        | _ ->
            model.Types
            |> Map.tryFind expr.Id
            |> Option.filter (fun ty ->
                match Unifier.unalias ty with
                | TyVar _
                | TyPrimitive "unit" -> false
                | unaliased -> not (Unifier.isBottom unaliased))

    /// What the message calls the discarded `expr`: a call by the name written, and syntax the parser makes a call to a
    /// built-in by what was written, never by the built-in's name.
    let private whatIsUnused (expr: Expression) =
        match expr.Kind with
        | WithSyntax.Call _ -> "This `with` update"
        | SpreadSyntax.Call _ -> "This record"
        | RangeSyntax.Call _ -> "This range"
        | MapSyntax.Call _ -> "This map"
        | FunctionCall(name, _, _) when MadeUpNames.isMadeUp name -> "This value"
        | FunctionCall(name, _, _)
        | Pipe(_, name, _, _)
        | ContextMemberCall(_, name, _) -> $"The result of `{name}`"
        | _ -> "This value"

    /// The values `body` ends with, each with the statement it is when it is one a fix can remove: the value of the
    /// last statement of a block, of each arm of an `if` or a `match`, or `body` itself. A block's last statement is
    /// removable when the block has others.
    let rec private endsWith (body: Expression) : (Statement option * Expression) list =
        match body.Kind with
        | Block statements ->
            match List.tryLast statements with
            | Some ({ Kind = ExprStatement value } as last) ->
                match value.Kind with
                | Block _
                | IfExpr _
                | Match _ -> endsWith value
                | _ -> [ (if statements.Length > 1 then Some last else None), value ]
            | _ -> []
        | IfExpr(_, whenTrue, whenFalse) -> endsWith whenTrue @ endsWith whenFalse
        | Match(_, arms) -> arms |> List.collect (snd >> endsWith)
        | _ -> [ None, body ]

    /// Each value that is thrown away: a statement of a block other than its last, and a value a `for` loop's body ends
    /// with. Each comes with the statement a fix can remove, if any, its type and what a message calls it.
    let internal discarded (model: SemanticModel) : (Statement option * Expression * Ty * string) list =
        let expressions = model.Syntax |> List.collect SyntaxTree.ofItem
        let statements =
            expressions
            |> List.collect SyntaxTree.blocksWithin
            |> List.collect (fun statements ->
                statements
                |> List.take (statements.Length - 1)
                |> List.choose (fun statement ->
                    match statement.Kind with
                    | ExprStatement value -> Some (Some statement, value)
                    | _ -> None))
        let loopBodies =
            expressions
            |> List.collect SyntaxTree.expressionsWithin
            |> List.collect (fun expr ->
                match expr.Kind with
                | ForSyntax.Call(_, _, _, body) -> endsWith body
                | _ -> [])
        statements @ loopBodies
        |> List.choose (fun (statement, value) ->
            discardedType model value |> Option.map (fun ty -> statement, value, ty, whatIsUnused value))
        |> List.sortBy (fun (_, value, _, _) -> value.Span.Start)

    let lint (model: SemanticModel) : Report list =
        discarded model
        |> List.filter (fun (_, value, _, _) -> not (Purity.isPure model value))
        |> List.map (fun (_, value, ty, what) ->
            let written = model.Source.Substring(value.Span.Start, value.Span.End - value.Span.Start)
            { Rule = rule.Id
              Subject = None
              Span = value.Span
              Message = $"{what}, of type '{Unifier.tyToDisplayString Ascii ty}', is never used: bind it to a name, or pass it to `ignore`"
              Related = []
              Fixes = [ { Title = "Pass it to `ignore`"; Edits = [ { Span = value.Span; NewText = $"ignore({written})" } ] } ] })

/// The official linter's `uselessExpressions` rule: a statement whose value is thrown away, as `unusedResults` finds
/// them, and which is pure (see `Purity`): `n + 2` or `x == y` on a line of its own, which can never do anything. It is usually a mistake: a missing `def`, an `==` meant as a binding, or an operand split from
/// the line above. Its fix removes the statement.
module UselessExpressions =
    let rule =
        { Id = "uselessExpressions"
          Title = "Useless expression"
          DefaultSeverity = Severity.Error
          Unnecessary = false
          Description =
            "A statement of a block, other than its last, whose value is thrown away and which can do nothing else, because neither its type nor any call in it requires a context. Remove it, or use its value." }

    let lint (model: SemanticModel) : Report list =
        UnusedResults.discarded model
        |> List.filter (fun (_, value, _, _) -> Purity.isPure model value)
        |> List.map (fun (statement, value, ty, what) ->
            { Rule = rule.Id
              Subject = None
              Span = value.Span
              Message = $"{what}, of type '{Unifier.tyToDisplayString Ascii ty}', does nothing and is never used: remove it, or use its value"
              Related = []
              Fixes =
                statement
                |> Option.map (fun statement -> { Title = "Remove the statement"; Edits = [ SourceEdits.removal model.Source statement.Span ] })
                |> Option.toList })
