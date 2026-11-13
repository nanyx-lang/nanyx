/// Destructuring defs, `def (x, y) = value` and `def Money(pence) = m`, desugared into plain defs as they are parsed.
[<AutoOpen>]
module Parser.Program.Destructuring

open FParsec
open Parser.Program

// `def (x, y) = value`, or `def x, y = value` without the brackets, binds each element of a tuple. It is desugared
// in the parser into a binding of the whole value plus one `def` per element (`def x = __tuple.0`), so the rest of
// the compiler only ever sees plain defs. This form takes only names, `_` and nested tuples, which cannot fail to
// match; any other pattern is bound through a match instead (see `matchingBindings`).
let private destructuringBindings (position: Position) (pattern: Pattern) (value: Expression) : (Identifier * Expression) list option =
    let temp = DestructuringSyntax.temp position.Line position.Column
    let rec bind (pattern: Pattern) (source: Expression) =
        match pattern.Kind with
        | IdentifierPattern name -> Some [ name, source ]
        | WildcardPattern -> Some []
        | TuplePattern elements ->
            elements
            |> List.mapi (fun index element -> bind element (Expression.from value (MemberAccess(source, string index, None))))
            |> List.fold (fun acc next ->
                match acc, next with
                | Some bound, Some more -> Some (bound @ more)
                | _ -> None) (Some [])
        | _ -> None
    bind pattern (Expression.from value (IdentifierExpr temp))
    |> Option.map (fun bindings -> (temp, value) :: bindings)

// `def pattern = value` with any other pattern (`def Money(pence) = m`, `def [first, ...rest] = xs`,
// `def #some(x) = get(key)`) is a match with one arm: the whole value is bound to a temporary under the name
// the tuple form uses, as the match's result, which is the tuple of the names the pattern binds, and then one
// `def` per name takes its element. Whether the pattern is total is for the type checker to say, as it does
// for any other match.
let private matchingBindings (position: Position) (pattern: Pattern) (names: Identifier list) (value: Expression) : Expression * (Identifier * Expression) list =
    let temp = DestructuringSyntax.temp position.Line position.Column
    let made kind = Expression.from value kind
    let body =
        match names with
        | [] -> made UnitExpr
        | [ single ] -> made (IdentifierExpr single)
        | several -> made (TupleExpr (several |> List.map (fun name -> made (IdentifierExpr name))))
    let matched = made (Match([ value ], [ [ pattern ], body ]))
    let bindings =
        match names with
        | [ single ] -> [ single, made (IdentifierExpr temp) ]
        | several -> several |> List.mapi (fun index name -> name, made (MemberAccess(made (IdentifierExpr temp), string index, None)))
    body, (temp, matched) :: bindings

/// `[export] def pattern = value`, written from `lineStart`, as the export flag and the bindings it stands for. A bare
/// name is not a pattern here: that is an ordinary def.
let destructuringDefWith (lineStart: Position) (exprParser: Parser<Expression, ParseState>) : Parser<bool * (Identifier * Expression) list, ParseState> =
    let lineStartCol = lineStart.Column
    // `def x, y = value` is `def (x, y) = value`: patterns separated by commas are the elements of a tuple.
    let patterns =
        withSpan (sepBy1 pattern (pstring "," .>> ws))
        |>> fun (elements, span) ->
            match elements with
            | [ single ] -> single
            | several -> Pattern.at span (TuplePattern several)
    pipe3
        (opt (attempt (pstring "export" >>. ws)))
        (getPosition .>>. (pstring "def" >>. ws >>. collectingNames patterns .>> wsInline))
        (defEqualsContinuation lineStartCol >>. pstring "=" >>. wsInline >>. exprParser)
        (fun exportOpt (position, pattern) value -> exportOpt.IsSome, position, pattern, value)
    >>= fun (isExport, position, (pattern, names), value) ->
        // Each name is declared by the value of the def it becomes.
        // A doc comment before it documents the first name it binds.
        let declareBindings (bindings: (Identifier * Expression) list) =
            bindings
            |> List.map (fun (name, bound) -> declare DefValue bound.Id (names |> List.filter (fun (declared, _) -> declared = name)))
            |> List.fold (>>.) (preturn ())
            >>. (match names with (_, first) :: _ -> noteDocumentable lineStart first.Start | [] -> preturn ())
        match pattern.Kind with
        | IdentifierPattern _ -> fail "a destructuring def needs a pattern, not a name"
        | _ ->
            match destructuringBindings position pattern value with
            | Some bindings -> declareBindings bindings >>% (isExport, bindings)
            | None ->
                let boundNames = names |> List.map fst |> List.distinct
                let body, bindings = matchingBindings position pattern boundNames value
                // The arm's body declares the names the pattern binds, as any match arm's does.
                declare ArmBody body.Id names >>. declareBindings bindings >>% (isExport, bindings)
