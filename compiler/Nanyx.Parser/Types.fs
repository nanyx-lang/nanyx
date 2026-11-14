[<AutoOpen>]
module Parser.Program.Types

open System
open FParsec
// After the libraries, so that the parser's names shadow theirs (`identifier`, `Attribute`) as they did in one file.
open Parser.Program

// Type expression parser
/// Fills `typeExpr`, `typeList`, `typeSignature` and `parameterType`. Called by `Api`: a file's top-level code runs only when one of
/// its own values is first used.
let internal fillTypeRefs () =
    // A type an aliased import brings in is qualified with the alias: `m.Email`, `m.Shape`.
    let qualifiedTypeName =
        pipe2
            typeNameIdentifier
            (many (attempt (pchar '.' >>. typeNameIdentifier)))
            (fun head tail -> String.concat "." (head :: tail))

    let typeIdentifier =
        spannedType (
            qualifiedTypeName |>> fun name ->
                if name = "_" then TypeHole
                elif Names.isTypeVariable name then TypeVar name
                else TypeName name)

    let typeMemberField =
        pipe2
            (pstring "type" >>. keywordEnd >>. ws >>. identifierNoWs .>> wsInline)
            (opt (attempt (pstring "=" >>. wsInline >>. typeExpr)))
            (fun name typeOpt -> TypeMember(name, typeOpt))

    // `name?: T` and `T?` are not Nanyx: a value that may be absent has its absence in its type, as everywhere else
    // (`#some(T) | #none`). They are reported, so that whoever writes them is told what to write instead.
    let noOptionalMarker (message: string) : Parser<unit, ParseState> =
        optional (getPosition .>> attempt (pchar '?' >>. notFollowedBy (pchar '?')) >>= fun position -> reportFocused position message)

    let noOptionalField (name: Identifier) =
        noOptionalMarker $"A field can't be marked optional with `?`: write one that may be absent as `{Names.written name}: #some(T) | #none`"

    // A named field's name is noted, for the record type it is in to declare.
    let typeField =
        let mutAndName =
            getPosition .>>. opt (attempt (pstring "mut" >>. keywordEnd >>. ws)) .>>. (getPosition .>>. nameNoWs)
            >>= fun ((fieldStart, mutOpt), (start, name)) ->
                noteName start name >>. noOptionalField name
                >>. (if mutOpt.IsSome then noteDocumentable fieldStart (int start.Index) else preturn ())
                >>% (mutOpt.IsSome, name, start)
        tuple3
            mutAndName
            (wsInline >>. (pstring ":" <|> pstring "::") >>. ws >>. typeExpr)
            (opt (attempt (ws >>. pstring "=" >>. ws >>. expression)))
        >>= fun ((isMut, name, start), typeValue, defaultOpt) ->
            declareField start name typeValue.Id >>% TypeField(name, isMut, Some typeValue, defaultOpt)

    let recordFieldSeparator =
        attempt (pstring "," >>. ws >>% ()) <|> (skipMany1 (skipAnyOf " \t\r\n") >>. ws)

    // Positional fields come before the named ones, as in a record value: `(int, step: int)`. They are named by
    // position, `0`, `1`, ..., as a tuple's are. Brackets with no named field are a tuple or a grouping instead.
    let positionalTypeFields =
        many (attempt (typeExpr .>> ws .>> pstring "," .>> ws))
        |>> List.mapi (fun index typeValue -> TypeField(string index, false, Some typeValue, None))

    let namedTypeFields =
        pipe2
            (attempt typeMemberField <|> typeField)
            (many (attempt (recordFieldSeparator >>. (attempt typeMemberField <|> typeField))))
            (fun first rest -> first :: rest)

    // The names noted while a record type is parsed are its named fields: a record nested in it takes its own.
    let declaringFields (record: Parser<TypeExpr, ParseState>) =
        collectingNames record >>= fun (record, fields) -> declare RecordTypeFields record.Id fields >>% record

    // `...` or `...r` after the named fields: the record has other named fields too (see `TypeOpenRecord`).
    let otherFields =
        attempt (recordFieldSeparator >>. pstring "...") >>. opt identifierNoWs

    // A doc comment before `(x: int, ...` documents the field written first.
    let typeRecord =
        let openBracket = getPosition .>> pstring "(" .>> ws .>>. getPosition >>= fun (start, after) -> noteDocumentable start (int after.Index)
        declaringFields (
            spannedType (
                between
                    openBracket
                    (ws >>. pstring ")")
                    (pipe3
                        (ws >>. positionalTypeFields)
                        namedTypeFields
                        (opt otherFields)
                        (fun positional named others ->
                            match others with
                            | Some row -> TypeOpenRecord(positional @ named, row)
                            | None -> TypeRecord(positional @ named)))))

    // Brackets group: `(int, a -> b)` is a function of two parameters, as it is without them, and `()` is unit.
    // A bracketed tuple spans its brackets, as a tuple pattern does; any other type in brackets is just that type.
    let typeTupleOrParen =
        withSpan (
            between
                (pstring "(")
                (ws >>. pstring ")")
                (ws >>. opt (attempt typeSignature)))
        |>> fun (inner, span) ->
            match inner with
            | None -> TypeExpr.at span TypeUnit
            | Some ({ TypeExpr.Kind = TypeTuple _ } as tuple) -> { tuple with Span = span }
            | Some single -> single

    let typeTag =
        let namedField =
            pipe2
                (identifierNoWs >>= fun name -> noOptionalField name >>% name)
                ((wsInline >>. (pstring ":" <|> pstring "::") >>. ws >>. typeExpr))
                (fun name typeValue -> TypeField(name, false, Some typeValue, None))
        // A payload is a record of named fields, or the types in the brackets: `#pair(int, string)` carries a
        // tuple and `#apply(a, b -> c)` a function. Empty brackets, `#some()`, carry the unit type.
        let unitPayload = attempt (spannedType (pstring "(" >>. ws >>. pstring ")" >>% TypeUnit))
        let payload =
            unitPayload
            <|> between
                (pstring "(")
                (ws >>. pstring ")")
                (attempt (ws >>. spannedType (sepBy1 namedField (pstring "," .>> ws) |>> TypeRecord)) <|> (ws >>. typeList |>> typeListToType))
        spannedType (pipe2 (pstring "#" >>. identifierNoWs) (opt payload) (fun tagName payloadOpt -> TypeTag(tagName, payloadOpt)))

    // `map(string, int)` has two arguments; `map(string, a -> b)` has one, a function, as `,` binds tighter. Named
    // fields are one argument, a record, in the application's own brackets as in a tag's payload: `list(name: string)`
    // is `list((name: string))`.
    let typeApply =
        // Only when the fields fill the brackets: `type` alone reads as the start of a member field in `typeof x`.
        let recordArgument =
            declaringFields (spannedType (namedTypeFields |>> TypeRecord)) .>> followedBy (ws >>. pstring ")")
            |>> List.singleton
        let typeArguments =
            typeList |>> fun ((args, resultOpt), span) ->
                match resultOpt with
                | None -> args
                | Some _ -> [ typeListToType ((args, resultOpt), span) ]
        spannedType (
            pipe2
                (pipe2 identifierNoWs (many (attempt (pchar '.' >>. identifierNoWs))) (fun head tail -> String.concat "." (head :: tail)))
                (between (pstring "(") (ws >>. pstring ")") (ws >>. (attempt recordArgument <|> typeArguments)))
                (fun name args -> TypeApply(name, args)))

    // An operator a function needs, required by the context member it reads rather than by a context's name:
    // `` [`+`: a, a -> a] ``. It is a record type of that one member, and its type is a list of types, as a def's is.
    // Its type runs to the end of the set, a `&` in it being an intersection, so a member with contexts after it is
    // bracketed: `` [(`+`: a, a -> a) & Eq(a)] ``. A bracketed member spans its brackets.
    let contextMember =
        spannedType (
            pipe2
                (nameNoWs .>> wsInline .>> pstring ":" .>> ws)
                typeSignature
                (fun name memberType -> TypeRecord [ TypeField(name, false, Some memberType, None) ]))

    let bracketedContextMember =
        withSpan (between (pstring "(" >>. ws) (ws >>. pstring ")") contextMember)
        |>> fun (item, span) -> { item with Span = span }

    // The contexts a function needs, joined with `&`: `[Eq(a) & Hash(a)]`. `+` joins them too, as an earlier spec of
    // the language had it; the official linter's `plusBetweenContexts` rule reports it. An empty set, `[]`, says on
    // purpose that the function requires no contexts, as leaving the set out does.
    let contextSet =
        spannedType (
            between
                (pstring "[" >>. wsInline)
                (wsInline >>. pstring "]")
                (sepBy
                    (attempt bracketedContextMember <|> attempt contextMember <|> attempt typeApply <|> typeIdentifier)
                    (attempt (wsInline >>. (pstring "&" <|> pstring "+")) >>. wsInline))
            |>> TypeContext)

    let contextsOf (contextSet: TypeExpr) =
        match contextSet.Kind with
        | TypeContext items -> items
        | _ -> []

    let typeConstraint =
        spannedType (
            pipe3
                identifierNoWs
                (pstring "::" >>. ws >>. typeExpr)
                (ws >>. pstring "where" >>. ws >>. expression)
                (fun name baseType predicate -> TypeConstraint(name, baseType, predicate)))

    // `typeof x` takes a value, not a type, so its operand is a name or a path of fields (`config.server`) and
    // never an arbitrary expression. Brackets only group: `typeof(x)` is `typeof x`.
    let typeOf =
        let path =
            withSpan (spanned (identifierNoWs |>> IdentifierExpr)) >>= fun (head, headText) ->
                many (attempt (pstring "." >>. withSpan identifierNoWs)) |>> fun fields ->
                    fields
                    |> List.fold (fun ((expr: Expression), text) (field, fieldSpan) ->
                        let span = SourceSpan.between text fieldSpan
                        Expression.at span (MemberAccess(expr, field, Some fieldSpan)), span) (head, headText)
                    |> fst
        let bracketed = between (pstring "(" >>. ws) (ws >>. pstring ")") path
        spannedType (pstring "typeof" >>. keywordEnd >>. wsInline >>. (bracketed <|> path) |>> TypeOf)

    let typeAtom =
        choice [
            attempt typeOf
            attempt typeConstraint
            attempt typeTag
            attempt typeApply
            attempt contextSet
            attempt typeRecord
            attempt typeTupleOrParen
            typeIdentifier
        ]

    // `mut int` is the type of a cell that holds an int (see `CellPassingSyntax`).
    let typeCell =
        spannedType (attempt (pstring CellPassingSyntax.typeName >>. keywordEnd >>. ws) >>. typeAtom |>> fun held -> TypeApply(CellPassingSyntax.typeName, [ held ]))

    let typePostfix =
        (typeCell <|> typeAtom) .>> noOptionalMarker "A type can't be marked optional with `?`: write a value that may be absent as `#some(T) | #none`"

    let typeIntersection =
        singleOrCombined TypeIntersection (sepBy1 typePostfix (attempt (ws >>. pstring "&" >>. ws)))

    // A `|` starts what comes after it, so a doc comment before `| #tag` documents the tag.
    let bar = getPosition .>> pstring "|" .>> ws .>>. getPosition >>= fun (barStart, after) -> noteDocumentable barStart (int after.Index)
    let typeUnion =
        opt bar >>. singleOrCombined TypeUnion (sepBy1 typeIntersection (attempt (ws >>. bar)))

    let typeArrow =
        withSpan (typeUnion .>>. opt (attempt (ws >>. pstring "->" >>. ws >>. typeExpr))) |>> fun ((left, rightOpt), span) ->
            match rightOpt with
            | None -> left
            | Some right -> TypeExpr.at span (TypeFunc(left, right))

    let typeWithContext =
        (attempt (
            withSpan ((contextSet .>> ws) .>>. opt (attempt typeArrow)) |>> fun ((ctx, typeOpt), span) ->
                match typeOpt with
                | Some t -> TypeExpr.at span (TypeWithContext(contextsOf ctx, t))
                | None -> ctx))
        <|> typeArrow

    typeExprRef := typeWithContext

    // `,` binds tighter than `->` wherever types are listed (see the types page), so `int, int -> int` means
    // `(int, int) -> int` in a def's annotation, in brackets, in type arguments and in a tag's payload alike. A
    // function that is one of the items has brackets of its own. A record's fields and a lambda's parameters
    // are separated by commas themselves, so a type there takes no bare list: a field's is a `typeExpr`, and a
    // parameter's a `parameterType`.
    typeListRef :=
        withSpan (
            sepBy1 typeUnion (attempt (wsInline >>. pstring "," >>. ws))
            .>>. opt (attempt (ws >>. pstring "->" >>. ws >>. typeExpr)))

    // A def's annotation: a type list, possibly after the contexts it needs.
    typeSignatureRef :=
        attempt (
            withSpan ((contextSet .>> ws) .>>. typeList) |>> fun ((ctx, list), span) ->
                TypeExpr.at span (TypeWithContext(contextsOf ctx, typeListToType list)))
        <|> (typeList |>> typeListToType)

    parameterTypeRef := typeUnion
    ()

