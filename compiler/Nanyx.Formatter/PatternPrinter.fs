/// Printing patterns: a match arm's, a lambda parameter's, a destructuring def's.
module internal Nanyx.Formatter.PatternPrinter

open Parser.Program
open Nanyx.Formatter.SourceText

/// Prints patterns. A literal is printed by `literal`, and the expressions in a pattern (a range's ends, a guard's
/// predicate) by `expression`.
type PatternPrinter(source: string, sourceText: SourceText, literal: SourceSpan -> Literal -> Doc, expression: Expression -> Doc) =
    let text = Doc.text
    let cat = Doc.concat
    let commaJoin docs = Doc.join (text ", ") docs

    let rec pattern (p: Pattern) : Doc =
        match p.Kind with
        | LiteralPattern l -> literal p.Span l
        | IdentifierPattern n -> text n
        | WildcardPattern -> text "_"
        | UnitPattern -> text "()"
        | ElsePattern -> text "else"
        | TuplePattern ps -> cat [ text "("; commaJoin (List.map pattern ps); text ")" ]
        | RecordPattern(n, ps) -> cat [ text n; text "("; commaJoin (List.map pattern ps); text ")" ]
        | RecordMemberPattern(fields, hasRest) ->
            let restDoc = if hasRest then [ text "..." ] else []
            cat [ text "("; commaJoin ((fields |> List.map (fun (n, p) -> cat [ text n; text " = "; pattern p ])) @ restDoc); text ")" ]
        | ListPattern(ps, rest) ->
            let restDoc =
                match rest with
                | Some (Kind WildcardPattern) -> [ text "..." ]
                | Some r -> [ cat [ text "..."; pattern r ] ]
                | None -> []
            cat [ text "["; commaJoin (List.map pattern ps @ restDoc); text "]" ]
        | ListSplatMiddle(before, after) ->
            cat [ text "["; commaJoin (List.map pattern before @ [ text "..." ] @ List.map pattern after); text "]" ]
        | RangePattern(a, b) -> cat [ expression a; text ".."; expression b ]
        // The bare `> 5` is kept bare: it is the shorthand lambda `{ > 5 }` without its braces.
        | GuardPattern(Kind (Lambda([ (only, None) ], body, _))) when
            only = implicitArgument && sourceText.Valid p.Span && p.Span.Start < source.Length && source[p.Span.Start] <> '{' ->
            expression body
        | GuardPattern predicate -> expression predicate
        | AndPattern parts -> parts |> List.map pattern |> List.reduce (fun left right -> cat [ left; text " & "; right ])
        | TagPattern(n, None) -> text ("#" + n)
        | TagPattern(n, Some (Kind UnitPattern)) -> text $"#{n}()"
        | TagPattern(n, Some payload) ->
            let afterName = p.Span.Start + 1 + n.Length
            if sourceText.Valid p.Span && afterName < source.Length && source[afterName] <> '(' then
                match payload.Kind with
                // `#n (a & b)`: without its brackets, the and-pattern would take in the tag.
                | AndPattern _ -> cat [ text $"#{n} ("; pattern payload; text ")" ]
                | _ -> cat [ text $"#{n} "; pattern payload ]
            else
                match payload.Kind with
                | TuplePattern ps -> cat [ text $"#{n}("; commaJoin (List.map pattern ps); text ")" ]
                | _ -> cat [ text $"#{n}("; pattern payload; text ")" ]

    member _.Pattern(p: Pattern) : Doc = pattern p
