namespace NyxCompiler

open Parser.Program
open NyxCompiler.ExpressionRewrites

/// Compiling a module's type and name errors to traps, so that a debug build runs the code around them (see the
/// Deferred errors page).
module internal DeferredErrors =
    /// Where `span` is, as `nanyx` prints a diagnostic's place: `file:line:column: `.
    let private deferredLocation (span: SourceSpan) =
        if span.File = "" then $"{span.StartLine + 1}:{span.StartColumn + 1}: "
        else $"{span.File}:{span.StartLine + 1}:{span.StartColumn + 1}: "

    /// What a deferred error writes when it is reached: the diagnostic as `nanyx` prints it, marked as deferred.
    let internal deferredMessage (diagnostic: Diagnostic) =
        let location = diagnostic.Span |> Option.map deferredLocation |> Option.defaultValue ""
        $"{location}error {diagnostic.Code}: {diagnostic.Message} (deferred from compile time)\n"

    /// `expr` with each expression that failed to check replaced by a call to `__panic`, which writes the errors
    /// and traps when it is reached, so a debug build runs the code around it. An expression in `failed` is found
    /// by its node. A conflict among the constraints is only known by where it was found, the expression whose
    /// constraints conflicted, so one in `mismatches` replaces the outermost expression written there. Each
    /// diagnostic placed is added to `placed`, by `deferredMessage`. A match in `unhandled`, which does not handle
    /// every case, is given a last arm that catches the rest and traps.
    ///
    /// An expression in `unknownTypeUses` needs the type of a value that has none, because of an error (piping it,
    /// or reading a member of it): the backend could not compile it, so it traps too. It is not an error of its own,
    /// so it writes why it stopped without a code.
    let internal deferErrors (failed: Map<NodeId, Diagnostic list>) (mismatches: Map<SourceSpan, Diagnostic>) (unhandled: Map<NodeId, Diagnostic list>) (unknownTypeUses: Set<NodeId>) (placed: System.Collections.Generic.HashSet<string>) (expr: Expression) =
        let panicWith (node: Expression) (message: string) =
            FunctionCall("__panic", None, [ Expression.from node (LiteralExpr (StringLit message)) ])
        let panic (node: Expression) (diagnostics: Diagnostic list) =
            let messages = diagnostics |> List.map deferredMessage
            messages |> List.iter (placed.Add >> ignore)
            panicWith node (String.concat "" messages)
        expr
        |> mapExpressionKinds (fun node kind ->
            let diagnostics =
                match failed |> Map.tryFind node.Id with
                | Some diagnostics -> diagnostics
                | None -> mismatches |> Map.tryFind node.Span |> Option.toList
            match diagnostics, kind, unhandled |> Map.tryFind node.Id with
            | _ :: _, _, _ -> panic node diagnostics
            | [], Match(values, arms), Some missing ->
                let catchAll = values |> List.map (fun _ -> Pattern.at node.Span WildcardPattern)
                Match(values, arms @ [ catchAll, Expression.from node (panic node missing) ])
            | [], _, _ when unknownTypeUses.Contains node.Id ->
                panicWith node $"{deferredLocation node.Span}error: the value used here has no known type, because of an error deferred from compile time\n"
            | _ -> kind)

