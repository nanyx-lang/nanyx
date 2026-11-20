/// The recursive-descent parser's patterns, read from the layout pass's tokens, trying the alternatives in order. A
/// guard's lambda and a custom pattern's arguments are expressions, read through `Forward`.
module Parser.Program.DescentPatterns

open Parser.Program.Lexer
open Parser.Program.Layout
open Parser.Program.DescentCursor
open Parser.Program.DescentLiterals

/// A name a pattern binds, noted as declared by the construct being read (see `Cursor.NoteName`).
let private boundName (c: Cursor) : Identifier * SourceSpan =
    let name, span = identifierWithSpan c
    // A qualified name, `m.x`, isn't a pattern.
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
    // `else` is read straight off the front of a longer name, `elsewhere`, which is a mistake.
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
        if c.Is StringStart then c.GiveUp "a string with holes in a pattern"
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
    noDollar c
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
    if c.Is LeftBrace then
        let lambda = Forward.lambda.Value c
        Pattern.at lambda.Span (GuardPattern lambda)
    else
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
    let bracketedPayload () = first c [ (fun () -> unitPattern c); (fun () -> recordMemberPattern c); parenPayload ]
    let payload () =
        if c.Adjacent && c.Is LeftParen then c.Attempt bracketedPayload
        else c.Attempt inlinePayload
    // A custom pattern's arguments are expressions in brackets, before its payload's if it has one (see
    // `ArgumentTagPattern`): one, or several as a tuple.
    let arguments () =
        if not (c.Adjacent && c.Is LeftParen) then raise NoMatch
        let open' = c.Take()
        let values = sepBy1 c (fun () -> Forward.expression.Value c) (fun () -> c.Expect Comma |> ignore)
        c.Expect RightParen |> ignore
        match values with
        | [ single ] -> single
        | several -> Expression.at (c.SpanFrom open'.Start) (TupleExpr several)
    let kind =
        if PatternSyntax.isPatternTag tagName then
            let withPayload () =
                let args = arguments ()
                if not (c.Adjacent && c.Is LeftParen) then raise NoMatch
                ArgumentTagPattern(tagName, args, Some(bracketedPayload ()))
            let payloadOnly () =
                let payload = payload ()
                if c.Is LeftParen then raise NoMatch
                TagPattern(tagName, payload)
            first c [ withPayload; payloadOnly; (fun () -> ArgumentTagPattern(tagName, arguments (), None)) ]
        else TagPattern(tagName, payload ())
    Pattern.at (c.SpanFrom start) kind

/// One pattern, not an and-pattern.
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
          fun () -> identifierPattern c
          fun () -> c.Fail "a pattern" ]

/// A pattern: single patterns joined by `&`, an and-pattern.
and pattern (c: Cursor) : Pattern =
    let ampersand () = if c.Across(fun c -> c.Is Ampersand) then c.Take() |> ignore else raise NoMatch
    match sepBy1 c (fun () -> singlePattern c) ampersand with
    | [ single ] -> single
    | parts -> Pattern.at (SourceSpan.between (List.head parts).Span (List.last parts).Span) (AndPattern parts)
