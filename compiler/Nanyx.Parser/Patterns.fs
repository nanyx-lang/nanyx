[<AutoOpen>]
module Parser.Program.Patterns

open System
open FParsec
// After the libraries, so that the parser's names shadow theirs (`identifier`, `Attribute`) as they did in one file.
open Parser.Program

// `_` alone: `_rest` is a name, which says it is unused on purpose.
let wildcardPattern = spannedPattern (pstring "_" .>> notFollowedBy (satisfy (fun c -> Char.IsLetterOrDigit c || c = '_')) >>% WildcardPattern)
let elsePattern = spannedPattern (pstring "else" >>% ElsePattern)
let identifierPattern =
    spannedPattern (getPosition .>>. identifierNoWs >>= fun (start, name) -> noteName start name >>% IdentifierPattern name)

// Parse either a range pattern (1..10), an integer literal pattern (1), 
// or other literal types (float, bool, string)
// NOTE: For ranges, we only allow INTEGER literals to avoid ambiguity with floats like "1."
let literalOrRangePattern =
    spannedPattern <| choice [
        // Try integer with optional range
        attempt (
            withSpan intLiteralNoWs >>= fun (startLit, startSpan) ->
            opt (pstring ".." >>. ws >>. withSpan intLiteralNoWs) >>= fun endLitOpt ->
            match endLitOpt with
            | Some (endLit, endSpan) ->
                preturn (RangePattern(Expression.at startSpan (LiteralExpr startLit), Expression.at endSpan (LiteralExpr endLit)))
            | None -> preturn (LiteralPattern startLit)
        )
        // Or any other literal type
        literalNoWs |>> LiteralPattern
    ]

// Tuple pattern: (pat1, pat2, ...)
let tuplePattern =
    withSpan (
        between
            (pstring "(")
            (pstring ")")
            (sepBy1 (ws >>. pattern) (pstring "," .>> ws)))
    |>> fun (patterns, span) ->
        match patterns with
        | [single] -> single  // Single pattern in parens is just that pattern, not a tuple
        | multiple -> Pattern.at span (TuplePattern multiple)

// Record pattern by position: TypeName(pat1, pat2, ...). A nominal type's name takes its value apart:
// `$Email(raw)` binds the string an `$Email` wraps.
let recordPatternByPosition =
    spannedPattern <| pipe2
        typeNameIdentifier
        (between
            (pstring "(")
            (pstring ")")
            (sepBy1 (ws >>. pattern) (pstring "," .>> ws)))
        (fun typeName patterns -> RecordPattern(typeName, patterns))

// Record pattern by member: (member1 = pat1, member2 = pat2, ...)
let recordMemberPattern =
    between
        (pstring "(")
        (pstring ")")
        (sepBy1
            (pipe2
                (ws >>. identifierNoWs .>> ws)
                (pstring "=" >>. ws >>. pattern)
                (fun name pat -> (name, pat)))
            (pstring "," .>> ws))
    |>> RecordMemberPattern
    |> spannedPattern

// List pattern: [pat1, pat2, ...] or [pat1, ...rest] or [first, ..., last]
let private listPatternKind =
    let splatPattern =
        withSpan (pstring "...") .>> ws
        .>>. opt (withSpan (getPosition .>>. identifierNoWs >>= fun (start, name) -> noteName start name >>% name))
        |>> fun ((_, dotsSpan), name) ->
            match name with
            | Some (id, span) -> Splat (Pattern.at span (IdentifierPattern id))
            | None -> Splat (Pattern.at dotsSpan WildcardPattern)
    
    let regularPatternOrSplat =
        (attempt splatPattern) <|> (pattern |>> RegularPattern)
    
    between
        (pstring "[")
        (pstring "]")
        (sepBy (ws >>. regularPatternOrSplat) (pstring "," .>> ws))
    |>> fun elements ->
        // Find where the splat is (if any)
        let splatIndex = elements |> List.tryFindIndex (fun e -> 
            match e with Splat _ -> true | _ -> false)
        
        match splatIndex with
        | None ->
            // No splat - regular list pattern
            let patterns = elements |> List.map (fun e -> 
                match e with 
                | RegularPattern p -> p 
                | _ -> failwith "unexpected")
            ListPattern(patterns, None)
        | Some idx when idx = elements.Length - 1 ->
            // Splat at end: [a, b, ...rest] or [a, b, ...]
            let beforeSplat = elements |> List.take idx |> List.map (fun e ->
                match e with 
                | RegularPattern p -> p 
                | _ -> failwith "unexpected")
            // `...` with no name keeps a WildcardPattern rest, so it still matches lists of any length
            let rest =
                match elements.[idx] with
                | Splat rest -> rest
                | _ -> failwith "unexpected"
            ListPattern(beforeSplat, Some rest)
        | Some idx ->
            // Splat in middle: [first, ..., last]
            let beforeSplat = elements |> List.take idx |> List.map (fun e ->
                match e with 
                | RegularPattern p -> p 
                | _ -> failwith "unexpected")
            let afterSplat = elements |> List.skip (idx + 1) |> List.map (fun e ->
                match e with 
                | RegularPattern p -> p 
                | _ -> failwith "unexpected")
            ListSplatMiddle(beforeSplat, afterSplat)

