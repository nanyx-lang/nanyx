namespace Parser.Program

/// `value is pattern`, a `bool` that says whether `value` matches `pattern`: a match of `value` against the pattern,
/// whose arm is `true` and whose wildcard arm, made up at the pattern, is `false`, both made up at the `is`. In an `and`
/// chain, what follows the test is the arm's body in place of `true`, so the names the pattern binds are in scope
/// for the rest of the chain: `x is #some(v) and v > 3` is `match x | #some(v) -> v > 3 | _ -> false`. See `asMatch`
/// and `BindingIf` for an `if` whose condition binds names.
module IsSyntax =
    let keyword = "is"

    /// The test `value is pattern` spanning `span`, with `rest` the rest of an `and` chain after it, if any.
    let test (span: SourceSpan) (isSpan: SourceSpan) (value: Expression) (pattern: Pattern) (rest: Expression option) : Expression =
        let success = rest |> Option.defaultWith (fun () -> Expression.at isSpan (LiteralExpr(BoolLit true)))
        let wildcard = Pattern.at pattern.Span WildcardPattern
        Expression.at span (Match([ value ], [ [ pattern ], success; [ wildcard ], Expression.at isSpan (LiteralExpr(BoolLit false)) ]))

    /// A test `value is pattern` (see `test`): the value, the pattern, and the rest of the `and` chain after it, if any.
    /// A match written in the source has no wildcard arm at the same place as its first pattern.
    let (|Test|_|) (kind: ExpressionKind) =
        match kind with
        | Match([ value ], [ [ pattern ], success; [ wildcard ], failure ]) when
            wildcard.Kind = WildcardPattern
            && failure.Kind = LiteralExpr(BoolLit false)
            && wildcard.Span = pattern.Span
            && pattern.Span <> SourceSpan.none ->
            let rest = if success.Kind = LiteralExpr(BoolLit true) && success.Span = failure.Span then None else Some success
            Some(value, pattern, rest)
        | _ -> None

    /// `left and right`, spanning from `left` to `right`, with the names the tests in `left` bind in scope in `right`:
    /// when `left` is a test, or ends in one, `right` goes on its chain. Each test's arm body is reported to `declare`
    /// with the names its pattern binds.
    let rec andThen (declare: Expression -> (Identifier * SourceSpan) list -> unit) (span: SourceSpan) (operatorSpan: SourceSpan) (left: Expression) (right: Expression) : Expression =
        let inner (first: Expression) = andThen declare (SourceSpan.between first.Span right.Span) operatorSpan first right
        match left.Kind with
        | Test(value, pattern, rest) ->
            let isSpan = match left.Kind with Match(_, [ _; _, failure ]) -> failure.Span | _ -> left.Span
            let chained = match rest with Some rest -> inner rest | None -> right
            declare chained (Pattern.boundNamesWithSpans pattern)
            test span isSpan value pattern (Some chained)
        | BinaryOp("and", opSpan, first, (Kind (Test _) as last)) -> Expression.at span (BinaryOp("and", opSpan, first, inner last))
        | _ -> Expression.at span (BinaryOp("and", Some operatorSpan, left, right))

    /// The patterns of the tests along the `and` chain `condition`, in order, whose names are in scope at its end.
    let rec chainPatterns (condition: Expression) : Pattern list =
        match condition.Kind with
        | Test(_, pattern, rest) -> pattern :: (rest |> Option.map chainPatterns |> Option.defaultValue [])
        | BinaryOp("and", _, first, last) -> chainPatterns first @ chainPatterns last
        | _ -> []

    /// The names the tests along `condition`'s `and` chain bind that are in scope at its end, each once, with the span
    /// of the pattern that binds it last.
    let chainNames (condition: Expression) : (Identifier * SourceSpan) list =
        chainPatterns condition
        |> List.collect Pattern.boundNamesWithSpans
        |> List.rev
        |> List.distinctBy fst
        |> List.rev

    /// The tag the condition of `if` made up by `asMatch` is when it holds, with the names its tests bind.
    let heldTag = "__is_held"

    /// The tag that condition is when it doesn't hold.
    let failedTag = "__is_failed"

    /// `if condition then thenBranch else elseBranch` when the tests along `condition`'s `and` chain
    /// bind names, which are in scope in `thenBranch`: a match of the condition, made a tag that holds the names, against
    /// that tag. The `else` branch is written once:
    ///
    ///   match (match x | #some(v) -> if v > 3 then #__is_held(v) else #__is_failed | _ -> #__is_failed)
    ///     | #__is_held(v) -> thenBranch
    ///     | _ -> elseBranch
    ///
    /// `None` when the chain binds no names, and the `if` is an `if` of a `bool`.
    let asMatch (condition: Expression) (thenBranch: Expression) (elseBranch: Expression) : ExpressionKind option =
        match chainNames condition with
        | [] -> None
        | names ->
            let made kind = Expression.from condition kind
            let failed () = made (TagExpr(failedTag, None))
            let held =
                match names with
                | [ name, _ ] -> made (IdentifierExpr name)
                | _ -> made (TupleExpr(names |> List.map (fst >> IdentifierExpr >> made)))
            let rec lower (condition: Expression) (rest: Expression) : Expression =
                match condition.Kind with
                | Test(value, pattern, chained) ->
                    // The arm's body is where the parser declared the pattern's names (see `andThen`).
                    let body = match chained with Some chained -> { lower chained rest with Id = chained.Id } | None -> rest
                    made (Match([ value ], [ [ pattern ], body; [ Pattern.at condition.Span WildcardPattern ], failed () ]))
                | BinaryOp("and", _, first, last) -> lower first (lower last rest)
                | _ -> made (IfExpr(condition, rest, failed ()))
            let lowered = lower condition (made (TagExpr(heldTag, Some held)))
            let bindings =
                match names with
                | [ name, span ] -> Pattern.at span (IdentifierPattern name)
                | _ -> Pattern.at condition.Span (TuplePattern(names |> List.map (fun (name, span) -> Pattern.at span (IdentifierPattern name)), false))
            Some(
                Match(
                    [ lowered ],
                    [ [ Pattern.at condition.Span (TagPattern(heldTag, Some bindings)) ], thenBranch
                      [ Pattern.at elseBranch.Span WildcardPattern ], elseBranch ]
                )
            )

    /// `IfExpr(condition, thenBranch, elseBranch)` whose condition binds names, as the match `asMatch` makes of it.
    let (|BindingIf|_|) (kind: ExpressionKind) =
        match kind with
        | IfExpr(condition, thenBranch, elseBranch) -> asMatch condition thenBranch elseBranch
        | _ -> None

    /// `while condition -> body` when the tests along `condition`'s `and` chain bind names, which are in scope in `body`
    /// each time round: the loop whose condition is the `if` of `condition` that runs the body (see `asMatch`), and
    /// whose own body is `()`:
    ///
    ///   while (if condition then { body; true } else false) -> ()
    ///
    /// The block, and the `if`'s match, are made at the condition's node, both a `bool` as it is: the parser declares
    /// the names there, and the checker's type for it is the backend's for the match. `None` when the chain binds no
    /// names. The `else` is made at the body, so the `if`'s match is not itself a test (see `Test`).
    let whileAsIf (condition: Expression) (body: Expression) : (Expression * Expression) option =
        let ran =
            { Expression.from body (Block [ Statement.from body (ExprStatement body); Statement.from body (ExprStatement(Expression.from body (LiteralExpr(BoolLit true)))) ]) with
                Id = condition.Id }
        asMatch condition ran (Expression.from body (LiteralExpr(BoolLit false)))
        |> Option.map (fun running -> { condition with Kind = running }, Expression.from body UnitExpr)

    /// A `while` loop whose condition binds names, as the loop `whileAsIf` makes of it.
    let (|BindingWhile|_|) (kind: ExpressionKind) =
        match kind with
        | WhileSyntax.Call(condition, body) ->
            whileAsIf condition body |> Option.map (fun (condition, body) -> FunctionCall(WhileSyntax.builtIn, None, [ condition; body ]))
        | _ -> None
