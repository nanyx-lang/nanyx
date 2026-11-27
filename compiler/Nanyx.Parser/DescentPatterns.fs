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

/// The first of `alternatives` that matches, each a reading and whether the token at the cursor lets it match, asked
/// just before it would be tried (see `Cursor.AttemptIf`).
let private firstThat (c: Cursor) (alternatives: ((unit -> bool) * (unit -> 'a)) list) : 'a =
    alternatives
    |> List.tryPick (fun (fits, read) -> c.AttemptIf(fits (), read))
    |> Option.defaultWith (fun () -> raise NoMatch)

/// `items` separated by `,`, as `sepBy1 (ws >>. pattern) (pstring "," .>> ws)` reads them.
let private commaSeparated (c: Cursor) (item: unit -> 'a) : 'a list =
    sepBy1If c item (fun () -> c.Expects Comma) (fun () -> c.Expect Comma |> ignore)

/// The operators a bare guard starts with, `> 5`.
let private isGuardOperator (text: string) = List.contains text [ ">="; "<="; "=="; "!="; ">"; "<" ]

/// Whether a name is at the cursor, as `identifierWithSpan` reads one: a name or a keyword.
let private nameNext (c: Cursor) = c.Is Name || c.Is Keyword

/// Whether `recordPatternByPosition` can read past the type's name at the cursor: a name with `(` straight after it.
/// Where there is a name without one, it would fail having taken the name, and this notes what it would have noted (see
/// `Cursor.AttemptIf`). A `$` is left to it, which reports one.
let private nominalNext (c: Cursor) =
    if c.Is Dollar then true
    elif not (nameNext c) then false
    elif not (c.AdjacentAt 1) then
        c.NoteAt 1
        false
    // What is straight after the name is read as a record member pattern, then as patterns by position, each of which
    // expects `(` first.
    elif c.IsAt(1, LeftParen) then true
    else
        c.NoteAt(1, "`(`")
        false

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
        match literal with
        | UnitLit _ -> c.Report start "A number with a unit is not a pattern yet: compare the value in a guard instead, as in `| == 0ms ->`"
        | _ -> ()
        Pattern.at (c.SpanFrom start) (LiteralPattern literal)

/// `, ...` after the elements of a tuple pattern, which leaves the value's other fields out: whether it is there.
let private rest (c: Cursor) =
    c.AttemptIf(c.Expects Comma, fun () -> c.Expect Comma |> ignore; c.Expect Ellipsis |> ignore).IsSome

/// `(p1, p2)`: a tuple, or one pattern in brackets, which is that pattern. `(p1, ...)` ends in `...` to leave the other
/// fields out, even after one pattern.
let rec private tuplePattern (c: Cursor) =
    let start = c.Start
    c.Expect LeftParen |> ignore
    let patterns = commaSeparated c (fun () -> pattern c)
    let hasRest = rest c
    c.Expect RightParen |> ignore
    match patterns with
    | [ single ] when not hasRest -> single
    | multiple -> Pattern.at (c.SpanFrom start) (TuplePattern(multiple, hasRest))

/// `Name(p1, p2)`: a nominal value taken apart, its representation matched by the patterns in its brackets, which
/// read as a tag's payload's do: `Name(a, ...)` and `Name(field = p)` are one pattern, a tuple pattern that leaves
/// the other fields out and a record member pattern (see `RecordPattern`).
and private recordPatternByPosition (c: Cursor) =
    let start = c.Start
    noDollar c
    let typeName, _ = identifierWithSpan c
    if not c.Adjacent then raise NoMatch
    let byPosition () =
        let open' = c.Expect LeftParen
        let patterns = commaSeparated c (fun () -> pattern c)
        let hasRest = rest c
        c.Expect RightParen |> ignore
        if hasRest then [ Pattern.at (c.SpanFrom open'.Start) (TuplePattern(patterns, true)) ] else patterns
    let patterns =
        match c.Attempt(fun () -> recordMemberPattern c) with
        | Some byName -> [ byName ]
        | None -> byPosition ()
    Pattern.at (c.SpanFrom start) (RecordPattern(typeName, patterns))

/// `(name = p, ...)`: a record's fields by name, ending in `...` to leave the others out. A field may be named alone,
/// short for `name = name`, beside one that isn't: `(x, y = b)`. With none that is, the brackets are a tuple pattern,
/// `(x, y)`, which takes a record's fields by name too (see `TuplePattern`).
and private recordMemberPattern (c: Cursor) =
    let start = c.Start
    c.Expect LeftParen |> ignore
    let memberPattern () =
        match c.Attempt(fun () -> let named = identifierWithSpan c in c.Expect Equals |> ignore; named) with
        | Some (name, nameSpan) ->
            let value = pattern c
            c.Declare NamedFieldValue value.Id [ name, nameSpan ]
            name, value, true
        | None ->
            let name, nameSpan = boundName c
            if name = "_" then raise NoMatch
            name, Pattern.at nameSpan (IdentifierPattern name), false
    let firstMember = memberPattern ()
    let others = manyIf c (fun () -> c.Expects Comma) (fun () -> c.Expect Comma |> ignore; memberPattern ())
    let members = firstMember :: others
    if members |> List.forall (fun (_, _, renamed) -> not renamed) then raise NoMatch
    let hasRest = rest c
    c.Expect RightParen |> ignore
    Pattern.at (c.SpanFrom start) (RecordMemberPattern(members |> List.map (fun (name, value, _) -> name, value), hasRest))

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
        match c.AttemptIf(c.Expects Ellipsis, splat) with
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
        let hasRest = rest c
        c.Expect RightParen |> ignore
        match patterns with
        | [ single ] when not hasRest -> single
        // Several payload patterns match the payload as a tuple, written as the parenthesised list.
        | multiple -> Pattern.at (c.SpanFrom open'.Start) (TuplePattern(multiple, hasRest))
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
        let values = sepBy1If c (fun () -> Forward.expression.Value c) (fun () -> c.Expects Comma) (fun () -> c.Expect Comma |> ignore)
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

/// Whether the pattern `offset` from the cursor is a name alone, which `pattern` reads as an `IdentifierPattern`. A
/// reading that wants another pattern there, a destructuring def's or a pattern parameter's, would fail once it had
/// read it, and is not tried: where the pattern is a name alone, this notes what reading it would have noted, then each
/// of `after`, what the reading expects after it (see `Cursor.AttemptIf`). With `commaJoins`, a `,` after the name
/// joins it to more patterns, a tuple. A name that `pattern` reads otherwise, or gives up at, is left to the reading.
and loneNameAt (c: Cursor) (offset: int) (commaJoins: bool) (after: string list) =
    let text = match c.At offset with Code token -> c.Text token | _ -> ""
    let next = offset + 1
    let mutable across = next
    while (match c.At across with Newline _ | Indent _ -> true | _ -> false) do
        across <- across + 1
    let lone =
        (c.IsAt(offset, Name) || c.IsAt(offset, Keyword))
        && not (List.contains text [ "true"; "false"; "_" ] || text.StartsWith "else")
        && not (c.AdjacentAt next && (c.IsAt(next, LeftParen) || c.IsAt(next, Dot) || c.IsAt(next, DotDot) || c.IsAt(next, Ellipsis)))
        && not (c.IsAt(across, Ampersand))
        && not (commaJoins && c.IsAt(next, Comma))
    if lone then
        if c.AdjacentAt next then c.NoteAt(next, "`(`") else c.NoteAt next
        for description in after do
            c.NoteAt(next, description)
    lone

/// One pattern, not an and-pattern.
and singlePattern (c: Cursor) : Pattern =
    let guardNext () =
        c.Is LeftBrace || (match c.Token with Some token -> token.Kind = Operator && isGuardOperator (c.Text token) | None -> false)
    // A literal is an integer, which `numberNext` asks for, or else one of `literal`'s: a number, `true` or `false`, or
    // a string, which ends in `Expect StringLiteral`.
    let literalNext () =
        c.Token.IsSome && (numberNext c || c.Is(Name, "true") || c.Is(Name, "false") || c.Is StringStart || c.Expects StringLiteral)
    firstThat
        c
        [ (fun () -> c.Expects(Keyword, "else")), (fun () -> elsePattern c)
          (fun () -> c.StartsWith Hash), (fun () -> tagPattern c)
          guardNext, (fun () -> guardPattern c)
          literalNext, (fun () -> literalOrRangePattern c)
          (fun () -> nominalNext c), (fun () -> recordPatternByPosition c)
          (fun () -> c.StartsWith LeftParen), (fun () -> recordMemberPattern c)
          (fun () -> c.StartsWith LeftBracket), (fun () -> listPattern c)
          (fun () -> c.StartsWith LeftParen), (fun () -> unitPattern c)
          (fun () -> c.StartsWith LeftParen), (fun () -> tuplePattern c)
          (fun () -> c.Expects(Name, "_")), (fun () -> wildcardPattern c)
          (fun () -> true), (fun () -> identifierPattern c)
          (fun () -> true), (fun () -> c.Fail "a pattern") ]

/// A pattern: single patterns joined by `&`, an and-pattern.
and pattern (c: Cursor) : Pattern =
    let ampersand () = if c.Across(fun c -> c.Is Ampersand) then c.Take() |> ignore else raise NoMatch
    match sepBy1If c (fun () -> singlePattern c) (fun () -> c.AcrossFits(fun c -> c.Is Ampersand)) ampersand with
    | [ single ] -> single
    | parts -> Pattern.at (SourceSpan.between (List.head parts).Span (List.last parts).Span) (AndPattern parts)