let listPattern = spannedPattern listPatternKind

// Guard pattern: any lambda is a predicate on the value matched (the pattern-matching page: "arbitrary function
// bodies"), most often a shorthand one, `{ < 0 }`, `{ % 2 == 0 }` or `{ .length > 5 }`. The older bare form `> 5`
// is still accepted, and is the shorthand lambda `{ > 5 }`, its argument made up at the operator.
let guardPattern =
    let guardOp =
        choice [
            pstring ">="
            pstring "<="
            pstring "=="
            pstring "!="
            pstring ">"
            pstring "<"
        ]
    let bare =
        attempt (
            withSpan guardOp .>>. (ws >>. spanned (literalNoWs |>> LiteralExpr))
            |>> fun ((op, opSpan), value) ->
                let argument = Expression.at opSpan (IdentifierExpr implicitArgument)
                let body = Expression.at (SourceSpan.between opSpan value.Span) (BinaryOp(op, argument, value))
                Expression.at body.Span (Lambda([ (implicitArgument, None) ], body)))
    spannedPattern (lambda <|> bare |>> GuardPattern)

/// `()`, the unit value.
let unitPattern = spannedPattern (pstring "(" >>. ws >>. pstring ")" >>% UnitPattern)

// Tag pattern: #nil or #some(x) or #error data
// Tag pattern: #tagName or #tagName(pattern) or #tagName(p1, p2, ...)
let tagPattern =
    attempt (spannedPattern (
        (pstring "#" >>. identifierNoWs) >>= fun tagName ->
        let parenPayload =
            withSpan (
                between
                    (pstring "(")
                    (pstring ")")
                    (sepBy1 (ws >>. pattern) (pstring "," .>> ws)))
        let inlinePayload =
            skipMany1 (skipAnyOf " \t") >>. pattern |>> fun p -> [p], p.Span
        // Empty brackets, `#some()`, carry the unit value, as they do in a value.
        let unitPayload = unitPattern |>> fun p -> [p], p.Span
        opt (attempt unitPayload <|> attempt parenPayload <|> attempt inlinePayload) >>= fun patternsOpt ->
        match patternsOpt with
        | None -> preturn (TagPattern(tagName, None))
        | Some ([single], _) -> preturn (TagPattern(tagName, Some single))
        // Several payload patterns match the payload as a tuple, written as the parenthesised list.
        | Some (multiple, span) -> preturn (TagPattern(tagName, Some (Pattern.at span (TuplePattern multiple))))
    ))

/// Fills `pattern`. Called by `Api`: a file's top-level code runs only when one of its own values is first used.
let internal fillPatternRef () =
    patternRef :=
        choice [
            attempt elsePattern         // "else" must come before identifiers
            attempt tagPattern          // "#tag" must come before literals/identifiers
            attempt guardPattern        // "> 5" must come before operators in other contexts
            attempt literalOrRangePattern  // Handles both literals and ranges (1 or 1..10)
            attempt recordPatternByPosition  // Must come before identifierPattern
            attempt recordMemberPattern
            attempt listPattern
            attempt unitPattern         // "()" must come before tuples
            attempt tuplePattern
            attempt wildcardPattern
            identifierPattern           // Last resort - any identifier
        ] .>> ws
        <?> "pattern"
