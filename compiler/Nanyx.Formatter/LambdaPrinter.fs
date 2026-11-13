/// Printing lambdas, with the shorthands the parser expands.
module internal Nanyx.Formatter.LambdaPrinter

open System
open Parser.Program
open Nanyx.Formatter.SourceText
open Nanyx.Formatter.Position
open Nanyx.Formatter.CommentPlacement
open Nanyx.Formatter.ExpressionParts

/// Prints lambdas, their parameters and their bodies through `parts`.
type LambdaPrinter(indentSize: int, sourceText: SourceText, placement: CommentPlacement, parts: ExpressionParts) =
    let text = Doc.text
    let cat = Doc.concat
    let hard = Doc.hardLine
    let nest doc = Doc.nest indentSize doc
    let commaJoin docs = Doc.join (text ", ") docs
    let hasCommentsIn start finish = placement.HasCommentsIn(start, finish)
    let expr = parts.Expression
    let needsParentheses = parts.NeedsParentheses
    let isWrittenBlock = parts.IsWrittenBlock
    let pattern = parts.Pattern
    let typeExpr = parts.TypeExpr

    /// A lambda's parameters as written, each with the pattern it was written as, if any, and its body as written. A
    /// pattern parameter (`{ (a, b) -> a + b }`) is taken apart by a one-arm match the parser puts at the top of the
    /// body, over the parameter that stands in its place (`patternArgument`).
    let writtenLambda (e: Expression) (parameters: (Identifier * TypeExpr option) list) (body: Expression) =
        let plain () = parameters |> List.map (fun parameter -> parameter, None), body
        match body.Kind with
        | Match(scrutinees, [ patterns, inner ]) when sourceText.Valid e.Span && body.Span = e.Span && scrutinees.Length = patterns.Length ->
            let taken =
                List.zip scrutinees patterns
                |> List.choose (fun (scrutinee, p) ->
                    match scrutinee.Kind with
                    | IdentifierExpr n -> Some(n, p)
                    | _ -> None)
                |> Map.ofList
            let written =
                parameters |> List.mapi (fun index (n, t) -> (n, t), (if n = patternArgument (index + 1) then Map.tryFind n taken else None))
            if taken.Count = scrutinees.Length && (List.choose snd written).Length = taken.Count then written, inner else plain ()
        | _ -> plain ()

    /// A lambda. With `bodyOnOwnLine`, a body that is an expression goes on a line of its own after the `->`, as the
    /// one statement of a block (see `defDoc`).
    let rec lambdaDoc (e: Expression) (parameters: (Identifier * TypeExpr option) list) (body: Expression) (bodyOnOwnLine: bool) : Doc =
        match shorthand e parameters body with
        | Some doc -> doc
        | None ->
            let parameters, body = writtenLambda e parameters body
            let takesUnit = match e.Kind with Lambda(_, _, takesUnit) -> takesUnit | _ -> false
            // A result annotation is written after the parameters (see `LambdaResultSyntax`).
            let resultDoc, body =
                match body.Kind with
                | LambdaResultSyntax.Annotated(resultType, inner) when takesUnit || not parameters.IsEmpty -> cat [ text ": "; typeExpr resultType ], inner
                | _ -> Doc.empty, body
            let head =
                match parameters with
                | [] when takesUnit -> cat [ text "{ ()"; resultDoc; text " ->" ]
                | [] -> text "{"
                | _ -> cat [ text "{ "; commaJoin (List.map parameterDoc parameters); resultDoc; text " ->" ]
            // A body on its own line is read as a statement, which can't take a range, so where the layout is ours to
            // choose, a body that would need parentheses there stays on the `->`'s line instead, as does a block of just
            // that body in parentheses.
            let bracketedAsStatement (x: Expression) = needsParentheses statementPosition x && not (needsParentheses full x)
            let body =
                match body.Kind with
                | Block [ { Kind = ExprStatement single } ] when
                    bodyOnOwnLine && bracketedAsStatement single && not (sourceText.Valid e.Span && hasCommentsIn e.Span.Start e.Span.End) -> single
                | _ -> body
            match body.Kind with
            // The statements of a lambda are read regardless of lines and indentation, so the comments anywhere between
            // its braces go among them.
            | Block statements when isWrittenBlock body ->
                cat [ head; nest (cat [ hard; parts.StatementsInSequence true e.Span.Start e.Span.End statements ]); hard; text "}" ]
            | _ when bodyOnOwnLine && not (bracketedAsStatement body) -> cat [ head; nest (cat [ hard; expr statementPosition body ]); hard; text "}" ]
            | _ ->
                let bodyDoc = expr full body
                // A body that starts like a shorthand lambda (`{ - 1 }`, `{ .name }`) needs the arrow.
                let head =
                    let first = (Doc.render Int32.MaxValue bodyDoc).TrimStart()
                    if List.isEmpty parameters && not takesUnit && first.Length > 0 && "*/+-<>=!.|".Contains first[0] then text "{ ->" else head
                // A body that spans lines is indented once, and the `}` goes back to the lambda's line's indent, so
                // the braces of `{ x -> proc {` ... `}` do not end up one above the other.
                Doc.group (cat [ head; text " "; nest bodyDoc; Doc.line; text "}" ])

    /// A lambda's parameter, with the pattern it was written as, if any (see `writtenLambda`). An annotated parameter is
    /// bracketed, so an arrow after its annotation is not read as part of it.
    and parameterDoc ((n, typeOpt), patternOpt) =
        match patternOpt, typeOpt with
        | Some p, _ -> pattern p
        // A parameter that takes a cell is written as it was: `mut cell`, or `mut cell: int` (see `CellPassingSyntax`).
        | None, CellPassingSyntax.CellParameter None -> text $"mut {Names.written n}"
        | None, CellPassingSyntax.CellParameter (Some held) -> cat [ text $"mut {Names.written n}: "; typeExpr held ]
        | None, Some t -> cat [ text "("; text n; text ": "; typeExpr t; text ")" ]
        | None, None -> text n

    /// The shorthand lambdas the parser expands: `{ }`, `{ * }`, a body that leaves out its first operand (`{ * 2 }`,
    /// `{ .name }`, which the implicit argument is printed as nothing in), `{ | pattern -> ... }` and
    /// `{ m -> | pattern -> ... }`.
    and shorthand (e: Expression) parameters (body: Expression) : Doc option =
        let made (x: Expression) = sourceText.Valid e.Span && x.Span = e.Span
        match parameters, body.Kind with
        | [], UnitExpr when made body -> Some(text "{ }")
        | [ (first, None); (second, None) ], BinaryOp(op, _, l, r) when
            first = implicitArgument && second = implicitSecondArgument && l.Kind = IdentifierExpr first && r.Kind = IdentifierExpr second ->
            Some(text $"{{ {op} }}")
        | [ (only, None) ], _ when only = implicitArgument ->
            Some(Doc.group (cat [ text "{ "; nest (expr full body); Doc.line; text "}" ]))
        // `{ | ... }` makes up its parameters and their scrutinees at the lambda; `{ m, f -> | ... }` makes up the
        // scrutinees of the parameters it names at its arms.
        | _, Match(scrutinees, arms) when
            made body
            && not parameters.IsEmpty
            && scrutinees.Length = parameters.Length
            && List.forall2 (fun (s: Expression) (n, _) -> s.Kind = IdentifierExpr n) scrutinees parameters
            && (if List.forall made scrutinees then parameters = List.init parameters.Length (fun i -> $"arg{i + 1}", None)
                else parameters |> List.indexed |> List.forall (fun (i, (n, _)) -> n <> patternArgument (i + 1)))
            ->
            let head =
                if List.forall made scrutinees then text "{"
                else cat [ text "{ "; commaJoin (parameters |> List.map (fun parameter -> parameterDoc (parameter, None))); text " ->" ]
            let armsDoc = parts.ArmsInSequence e.Span.Start e.Span.End arms
            let single = (List.distinctBy (fun (_, b: Expression) -> b.Id) arms).Length = 1
            if single && not (Doc.hasHardLine armsDoc) then Some(cat [ head; text " "; armsDoc; text " }" ])
            else Some(cat [ head; nest (cat [ hard; armsDoc ]); hard; text "}" ])
        | _ -> None

    member _.WrittenLambda e parameters body = writtenLambda e parameters body

    member _.LambdaDoc e parameters body bodyOnOwnLine = lambdaDoc e parameters body bodyOnOwnLine
