/// The recursive-descent parser's patterns: the grammar of `Patterns.fs`, read from the layout pass's tokens, trying the
/// alternatives in the order `fillPatternRef` lists them. It gives up at a pattern that holds an expression other than a
/// literal (a guard's lambda, a custom pattern's arguments), which the expression parser will read once it is written
/// (see the Work in progress page "The parser"), and at text the FParsec parser reads only to fail later.
module Parser.Program.DescentPatterns

open Parser.Program.Lexer
open Parser.Program.Layout
open Parser.Program.DescentCursor
open Parser.Program.DescentLiterals

/// A name a pattern binds, noted as declared by the construct being read (see `Cursor.NoteName`).
let private boundName (c: Cursor) : Identifier * SourceSpan =
    let name, span = identifierWithSpan c
    // The FParsec parser reads `m.x` as the name `m`, and then fails at the `.`.
    if c.Adjacent && (c.Is Dot || c.Is DotDot || c.Is Ellipsis) then c.GiveUp "a pattern's name with a `.` straight after it"
    c.NoteName name span
    name, span

/// The first of `alternatives` that matches, each tried with the cursor put back after one that doesn't.
let private first (c: Cursor) (alternatives: (unit -> 'a) list) : 'a =
    alternatives
    |> List.tryPick (fun read -> c.Attempt read)
    |> Option.defaultWith (fun () -> raise NoMatch)

/// `items` separated by `,`, as `sepBy1 (ws >>. pattern) (pstring "," .>> ws)` reads them.
let private commaSeparated (c: Cursor) (item: unit -> 'a) : 'a list =
    sepBy1 c item (fun () -> c.Expect Comma |> ignore)

/// `else`, a match's last arm.
let private elsePattern (c: Cursor) =
    let token = c.Expect(Keyword, "else")
    Pattern.at (c.SpanOf token) ElsePattern

/// `_` alone: `_rest` is a name.
let private wildcardPattern (c: Cursor) =
    let token = c.Expect(Name, "_")
    Pattern.at (c.SpanOf token) WildcardPattern

let private identifierPattern (c: Cursor) =
    match c.Token with
    // The FParsec parser reads `else` straight off the front of a longer name, `elsewhere`, and then fails.
    | Some token when token.Kind = Name && (c.Text token).StartsWith "else" -> c.GiveUp "a name that starts with `else`"
    | _ ->
        let name, span = boundName c
        Pattern.at span (IdentifierPattern name)

/// An integer and `..` and another, a range, or a literal alone. Only integers make ranges, so that `1.` is a float.
let private literalOrRangePattern (c: Cursor) =
    let start = c.Start
    let integerOrRange () =
        let low, lowSpan = intLiteral c
        if c.Adjacent && c.Is DotDot then
            c.Take() |> ignore
            let high, highSpan = intLiteral c
            Pattern.at (c.SpanFrom start) (RangePattern(Expression.at lowSpan (LiteralExpr low), Expression.at highSpan (LiteralExpr high)))
        else Pattern.at (c.SpanFrom start) (LiteralPattern low)
    match c.Attempt integerOrRange with
    | Some pattern -> pattern
    | None ->
        if c.Is StringStart then c.GiveUp "a string with holes in a pattern, which the FParsec parser reads as text"
        let literal, _ = literal c
        Pattern.at (c.SpanFrom start) (LiteralPattern literal)

/// `(p1, p2, ...)`: a tuple, or one pattern in brackets, which is that pattern.
let rec private tuplePattern (c: Cursor) =
    let start = c.Start
    c.Expect LeftParen |> ignore
    let patterns = commaSeparated c (fun () -> pattern c)
    c.Expect RightParen |> ignore
    match patterns with
    | [ single ] -> single
    | multiple -> Pattern.at (c.SpanFrom start) (TuplePattern multiple)

/// `Name(p1, p2, ...)`: a record's fields by position, or what a nominal type wraps.
and private recordPatternByPosition (c: Cursor) =
    let start = c.Start
    if c.Is Dollar then c.GiveUp "a `$` before a type's name, which is a mistake"
    let typeName, _ = identifierWithSpan c
    if not c.Adjacent then raise NoMatch
    c.Expect LeftParen |> ignore
    let patterns = commaSeparated c (fun () -> pattern c)
    c.Expect RightParen |> ignore
    Pattern.at (c.SpanFrom start) (RecordPattern(typeName, patterns))

/// `(name = p, ...)`: a record's fields by name, ending in `...` to leave the others out.
and private recordMemberPattern (c: Cursor) =
    let start = c.Start
    c.Expect LeftParen |> ignore
    let memberPattern () =
        let name, nameSpan = identifierWithSpan c
        c.Expect Equals |> ignore
        let value = pattern c
        c.Declare NamedFieldValue value.Id [ name, nameSpan ]
        name, value
    let firstMember = memberPattern ()
    let others = many c (fun () -> c.Expect Comma |> ignore; memberPattern ())
    let hasRest = c.Attempt(fun () -> c.Expect Comma |> ignore; c.Expect Ellipsis |> ignore).IsSome
    c.Expect RightParen |> ignore
    Pattern.at (c.SpanFrom start) (RecordMemberPattern(firstMember :: others, hasRest))

/// `[p1, p2]`, `[p1, ...rest]`, `[first, ..., last]`.
and private listPattern (c: Cursor) =
    let start = c.Start
    c.Expect LeftBracket |> ignore
    let splat () =
        let dots = c.Expect Ellipsis
        match c.Attempt(fun () -> boundName c) with
        | Some(name, span) -> Splat(Pattern.at span (IdentifierPattern name))
        | None -> Splat(Pattern.at (c.SpanOf dots) WildcardPattern)
    let element () =
        match c.Attempt splat with
        | Some element -> element
        | None -> RegularPattern(pattern c)
    let elements = if c.Is RightBracket then [] else commaSeparated c element
    c.Expect RightBracket |> ignore
    let regular elements = elements |> List.map (function RegularPattern p -> p | Splat _ -> c.GiveUp "a list pattern with two splats")
    let kind =
        match elements |> List.tryFindIndex (function Splat _ -> true | RegularPattern _ -> false) with
        | None -> ListPattern(regular elements, None)
        | Some index when index = elements.Length - 1 ->
            let rest = match elements[index] with Splat rest -> rest | RegularPattern p -> p
            ListPattern(regular (List.take index elements), Some rest)
        | Some index -> ListSplatMiddle(regular (List.take index elements), regular (List.skip (index + 1) elements))
    Pattern.at (c.SpanFrom start) kind

/// A guard: a lambda, which is an expression, or the older bare form, `> 5`, which is the shorthand lambda `{ > 5 }`.
and private guardPattern (c: Cursor) =
    if c.Is LeftBrace then c.GiveUp "a guard, which is a lambda"
    let isGuardOperator text = List.contains text [ ">="; "<="; "=="; "!="; ">"; "<" ]
    match c.Token with
    | Some token when token.Kind = Operator && isGuardOperator (c.Text token) ->
        c.Take() |> ignore
        let opSpan = c.SpanOf token
        let literal, literalSpan = literal c
        let argument = Expression.at opSpan (IdentifierExpr implicitArgument)
        let value = Expression.at literalSpan (LiteralExpr literal)
        let body = Expression.at (SourceSpan.between opSpan value.Span) (BinaryOp(c.Text token, Some opSpan, argument, value))
        let lambda = Expression.at body.Span (Lambda([ (implicitArgument, None) ], body, false))
        Pattern.at (c.SpanFrom token.Start) (GuardPattern lambda)
    | _ -> raise NoMatch

/// `()`, the unit value.
and private unitPattern (c: Cursor) =
    let start = c.Start
    c.Expect LeftParen |> ignore
    c.Expect RightParen |> ignore
    Pattern.at (c.SpanFrom start) UnitPattern

/// `#tag`, with its payload in brackets straight after it or one pattern after a space: `#some(x)`, `#error e`.
and private tagPattern (c: Cursor) =
    let start = c.Start
    c.Expect Hash |> ignore
    if not c.Adjacent then raise NoMatch
    let tagName, _ = identifierWithSpan c
    let parenPayload () =
        let open' = c.Expect LeftParen
        let patterns = commaSeparated c (fun () -> pattern c)
        c.Expect RightParen |> ignore
        match patterns with
        | [ single ] -> single
        // Several payload patterns match the payload as a tuple, written as the parenthesised list.
        | multiple -> Pattern.at (c.SpanFrom open'.Start) (TuplePattern multiple)
    // One pattern after spaces or tabs on the same line, and not a keyword: `if x is #zero then ...`.
    let inlinePayload () =
        let gap = c.Source.Substring(c.LastEnd, c.Start - c.LastEnd)
        if gap = "" || gap |> Seq.exists (fun ch -> ch <> ' ' && ch <> '\t') || c.Is Keyword then raise NoMatch
        singlePattern c
    let bracketed = c.Adjacent && c.Is LeftParen
    let payload =
        if bracketed then
            c.Attempt(fun () -> first c [ (fun () -> unitPattern c); (fun () -> recordMemberPattern c); parenPayload ])
        else c.Attempt inlinePayload
    // A custom pattern's arguments come in brackets before its payload's (see `ArgumentTagPattern`), and are
    // expressions: brackets that aren't a payload, or a second pair after it, are its arguments.
    if PatternSyntax.isPatternTag tagName && ((bracketed && payload.IsNone) || c.Is LeftParen) then
        c.GiveUp "a custom pattern's arguments, which are expressions"
    Pattern.at (c.SpanFrom start) (TagPattern(tagName, payload))

/// One pattern, not an and-pattern (`Patterns.fs`'s `singlePattern`).
and singlePattern (c: Cursor) : Pattern =
    first
        c
        [ fun () -> elsePattern c
          fun () -> tagPattern c
          fun () -> guardPattern c
          fun () -> literalOrRangePattern c
          fun () -> recordPatternByPosition c
          fun () -> recordMemberPattern c
          fun () -> listPattern c
          fun () -> unitPattern c
          fun () -> tuplePattern c
          fun () -> wildcardPattern c
          fun () -> identifierPattern c ]

/// A pattern: single patterns joined by `&`, an and-pattern (`Patterns.fs`'s `pattern`).
and pattern (c: Cursor) : Pattern =
    let ampersand () = if c.Across(fun c -> c.Is Ampersand) then c.Take() |> ignore else raise NoMatch
    match sepBy1 c (fun () -> singlePattern c) ampersand with
    | [ single ] -> single
    | parts -> Pattern.at (SourceSpan.between (List.head parts).Span (List.last parts).Span) (AndPattern parts)
