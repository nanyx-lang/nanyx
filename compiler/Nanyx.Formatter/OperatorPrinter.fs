/// Printing chains of binary operators and of pipes.
module internal Nanyx.Formatter.OperatorPrinter

open Parser.Program
open Nanyx.Formatter.SourceText
open Nanyx.Formatter.Position
open Nanyx.Formatter.CommentPlacement
open Nanyx.Formatter.ExpressionParts
open Nanyx.Formatter.CallPrinter

/// Prints operator and pipe chains in `source`, with the comments between a pipe's steps put back by `placement`. Their
/// operands are printed through `parts`, and a pipe's arguments by `calls`.
type OperatorPrinter(indentSize: int, source: string, sourceText: SourceText, placement: CommentPlacement, parts: ExpressionParts, calls: CallPrinter) =
    let text = Doc.text
    let cat = Doc.concat
    let hard = Doc.hardLine
    let nest doc = Doc.nest indentSize doc
    let parens doc = cat [ text "("; doc; text ")" ]
    let unplacedIn start finish = placement.UnplacedIn(start, finish)
    let expr = parts.Expression
    let armsInSequence = parts.ArmsInSequence
    let pipeArguments = calls.PipeArguments
    let nameEnd = calls.NameEnd
    /// A chain of operators of one precedence, `a ++ b ++ c`, as its operands and the operators between them. In a
    /// position that may cross lines, as many operands go on a line as fit, each line ending in an operator (the
    /// parser takes a line break after an operator, not before one), and the lines after the first indented once.
    let rec binary (position: Position) (op: string) (l: Expression) (r: Expression) : Doc =
        let p = precedence op
        let grouping = associativity op
        let isRight = grouping = RightAssociative
        // A node of the chain: same precedence and grouping, which groups at all, needing no brackets where it is, and
        // not a list with spreads.
        let continues (x: Expression) =
            match x.Kind with
            | BinaryOp(op', _, _, _) ->
                grouping <> NonAssociative
                && precedence op' = p
                && associativity op' = grouping
                && (spreadSegments x).IsNone
            | _ -> false
        let rec flattenLeft (x: Expression) =
            match x.Kind with
            | BinaryOp(op', _, a, b) when continues x ->
                let operands, operators = flattenLeft a
                operands @ [ b ], operators @ [ op' ]
            | _ -> [ x ], []
        let rec flattenRight (x: Expression) =
            match x.Kind with
            | BinaryOp(op', _, a, b) when continues x ->
                let operands, operators = flattenRight b
                a :: operands, op' :: operators
            | _ -> [ x ], []
        let operands, operators =
            if isRight then
                let rest, operators = flattenRight r
                l :: rest, op :: operators
            else
                let first, operators = flattenLeft l
                first @ [ r ], operators @ [ op ]
        let count = operands.Length
        let operandDoc index operand =
            let isLast = index = count - 1
            // An operand of the same precedence that groups another way is bracketed: `(f >> g) << h`.
            let groupsAnotherWay =
                match operand.Kind with
                | BinaryOp(op', _, _, _) -> precedence op' = p && associativity op' <> grouping
                | _ -> false
            let prec =
                match grouping with
                | _ when groupsAnotherWay -> p + 1
                | RightAssociative -> if isLast then p else p + 1
                | LeftAssociative -> if index = 0 then p else p + 1
                | NonAssociative -> p + 1
            expr { position with Tail = isLast && position.Tail; Prec = prec } operand
        let docs = List.mapi operandDoc operands
        // A shorthand lambda's implicit argument, the first operand, is printed as nothing, and so is the space after it.
        let leftOut index = index = 0 && (List.head operands).Kind = IdentifierExpr implicitArgument
        if position.Inline then
            List.zip (List.take (count - 1) docs) operators
            |> List.mapi (fun index (doc, operator) -> if leftOut index then [ text $"{operator} " ] else [ doc; text $" {operator} " ])
            |> List.concat
            |> fun parts -> cat (parts @ [ List.last docs ])
        else
            let items =
                List.zip (List.take (count - 1) docs) operators
                |> List.mapi (fun index (doc, operator) -> [ (if leftOut index then text operator else cat [ doc; text $" {operator}" ]); Doc.line ])
                |> List.concat
            nest (Fill(items @ [ List.last docs ]))

    /// `[a, ...xs, b]` is parsed as `[a] ++ xs ++ [b]`, the list runs spanning the whole literal. The segments, each a
    /// run of elements or a list spread, when `e` is such a literal.
    and spreadSegments (e: Expression) : Choice<Expression list, Expression> list option =
        match e.Kind with
        | BinaryOp("++", _, l, r) when sourceText.Valid e.Span && source[e.Span.Start] = '[' && (l.Span = e.Span || (sourceText.Valid l.Span && l.Span.Start > e.Span.Start)) ->
            let segment (x: Expression) =
                match x.Kind with
                | ListExpr items when x.Span = e.Span -> Choice1Of2 items
                | _ -> Choice2Of2 x
            let rec flatten (x: Expression) =
                match x.Kind with
                | BinaryOp("++", _, a, b) when x.Span = e.Span -> segment a :: flatten b
                | _ -> [ segment x ]
            Some(segment l :: flatten r)
        | _ -> None

    /// A pipe chain, `value \f \g(x)`, perhaps with `\match` steps. It stays on one line when it was written on one and
    /// fits; otherwise each step goes on a line of its own, indented once from the line the chain starts on, as the
    /// parser needs a pipe that continues a statement to be indented more than the statement's line. A chain that
    /// takes up lines anyway, with a lambda that spans lines in it, is broken only if it was written so. A step after
    /// a `\match` always goes on a line of its own, since the arms take the rest of their lines. A chain in a position
    /// that something follows on its line stays on one line.
    let pipeChain (position: Position) (e: Expression) : Doc =
        // The value at the start of the chain, and each step with the expression that is the chain up to it: the value
        // it pipes, the offset of its `\` or name in the source, and its document.
        let rec collect (e: Expression) steps =
            match e.Kind with
            | Pipe(value, n, nameSpan, args) ->
                let at = match nameSpan with Some span when sourceText.Valid span -> span.Start | _ -> -1
                let doc = cat [ text "\\"; text n; pipeArguments e (nameEnd nameSpan value.Span.End) args ]
                collect value ((value, at, false, doc) :: steps)
            | Match([ value ], arms) ->
                match sourceText.PipedMatchAt e value with
                | Some at ->
                    let doc = cat [ text "\\match"; nest (cat [ hard; armsInSequence value.Span.End e.Span.End arms ]) ]
                    collect value ((value, at, true, doc) :: steps)
                | None -> e, steps
            | _ -> e, steps
        let root, steps = collect e []
        // A pipe binds tighter than every binary operator, so one piped is bracketed: `(a + b) \f`. A range without an end
        // has nothing after its `..` for a pipe to take, so it is not: `1.. \take(3)`.
        let rootDoc =
            match root.Kind with
            | BinaryOp _
            | RangeSyntax.Call(_, Some _) -> parens (expr full root)
            | _ -> expr { position with Tail = false; Prec = 0 } root
        let isMatch = steps |> List.map (fun (_, _, isMatch, _) -> isMatch)
        let afterMatch = false :: List.truncate (steps.Length - 1) isMatch
        // Whether the step at `at` starts a line of its own after the value it pipes.
        let startsLine (value: Expression) at = at >= 0 && sourceText.Valid value.Span && value.Span.End > 0 && sourceText.LineOf at > sourceText.LineOf (value.Span.End - 1)
        let writtenOnLines =
            List.zip steps afterMatch |> List.exists (fun ((value, at, _, _), afterMatch) -> not afterMatch && startsLine value at)
        let stepDocs = steps |> List.map (fun (_, _, _, doc) -> doc)
        // The comments between a step and the value it pipes: one at the end of the value's last line stays there, and
        // any other goes on a line of its own before the step.
        let commentsBefore (value: Expression) at =
            if at < 0 || not (sourceText.Valid value.Span) then
                Doc.empty
            else
                cat [ for i in unplacedIn value.Span.End at ->
                          let comment, span = placement.Place i
                          if sourceText.LineOf span.Start = sourceText.LineOf (max 0 (value.Span.End - 1)) then Doc.trailingComment comment
                          else cat [ hard; Doc.comment comment ] ]
        if position.Tail && writtenOnLines then
            cat [ rootDoc; nest (cat [ for value, at, _, doc in steps -> cat [ commentsBefore value at; hard; doc ] ]) ]
        elif position.Tail && steps.Length > 1 && not (List.contains true isMatch)
             && not (List.exists Doc.hasHardLine (rootDoc :: stepDocs)) then
            Doc.group (cat [ rootDoc; nest (cat [ for doc in stepDocs -> cat [ Doc.line; doc ] ]) ])
        else
            let step doc afterMatch = if afterMatch then nest (cat [ hard; doc ]) else cat [ text " "; doc ]
            cat (rootDoc :: List.map2 step stepDocs afterMatch)

    member _.Binary position op l r = binary position op l r

    member _.SpreadSegments e = spreadSegments e

    member _.PipeChain position e = pipeChain position e