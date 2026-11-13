/// Statements, and the top-level items of a module: definitions, imports, attributes and module blocks.
[<AutoOpen>]
module Parser.Program.TopLevel

open System
open FParsec
// After the libraries, so that the parser's names shadow theirs (identifier, Attribute) as they did in one file.
open Parser.Program

// Wire up statement parser. In a library a file's top-level code runs only when one of its own values is first used.
// Every parse reads `moduleParser`, so this runs before any parse, and it reads values of `Expressions` first, which
// fills that file's refs before these, in the order they were filled when the parser was one file.
do
    let statementTail =
        wsInline >>. opt (attempt (inlineCommentNoNl ())) >>% ()

    let importStatement =
        pstring "import" >>. wsInline >>. importItems |>> ImportStatement

    let useStatement =
        (pstring "use" >>. keywordEnd >>. ws >>. useValue .>> notFollowedBy (ws >>. pstring "in" >>. keywordEnd))
        |>> UseStatement

    let typeDefStatement =
        typeDefCore |>> function
            | TypeDef(name, modifiers, typeParams, typeValue) ->
                TypeDefStatement(name, modifiers, typeParams, typeValue)
            | _ -> failwith "Expected type definition"

    let defExprParser =
        attempt (blockExpr()) <|> singleOrTuple (sepBy1 expression (pstring "," .>> ws))

    let defStatement =
        getPosition >>= fun lineStart ->
            pipe2
                (opt (attempt (pstring "export" >>. ws)))
                (parseDefWith lineStart typedQualifiedIdentifier defExprParser)
                (fun exportOpt (name, typeOpt, expr) -> DefStatement(exportOpt.IsSome, name, typeOpt, expr))

    // Each def a destructuring def stands for spans the whole `def (...) = value`.
    let destructuringDefStatement =
        getPosition >>= fun lineStart ->
            withSpan (destructuringDefWith lineStart defExprParser)
            |>> fun ((isExport, bindings), span) ->
                let span = match List.tryHead bindings with Some (_, value) -> endingAt value.Span span | None -> span
                bindings |> List.mapi (fun index (name, value) -> Statement.at span (DefStatement(isExport && index > 0, name, None, value)))
    // `rec name = { ... }` is a local definition that can refer to itself (see the functions page). It is a
    // def whose value is wrapped in a call to the built-in "__rec" (see `RecSyntax`), which makes the name visible in
    // the value, and tells the backend to compile the lambda as a function that may call itself.
    let recStatement =
        getPosition >>= fun lineStart ->
            (pstring "rec" >>. keywordEnd >>. ws >>. getPosition .>>. defNameAndType lineStart.Column typedQualifiedIdentifier)
            >>= fun (start, (name, typeOpt)) ->
                definableName start name
                >>. defEqualsContinuation lineStart.Column >>. pstring "=" >>. wsInline >>. defExprParser
                >>= fun expr ->
                    let value = Expression.from expr (FunctionCall(RecSyntax.builtIn, None, [ expr ]))
                    writtenNameSpan start name >>= fun span -> declare DefValue value.Id [ name, span ]
                    >>. noteDocumentable lineStart (int start.Index)
                    >>% DefStatement(false, name, typeOpt, value)

    // `mut name = value` declares a cell (see the memory page): a def whose value is a call to the built-in "__mut",
    // spanning the declaration (see `MutSyntax`). It declares one name, so a pattern after `mut` is an error: a value's
    // parts are taken apart with `def` first.
    let mutStatement =
        let named =
            followedBy (satisfy (fun c -> Char.IsLetter c || c = '_' || c = '`'))
            <|> (getPosition >>= fun position ->
                    reportFocused position "`mut` declares one cell, by name: take a value apart with `def` first, then declare a cell for each part")
        getPosition >>= fun lineStart ->
            (pstring "mut" >>. keywordEnd >>. ws >>. named >>. getPosition .>>. defNameAndType lineStart.Column typedQualifiedIdentifier)
            >>= fun (start, (name, typeOpt)) ->
                definableName start name
                >>. defEqualsContinuation lineStart.Column >>. pstring "=" >>. wsInline >>. defExprParser
                >>= fun expr ->
                    let span = spanOf lineStart expr.Span.End
                    let value = Expression.at span (FunctionCall(MutSyntax.builtIn, None, [ expr ]))
                    declare DefValue value.Id [ name, nameSpan start name ]
                    >>. noteDocumentable lineStart (int start.Index)
                    >>% DefStatement(false, name, typeOpt, value)

    let exprStatement =
        wsInline
        >>. exprWithoutCrossingNewlines
        .>> statementTail
        |>> ExprStatement  // Consume trailing inline comments without consuming newlines
    
    statementItemsRef := (attempt destructuringDefStatement <|> (statement |>> List.singleton)) <?> "statement"
    statementRef :=
        spannedStatement (attempt importStatement <|> attempt typeDefStatement <|> attempt defStatement <|> attempt recStatement <|> attempt mutStatement <|> attempt useStatement <|> exprStatement)
        <?> "statement"

/// Runs `p` and makes what it built a top-level item spanning the text it consumed.
let private spannedItem (p: Parser<TopLevelItemKind, ParseState>) : Parser<TopLevelItem, ParseState> =
    withSpan p |>> fun (kind, span) -> TopLevelItem.at span kind