/// A lambda parameter with an optional annotation of the type `annotation` parses: a `parameterType` where the `->`
/// after it ends the parameters, a `typeExpr` inside brackets.
let typedIdentifier (annotation: Parser<TypeExpr, ParseState>) : Parser<Identifier * TypeExpr option, ParseState> =
    pipe2
        identifierNoWs
        (opt (attempt (wsInline >>. pstring ":" >>. ws >>. annotation)))
        (fun name typeOpt -> (name, typeOpt))

/// A def's name with an optional same-line annotation. Unlike `typedIdentifier` (lambda parameters), the
/// annotation is a `typeSignature`.
let typedDefIdentifier: Parser<Identifier * TypeExpr option, ParseState> =
    pipe2
        nameNoWs
        (opt (attempt (wsInline >>. pstring ":" >>. ws >>. typeSignature)))
        (fun name typeOpt -> (name, typeOpt))

let typedQualifiedIdentifier: Parser<Identifier * TypeExpr option, ParseState> =
    pipe2
        qualifiedIdentifier
        (opt (attempt (wsInline >>. pstring ":" >>. ws >>. typeSignature)))
        (fun name typeOpt -> (name, typeOpt))

// A def's name and type annotation. The annotation may follow the name on the same line or start an
// indented continuation line (`def f` / `  : T` / `  = ...`), like the `=` does.
let defNameAndType (lineStartCol: int64) (identParser: Parser<Identifier * TypeExpr option, ParseState>) =
    identParser .>> wsInline
    >>= fun (name, typeOpt) ->
        match typeOpt with
        | Some _ -> preturn (name, typeOpt)
        | None ->
            opt (attempt (newlineIndentedContinuation lineStartCol >>. pstring ":" >>. ws >>. typeSignature .>> wsInline))
            |>> fun continuationType -> (name, continuationType)

