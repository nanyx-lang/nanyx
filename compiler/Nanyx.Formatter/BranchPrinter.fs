/// Printing the constructs that branch: `match`, `except`, `if` and `for`, with their arms.
module internal Nanyx.Formatter.BranchPrinter

open Parser.Program
open Nanyx.Formatter.SourceText
open Nanyx.Formatter.Position
open Nanyx.Formatter.CommentPlacement
open Nanyx.Formatter.ExpressionParts

/// What an `if` arm tests: a condition, or a value against a pattern (`if x is #some(y) then ...`).
type IfHead =
    | Condition of Expression
    | Binding of Expression * Pattern

/// Prints branching constructs and their arms, with the comments among the arms put back by `placement`, and their
/// parts printed through `parts`.
type BranchPrinter(indentSize: int, sourceText: SourceText, placement: CommentPlacement, parts: ExpressionParts) =
    let text = Doc.text
    let cat = Doc.concat
    let hard = Doc.hardLine
    let nest doc = Doc.nest indentSize doc
    let commaJoin docs = Doc.join (text ", ") docs
    let sequence = placement.Sequence
    let expr = parts.Expression
    let isWrittenBlock = parts.IsWrittenBlock
    let block = parts.Block
    let pattern = parts.Pattern

    /// Match arms one per line. Arms that share one body are the alternatives of an or-pattern, `| a | b -> body`.
    let rec armsInSequence (start: int) (finish: int) (arms: MatchArm list) : Doc =
        let groups =
            arms
            |> List.fold
                (fun groups (patterns, body: Expression) ->
                    match groups with
                    | (alternatives, shared: Expression) :: rest when obj.ReferenceEquals(shared, body) -> (alternatives @ [ patterns ], shared) :: rest
                    | _ -> ([ patterns ], body) :: groups)
                []
            |> List.rev
        let count = groups.Length
        groups
        |> List.mapi (fun index (alternatives, body) ->
            let armStart =
                match alternatives with
                | (first :: _) :: _ when sourceText.Valid first.Span -> first.Span.Start
                | _ -> body.Span.Start
            armStart, body.Span.End, fun () ->
                let patterns = alternatives |> List.map (fun ps -> commaJoin (List.map pattern ps)) |> Doc.join (text " | ")
                cat [ text "| "; patterns; armBody (index = count - 1) body ])
        |> sequence false start finish

    /// `-> body`: an indented block on the lines after it, or an expression on the same line. Statements separated by
    /// `;` stay on the same line if they fit there, and are otherwise a block.
    and armBody (isLast: bool) (body: Expression) : Doc =
        match parts.SemicolonLine body with
        | Some line -> Doc.group (cat [ text " ->"; nest (cat [ Doc.line; line () ]) ])
        | None when isWrittenBlock body -> cat [ text " ->"; nest (cat [ hard; block body ]) ]
        | None -> cat [ text " -> "; expr { statementPosition with Tail = isLast } body ]

    let matchDoc (e: Expression) (scrutinees: Expression list) (arms: MatchArm list) : Doc =
        let armsDoc = nest (cat [ hard; armsInSequence e.Span.Start e.Span.End arms ])
        match scrutinees with
        // A block scrutinee is indented more than the arms, which are indented more than the `match`.
        | [ scrutinee ] when (match scrutinee.Kind with Block _ -> true | _ -> false) ->
            cat [ text "match"; Doc.nest (2 * indentSize) (cat [ hard; block scrutinee ]); armsDoc ]
        | _ -> cat [ text "match "; commaJoin (List.map (expr { full with Tail = false }) scrutinees); armsDoc ]

    /// The patterns of arms that all share one body, joined as alternatives `a | b`, and that body.
    let singleArm (arms: MatchArm list) : (Doc * Expression) option =
        match List.distinctBy (fun (_, body: Expression) -> body.Id) arms with
        | [ _, body ] -> Some(arms |> List.map (fun (ps, _) -> commaJoin (List.map pattern ps)) |> Doc.join (text " | "), body)
        | _ -> None

    /// `value except pattern -> replacement` when there is one arm (perhaps with alternatives), and otherwise `value
    /// except` with the arms on the lines after it, as a match has them.
    let exceptDoc (position: Position) (e: Expression) (value: Expression) (arms: MatchArm list) : Doc =
        let valueDoc = expr { position with Tail = false; Prec = 0 } value
        match singleArm arms with
        | Some(patterns, body) -> cat [ valueDoc; text " except "; patterns; armBody true body ]
        | None -> cat [ valueDoc; text " except"; nest (cat [ hard; armsInSequence value.Span.End e.Span.End arms ]) ]

    /// An `if` as its head, its `then` branch and its `else` branch if it has one (a left-out `else` is `()` made up at
    /// the `then` branch). `if x is pattern then a else b` is parsed as a match of `x` against the pattern and `_`.
    let ifArm (e: Expression) : (IfHead * Expression * Expression option) option =
        let elseOf (thenBranch: Expression) (elseBranch: Expression) =
            if elseBranch.Kind = UnitExpr && elseBranch.Span = thenBranch.Span then None else Some elseBranch
        match e.Kind with
        | IfExpr(condition, thenBranch, elseBranch) -> Some(Condition condition, thenBranch, elseOf thenBranch elseBranch)
        | Match([ value ], [ [ p ], thenBranch; [ wildcard ], elseBranch ]) when
            wildcard.Kind = WildcardPattern
            && wildcard.Span = elseBranch.Span
            && (sourceText.StartsWith e.Span "if" || sourceText.StartsWith e.Span "|")
            && not (obj.ReferenceEquals(thenBranch, elseBranch))
            ->
            Some(Binding(value, p), thenBranch, elseOf thenBranch elseBranch)
        | _ -> None

    let ifHead (head: IfHead) : Doc =
        match head with
        | Condition condition -> expr { full with Tail = false } condition
        | Binding(value, p) -> cat [ expr { full with Tail = false } value; text " is "; pattern p ]

    let ifDoc (e: Expression) : Doc =
        let head, thenBranch, elseBranch = (ifArm e).Value
        // `if | a -> x | b -> y | else -> z` is parsed as nested ifs, each after the first spanning from its `|`.
        if sourceText.StartsWith e.Span "if" && sourceText.NextChar (e.Span.Start + 2) = '|' then
            let rec chain arms (head, body, rest: Expression option) =
                match rest with
                | Some r when sourceText.StartsWith r.Span "|" && (ifArm r).IsSome -> chain ((head, body) :: arms) (ifArm r).Value
                | _ -> List.rev ((head, body) :: arms), rest
            let arms, elseBranch = chain [] (head, thenBranch, elseBranch)
            let headStart head =
                match head with
                | Condition condition -> condition.Span.Start
                | Binding(value, _) -> value.Span.Start
            let conditionArms =
                arms
                |> List.mapi (fun index (head, body: Expression) ->
                    let isLast = index = arms.Length - 1 && elseBranch.IsNone
                    headStart head, body.Span.End, fun () -> cat [ text "| "; ifHead head; armBody isLast body ])
            let elseArm =
                match elseBranch with
                | Some body -> [ body.Span.Start, body.Span.End, fun () -> cat [ text "| else"; armBody true body ] ]
                | None -> []
            cat [ text "if"; nest (cat [ hard; sequence false e.Span.Start e.Span.End (conditionArms @ elseArm) ]) ]
        else
            let isBlock (x: Expression) = match x.Kind with Block _ -> true | _ -> false
            let isSemicolonLine (x: Expression) = (parts.SemicolonLine x).IsSome
            let branches = thenBranch :: Option.toList elseBranch
            let anyBlock = branches |> List.exists isBlock
            // Two expressions go on the `if`'s line if they fit, or else `then` and `else` start lines of their own,
            // indented once, which parses the same.
            if not anyBlock then
                let thenPart = cat [ Doc.line; text "then "; expr { full with Tail = elseBranch.IsNone } thenBranch ]
                let elsePart =
                    match elseBranch with
                    | Some elseBranch -> cat [ Doc.line; text "else "; expr full elseBranch ]
                    | None -> Doc.empty
                Doc.group (cat [ text "if "; ifHead head; nest (cat [ thenPart; elsePart ]) ])
            // Branches of statements separated by `;` go on the `if`'s line if they fit, with any branch that is an
            // expression, or else each is a block on the lines after its keyword, and the `else` starts a line.
            elif branches |> List.exists isSemicolonLine && branches |> List.forall (fun branch -> isSemicolonLine branch || not (isBlock branch)) then
                let branchDoc (branch: Expression) (isLast: bool) =
                    match parts.SemicolonLine branch with
                    | Some line -> nest (cat [ Doc.line; line () ])
                    | None -> cat [ text " "; expr { full with Tail = isLast } branch ]
                let elsePart =
                    match elseBranch with
                    | Some elseBranch -> cat [ Doc.line; text "else"; branchDoc elseBranch true ]
                    | None -> Doc.empty
                Doc.group (cat [ text "if "; ifHead head; text " then"; branchDoc thenBranch elseBranch.IsNone; elsePart ])
            else
                let thenDoc =
                    if isBlock thenBranch then cat [ text " then"; nest (cat [ hard; block thenBranch ]) ]
                    else cat [ text " then "; expr { full with Tail = elseBranch.IsNone } thenBranch ]
                let elseDoc =
                    match elseBranch with
                    | None -> Doc.empty
                    | Some elseBranch ->
                        let keyword = if isBlock thenBranch then cat [ hard; text "else" ] else text " else"
                        if isBlock elseBranch then cat [ keyword; nest (cat [ hard; block elseBranch ]) ]
                        else cat [ keyword; text " "; expr full elseBranch ]
                cat [ text "if "; ifHead head; thenDoc; elseDoc ]

    /// `for xs, ys | arms` and `for xs -> body`, parsed as a call to `__for` with a lambda of the elements. One arm
    /// (perhaps with alternatives) stays on the head's line, `for xs | x -> body`; several go on the lines after it.
    let forDoc (iterables: Expression list) (loop: Expression) : Doc =
        let head = cat [ text "for "; commaJoin (iterables |> List.map (expr { full with Tail = false })) ]
        match loop.Kind with
        | Lambda(parameters, body, _) ->
            match body.Kind with
            | Match(elements, arms) when elements |> List.map _.Kind = (parameters |> List.map (fst >> IdentifierExpr)) && body.Span = loop.Span ->
                match singleArm arms with
                | Some(patterns, armBodyExpr) -> cat [ head; text " | "; patterns; armBody true armBodyExpr ]
                | None -> cat [ head; nest (cat [ hard; armsInSequence loop.Span.Start loop.Span.End arms ]) ]
            | _ -> cat [ head; armBody true body ]
        | _ -> head

    member _.ArmsInSequence start finish arms = armsInSequence start finish arms

    member _.ArmBody isLast body = armBody isLast body

    member _.MatchDoc e scrutinees arms = matchDoc e scrutinees arms

    member _.ExceptDoc position e value arms = exceptDoc position e value arms

    member _.IfArm e = ifArm e

    member _.IfDoc e = ifDoc e

    member _.ForDoc iterables loop = forDoc iterables loop
