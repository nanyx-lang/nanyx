namespace NyxCompiler

open Parser.Program
open NyxCompiler.ExpressionRewrites
open NyxCompiler.InferState

/// Compiling a module's type and name errors to traps, so that a debug build runs the code around them (see the
/// Deferred errors page).
module internal DeferredErrors =
    /// Where a module's errors were found, for `deferErrors` to place them.
    type Sites =
        { /// The diagnostics of each expression that failed to check, by its node.
          Failed: Map<NodeId, Diagnostic list>
          /// The conflicts among the constraints, by where each was found: only known by that span, the expression
          /// whose constraints conflicted. Several can be found at one span.
          Mismatches: Map<SourceSpan, Diagnostic list>
          /// The cases each match that does not handle every case leaves out, by the match's node.
          Unhandled: Map<NodeId, Diagnostic list>
          /// The expressions that need the type of a value that has none, because of an error.
          UnknownTypeUses: Set<NodeId> }

    /// Where the errors `state` recorded as it inferred a module were found, with `mismatches`, the conflicts solving
    /// its constraints found. An expression recorded twice keeps the diagnostics recorded first (the lists are newest
    /// first).
    let sitesOf (state: InferState) (mismatches: Diagnostic list) : Sites =
        let byNode (entries: (NodeId * Diagnostic list) list) = Map.ofList entries
        { Failed = byNode state.FailedExprs
          Mismatches =
            mismatches
            |> List.choose (fun diagnostic -> diagnostic.Span |> Option.map (fun span -> span, diagnostic))
            |> List.groupBy fst
            |> List.map (fun (span, found) -> span, List.map snd found)
            |> Map.ofList
          Unhandled = byNode state.UnhandledMatches
          UnknownTypeUses = Set.ofList state.UnknownTypeUses }

    /// Where `span` is, as `nanyx` prints a diagnostic's place: `file:line:column: `.
    let private deferredLocation (span: SourceSpan) =
        if span.File = "" then $"{span.StartLine + 1}:{span.StartColumn + 1}: "
        else $"{span.File}:{span.StartLine + 1}:{span.StartColumn + 1}: "

    /// What a deferred error writes when it is reached: the diagnostic as `nanyx` prints it, marked as deferred.
    let internal deferredMessage (diagnostic: Diagnostic) =
        let location = diagnostic.Span |> Option.map deferredLocation |> Option.defaultValue ""
        $"{location}error {diagnostic.Code}: {diagnostic.Message} (deferred from compile time)\n"

    /// `expr` with each expression that failed to check replaced by a call to `__panic`, which writes the errors
    /// and traps when it is reached, so a debug build runs the code around it, and the diagnostics placed so. An
    /// expression in `sites.Failed` is found by its node; the conflicts in `sites.Mismatches` replace the outermost
    /// expression written at their span. A match in `sites.Unhandled`, which does not handle every case, is given a
    /// last arm that catches the rest and traps.
    ///
    /// An expression in `sites.UnknownTypeUses` needs the type of a value that has none, because of an error (piping
    /// it, or reading a member of it): the backend could not compile it, so it traps too. It is not an error of its
    /// own, so it writes why it stopped without a code.
    let internal deferErrors (sites: Sites) (expr: Expression) : Expression * Diagnostic list =
        let placed = ResizeArray<Diagnostic>()
        let panicWith (node: Expression) (message: string) =
            FunctionCall(PanicSyntax.builtIn, None, [ Expression.from node (LiteralExpr (StringLit message)) ])
        let panic (node: Expression) (diagnostics: Diagnostic list) =
            placed.AddRange diagnostics
            panicWith node (diagnostics |> List.map deferredMessage |> String.concat "")
        let deferred =
            expr
            |> mapExpressionKinds (fun node kind ->
                let diagnostics =
                    match sites.Failed |> Map.tryFind node.Id with
                    | Some diagnostics -> diagnostics
                    | None -> sites.Mismatches |> Map.tryFind node.Span |> Option.defaultValue []
                match diagnostics, kind, sites.Unhandled |> Map.tryFind node.Id with
                | _ :: _, _, _ -> panic node diagnostics
                | [], Match(values, arms), Some missing ->
                    let catchAll = values |> List.map (fun _ -> Pattern.at node.Span WildcardPattern)
                    Match(values, arms @ [ catchAll, Expression.from node (panic node missing) ])
                | [], _, _ when sites.UnknownTypeUses.Contains node.Id ->
                    panicWith node $"{deferredLocation node.Span}error: the value used here has no known type, because of an error deferred from compile time\n"
                | _ -> kind)
        deferred, List.ofSeq placed