/// Fails, at the operator, when `name`, a def's name written from `start`, defines an operator Nanyx does not have:
/// `` def `+++` = ... `` or `` def Money.`+++` = ... `` (see `binaryOperators`), or `??`, which is syntax.
let definableName (start: Position) (name: Identifier) : Parser<unit, ParseState> =
    let last = name.Substring(name.LastIndexOf '.' + 1)
    let reportAtOperator message =
        writtenNameSpan start name >>= fun span ->
            let offset = int64 (span.End - (Names.written last).Length) - start.Index
            reportFocused (Position(start.StreamName, start.Index + offset, start.Line, start.Column + offset)) message
    if last = coalesceOperator then
        reportAtOperator $"`{last}` is built in, so it cannot be defined: `a ?? b` is the value inside `a`'s `#some`, or else `b`"
    elif Names.isOperator last && not (isDefinableOperator last) then
        let operators = binaryOperators |> List.filter (fun op -> isDefinableOperator op.Text) |> List.map (fun op -> $"`{op.Text}`")
        reportAtOperator $"""`{last}` is not one of Nanyx's operators, so it cannot be defined. The operators are {String.concat ", " operators}"""
    else
        preturn ()

/// `def name = value`, written from `lineStart` (where an `export` before it starts). The def's name is declared by its
/// value.
let parseDefWith (lineStart: Position) (identParser: Parser<Identifier * TypeExpr option, ParseState>) (exprParser: Parser<Expression, ParseState>) =
    (pstring "def" >>. ws >>. getPosition .>>. defNameAndType lineStart.Column identParser)
    >>= fun (start, (name, typeOpt)) ->
        definableName start name
        >>. defEqualsContinuation lineStart.Column >>. pstring "=" >>. wsInline >>. exprParser
        >>= fun expr ->
            writtenNameSpan start name >>= fun span -> declare DefValue expr.Id [ name, span ]
            >>. noteDocumentable lineStart (int start.Index)
            >>% (name, typeOpt, expr)

/// A type's or a context's parameter, with an optional bound: `a`, `a :: Shape`. It is a type variable in the
/// definition's body, so it is a single lowercase letter, as any type variable is.
let typeParam: Parser<Identifier * TypeExpr option, ParseState> =
    let name =
        getPosition .>>. identifierNoWs >>= fun (start, name) ->
            if Names.isTypeVariable name then
                preturn name
            elif Char.IsLower name[0] then
                reportFocused start
                    $"A type parameter is a single lowercase letter, such as 'a', so '{name}' cannot be one: longer lowercase names are reserved for associated types"
            else
                reportFocused start $"A type parameter is a single lowercase letter, such as 'a', so '{name}' cannot be one"
    pipe2
        name
        (opt (attempt (ws >>. pstring "::" >>. ws >>. typeExpr)))
        (fun name typeOpt -> (name, typeOpt))
