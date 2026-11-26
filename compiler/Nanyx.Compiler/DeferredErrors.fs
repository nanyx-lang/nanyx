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
          UnknownTypeUses: Set<NodeId>
          /// The root the program names its files from, its package's, which none is for a script (see
          /// `PackageModules.shownFile`).
          Root: string option }

    /// Where the errors `state` recorded as it inferred a module were found, with `mismatches`, the conflicts solving
    /// its constraints found, in a program that names its files from `root`. An expression recorded twice keeps the
    /// diagnostics recorded first (the lists are newest first).
    let sitesOf (state: InferState) (mismatches: Diagnostic list) (root: string option) : Sites =
        let byNode (entries: (NodeId * Diagnostic list) list) = Map.ofList entries
        { Failed = byNode state.FailedExprs
          Mismatches =
            mismatches
            |> List.choose (fun diagnostic -> diagnostic.Span |> Option.map (fun span -> span, diagnostic))
            |> List.groupBy fst
            |> List.map (fun (span, found) -> span, List.map snd found)
            |> Map.ofList
          Unhandled = byNode state.UnhandledMatches
          UnknownTypeUses = Set.ofList state.UnknownTypeUses
          Root = root }

    /// What a deferred error writes when it is reached, on one line: its place, its file named from `root` as a crash's
    /// is (see `PackageModules.placeOf`), its code and headline, then its body, marked as deferred.
    let internal deferredMessage (root: string option) (diagnostic: Diagnostic) =
        let location = diagnostic.Span |> Option.map (PackageModules.placeOf root) |> Option.defaultValue ""
        $"{location}error {diagnostic.Code}: {Diagnostics.oneLine diagnostic} (deferred from compile time)\n"

    /// `expr` with each expression that failed to check replaced by a call to `__crash`, which writes the errors
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
        let crashWith (node: Expression) (message: string) =
            FunctionCall(CrashSyntax.builtIn, None, [ Expression.from node (LiteralExpr (StringLit message)) ])
        let crash (node: Expression) (diagnostics: Diagnostic list) =
            placed.AddRange diagnostics
            crashWith node (diagnostics |> List.map (deferredMessage sites.Root) |> String.concat "")
        let deferred =
            expr
            |> mapExpressionKinds (fun node kind ->
                let diagnostics =
                    match sites.Failed |> Map.tryFind node.Id with
                    | Some diagnostics -> diagnostics
                    | None -> sites.Mismatches |> Map.tryFind node.Span |> Option.defaultValue []
                match diagnostics, kind, sites.Unhandled |> Map.tryFind node.Id with
                | _ :: _, _, _ -> crash node diagnostics
                | [], Match(values, arms), Some missing ->
                    let catchAll = values |> List.map (fun _ -> Pattern.at node.Span WildcardPattern)
                    Match(values, arms @ [ catchAll, Expression.from node (crash node missing) ])
                | [], _, _ when sites.UnknownTypeUses.Contains node.Id ->
                    crashWith node $"{PackageModules.placeOf sites.Root node.Span}error: the value used here has no known type, because of an error deferred from compile time\n"
                | _ -> kind)
        deferred, List.ofSeq placed