/// A module declaration, `module collections.list`. A dotted name is kept whole, as a module block's qualified name is.
let moduleDecl: Parser<TopLevelItem, ParseState> =
    let moduleName = (sepBy1 identifierNoWs (pchar '.') |>> String.concat ".") .>> wsInline
    spannedItem (pstring "module" >>. ws >>. moduleName |>> ModuleDecl)
    <?> "module declaration"

let private valueDefExprParser =
    attempt (blockExpr()) <|> (wsInline >>. singleOrTuple (sepBy1 expression (pstring "," .>> ws)))

// Parser for value definition (top-level)
let valueDef: Parser<TopLevelItem, ParseState> =
    getPosition >>= fun lineStart ->
        spannedItem (
            pipe2
                (opt (attempt (pstring "export" >>. ws)))
                (parseDefWith lineStart typedQualifiedIdentifier valueDefExprParser)
                (fun exportOpt (name, typeOpt, expr) -> Def(ValueDef(exportOpt.IsSome, name, typeOpt, expr))))
    <?> "value definition"

// Top-level `def (x, y) = ...`, as the defs it desugars into (see `destructuringBindings`). Each spans the whole
// `def (...) = value`.
let valueDestructuringDef: Parser<TopLevelItem list, ParseState> =
    getPosition >>= fun lineStart ->
        withSpan (destructuringDefWith lineStart valueDefExprParser)
        |>> fun ((isExport, bindings), span) ->
            let span = match List.tryHead bindings with Some (_, value) -> endingAt value.Span span | None -> span
            bindings |> List.mapi (fun index (name, value) -> TopLevelItem.at span (Def(ValueDef(isExport && index > 0, name, None, value))))
    <?> "value definition"

// Parser for type definition (top-level)
let typeDef: Parser<TopLevelItem, ParseState> =
    spannedItem (typeDefCore |>> Def)

// Parser for top-level expressions (script-style)
let topLevelExpr: Parser<TopLevelItem, ParseState> =
    spannedItem (exprWithoutCrossingNewlines |>> Expr)
    <?> "top-level expression"

// Parser for an attribute line such as `@entry`; it annotates the next top-level item. Its argument, if it has one,
// is written straight after the name as a tag's payload is, `@test(#io)`, so `@entry (x)` is an attribute and then an
// expression.
let attributeItem: Parser<TopLevelItem, ParseState> =
    spannedItem (
        pchar '@' >>. identifierNoWs .>>. opt (attempt unitExpr <|> parenArguments)
        |>> Attribute)
    <?> "attribute"

let private importLine: Parser<TopLevelItem, ParseState> =
    spannedItem (attempt (pstring "import" >>. wsInline >>. importItems) |>> Import)

// Tried before the definitions, which start with `export` too: `export def f` has no bracket after `def f`.
let private reExportLine: Parser<TopLevelItem, ParseState> =
    spannedItem (attempt (pstring "export" >>. keywordEnd >>. wsInline >>. reExportItem) |>> ReExport)

let private moduleBlockItem: Parser<TopLevelItem, ParseState> =
    choice [
        importLine
        attributeItem
        attempt typeDef
        valueDef
        topLevelExpr
    ]

let moduleBlockDecl: Parser<TopLevelItem, ParseState> =
    let linePayload: Parser<TopLevelItem option, ParseState> =
        choice [
            attempt (skipMany (skipAnyOf " \t") >>. inlineCommentNoNl() >>% None)
            attempt (skipMany (skipAnyOf " \t") >>. (followedBy newline <|> followedBy eof) >>% None)
            moduleBlockItem |>> Some
        ]

    let lineAtIndent indent =
        attempt (manyMinMaxSatisfy indent indent (fun c -> c = ' ' || c = '\t') >>. linePayload)

    // The block's text is noted, since what it does not export can only be named inside it. The block itself ends
    // where its last item does, not at a comment after it.
    withSpan (attempt (
        (pstring "module" >>. ws >>. identifierNoWs .>> wsInline .>> pstring "=") >>= fun name ->
            skipMany (skipAnyOf " \t") >>. newline >>.
            many (pchar ' ' <|> pchar '\t') >>= fun indentChars ->
                let indent = indentChars.Length
                if indent > 0 then
                    pipe2
                        linePayload
                        (many (attempt (blockLinesEnd () >>. lineAtIndent indent)))
                        (fun first rest -> name, first :: rest |> List.choose id)
                    >>= fun (name, items) -> if items.IsEmpty then pzero else preturn (ModuleBlock(name, items))
                else
                    pzero
    ))
    >>= fun (kind, span) ->
        let block =
            match kind with
            | ModuleBlock(_, items) -> TopLevelItem.at (endingAt (List.last items).Span span) kind
            | _ -> TopLevelItem.at span kind
        match kind with
        | ModuleBlock(name, _) ->
            updateUserState (fun state -> { state with ModuleBlocks = state.ModuleBlocks @ [ name, span ] }) >>% block
        | _ -> preturn block
    <?> "module block declaration"

// Parser for top-level items
let topLevelItem: Parser<TopLevelItem, ParseState> =
    ws >>. choice [
        attempt moduleBlockDecl
        moduleDecl
        importLine
        reExportLine
        attributeItem
        attempt typeDef
        valueDef
        topLevelExpr
    ] .>> ws
    <?> "top-level item"

let private topLevelItems: Parser<TopLevelItem list, ParseState> =
    attempt (ws >>. valueDestructuringDef .>> ws) <|> (topLevelItem |>> List.singleton)

// Parser for a complete module
let moduleParser: Parser<Module, ParseState> =
    ws >>. many topLevelItems .>> ws .>> eof |>> List.concat
    <?> "module"
