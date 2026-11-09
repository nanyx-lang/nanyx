[<AutoOpen>]
module Parser.Program.Api

open System
open FParsec
// After the libraries, so that the parser's names shadow theirs (identifier, Attribute) as they did in one file.
open Parser.Program

// The forward refs filled in earlier files, in the order they were filled when the parser was one file. In a
// library a file's top-level code runs only when one of its own values is first used, so this file, which every
// parse goes through, fills them.
do fillPatternRef ()
do fillTypeRefs ()

// Interpolated double-quoted strings with {expr} holes
let interpolatedStringExprNoWs: Parser<Expression, ParseState> =
    let textPart = stringText (many1Chars (noneOf "\"{")) |>> StringText
    let holePart =
        between (pchar '{' .>> ws) (ws >>. pchar '}') expression
        |>> StringExpr
    let partsParser = many (choice [attempt holePart; textPart])
    let toKind parts =
        match parts with
        | [] -> LiteralExpr (StringLit "")
        | _ when parts |> List.exists (function StringExpr _ -> true | _ -> false) ->
            InterpolatedString parts
        | _ ->
            let text =
                parts
                |> List.choose (function StringText value -> Some value | _ -> None)
                |> String.concat ""
            LiteralExpr (StringLit text)
    spanned (between (pstring "\"") (pstring "\"") partsParser |>> toKind)

// `def (x, y) = value`, or `def x, y = value` without the brackets, binds each element of a tuple. It is desugared
// in the parser into a binding of the whole value plus one `def` per element (`def x = __tuple.0`), so the rest of
// the compiler only ever sees plain defs. This form takes only names, `_` and nested tuples, which cannot fail to
// match; any other pattern is bound through a match instead (see `matchingBindings`).
let private destructuringBindings (position: Position) (pattern: Pattern) (value: Expression) : (Identifier * Expression) list option =
    let temp = $"__tuple_{position.Line}_{position.Column}"
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

// `def pattern = value` with any other pattern (`def $Money(pence) = m`, `def [first, ...rest] = xs`,
// `def #some(x) = get(key)`) is a match with one arm: the whole value is bound to a temporary under the name
// the tuple form uses, as the match's result, which is the tuple of the names the pattern binds, and then one
// `def` per name takes its element. Whether the pattern is total is for the type checker to say, as it does
// for any other match.
let private matchingBindings (position: Position) (pattern: Pattern) (names: Identifier list) (value: Expression) : Expression * (Identifier * Expression) list =
    let temp = $"__tuple_{position.Line}_{position.Column}"
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

/// `[export] def pattern = value`, as the export flag and the bindings it stands for. A bare name is not a
/// pattern here: that is an ordinary def.
let destructuringDefWith (lineStartCol: int64) (exprParser: Parser<Expression, ParseState>) : Parser<bool * (Identifier * Expression) list, ParseState> =
    // `def x, y = value` is `def (x, y) = value`: patterns separated by commas are the elements of a tuple.
    let patterns =
        withSpan (sepBy1 pattern (pstring "," .>> ws))
        |>> fun (elements, span) ->
            match elements with
            | [ single ] -> single
            | several -> Pattern.at span (TuplePattern several)
    pipe3
        (opt (attempt (pstring "export" >>. ws)))
        (getPosition .>>. (pstring "def" >>. ws >>. collectingNames patterns .>> wsNoNl()))
        (defEqualsContinuation lineStartCol >>. pstring "=" >>. wsNoNl() >>. exprParser)
        (fun exportOpt (position, pattern) value -> exportOpt.IsSome, position, pattern, value)
    >>= fun (isExport, position, (pattern, names), value) ->
        // Each name is declared by the value of the def it becomes.
        let declareBindings (bindings: (Identifier * Expression) list) =
            bindings
            |> List.map (fun (name, bound) -> declare DefValue bound.Id (names |> List.filter (fun (declared, _) -> declared = name)))
            |> List.fold (>>.) (preturn ())
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

// Block parser - parses indented statements after a newline
let blockExpr() : Parser<Expression, ParseState> =
    skipMany (skipAnyOf " \t") >>. newline >>.
    many (pchar ' ' <|> pchar '\t') >>= fun indentChars ->
        let indentLevel = indentChars.Length
        if indentLevel > 0 then
            let statementAtIndent =
                attempt (manyMinMaxSatisfy indentLevel indentLevel (fun c -> c = ' ' || c = '\t') >>. statementItems)

            blockSpanned (
                pipe2
                    statementItems
                    (many (attempt (blockLinesEnd () >>. statementAtIndent)))
                    (fun first rest -> List.concat (first :: rest)))
        else
            pzero

let matchArmBody: Parser<Expression, ParseState> =
    attempt (blockExpr()) <|> (wsNoNl() >>. exprWithoutCrossingNewlines)

let typeDefCore: Parser<Definition, ParseState> =
    let typeModifiers =
        many (attempt (
            choice [
                pstring "export" >>. ws >>% Export
                pstring "context" >>. ws >>% Context
            ]))

    let typeParams =
        between (pstring "(") (pstring ")") (sepBy1 (wsNoNl() >>. typeParam) (pstring "," .>> wsNoNl()))

    let typeExprWithWhere =
        withSpan (typeExpr .>>. opt (attempt (ws >>. pstring "where" >>. ws >>. expression))) |>> fun ((baseType, whereOpt), span) ->
            match whereOpt with
            | Some predicate -> TypeExpr.at (endingAt predicate.Span span) (TypeWhere(baseType, predicate))
            | None -> baseType

    // The body declares the type's name, written at `start`.
    let declaringTypeName ((start, name), def) =
        match def with
        | TypeDef(_, _, _, body) -> declare TypeDefBody body.Id [ name, nameSpan start name ] >>% def
        | ValueDef _ -> preturn def

    let applyPrivate modifiers privateOpt =
        match privateOpt with
        | Some _ -> Private :: modifiers
        | None -> modifiers

    let contextName =
        typeNameIdentifier >>= fun name ->
            if name = "type" then
                fail "expected context name"
            else
                preturn name

    let contextDef =
        pipe5
            (opt (attempt (pstring "export" >>. ws)))
            (pstring "context" >>. ws >>. (getPosition .>>. contextName))
            (opt (attempt (wsNoNl() >>. typeParams)))
            (wsNoNl() >>. pstring "=" >>. ws >>. opt (attempt (pstring "private" >>. ws)))
            typeExprWithWhere
            (fun exportOpt (start, name) paramsOpt privateOpt typeValue ->
                let typeParams = defaultArg paramsOpt []
                let baseMods = if exportOpt.IsSome then [Export] else []
                let modifiers = applyPrivate (Context :: baseMods) privateOpt
                (start, name), TypeDef(name, modifiers, typeParams, typeValue))
        >>= declaringTypeName

    let typeDefWithModifiers =
        pipe5
            typeModifiers
            (pstring "type" >>. ws >>. (getPosition .>>. typeNameIdentifier))
            (opt (attempt (wsNoNl() >>. typeParams)))
            (wsNoNl() >>. pstring "=" >>. ws >>. opt (attempt (pstring "private" >>. ws)))
            typeExprWithWhere
            (fun modifiers (start, name) paramsOpt privateOpt typeValue ->
                let typeParams = defaultArg paramsOpt []
                let modifiers = applyPrivate modifiers privateOpt
                (start, name), TypeDef(name, modifiers, typeParams, typeValue))
        >>= declaringTypeName

    attempt contextDef <|> typeDefWithModifiers
    <?> "type definition"

let valueDefCore: Parser<Definition, ParseState> =
    getPosition >>= fun lineStart ->
        pipe2
            (opt (attempt (pstring "export" >>. ws)))
            (parseDefWith lineStart.Column typedDefIdentifier (wsInline >>. singleOrTuple (sepBy1 expression (pstring "," .>> ws))))
            (fun exportOpt (name, typeOpt, expr) -> ValueDef(exportOpt.IsSome, name, typeOpt, expr))
    <?> "value definition"

// A match arm: `| patterns -> body`. An arm may list several alternatives (an or-pattern), written
// `| p1 | p2 -> body` on one line or with each `|` on its own line; every arm ends in `->`, so a `|` that is
// not preceded by `->` continues the current arm rather than starting a new one. The arm is desugared into one
// arm per alternative, all sharing the body, so the rest of the compiler only ever sees plain arms.
let private armAlternativesAfter (leading: Parser<unit, ParseState>) : Parser<(Pattern list * Expression) list, ParseState> =
    let alternative = sepBy1 pattern (pstring "," .>> ws)
    pipe2
        (leading >>. collectingNames (sepBy1 alternative (attempt (ws >>. pstring "|" >>. ws))))
        (pstring "->" >>. matchArmBody)
        (fun (alternatives, names) body -> alternatives, names, body)
    >>= fun (alternatives, names, body) ->
        declare ArmBody body.Id names >>% (alternatives |> List.map (fun patterns -> patterns, body))
    <?> "match arm"

let matchArmAlternatives : Parser<(Pattern list * Expression) list, ParseState> =
    armAlternativesAfter (pstring "|" >>. ws)

/// One or more arms, each possibly an or-pattern, flattened into plain arms.
let matchArms : Parser<(Pattern list * Expression) list, ParseState> =
    many1 (attempt (ws >>. matchArmAlternatives)) |>> List.concat

// `value except pattern -> replacement`, or `value except` followed by `| pattern -> replacement` arms as a match
// has them, replaces the cases of `value` the arms match and passes every other case through. It binds looser
// than every operator and pipe, and may start a line of its own, as a pipe may: `xs \find(p)` and then
// `except #notFound -> #none` on the next line. Its arms take one value each.
let private exceptArms : Parser<(Pattern list * Expression) list, ParseState> =
    let keywordEnd = notFollowedBy (satisfy (fun c -> Char.IsLetterOrDigit c || c = '_'))
    pstring "except" >>. keywordEnd >>. (attempt matchArms <|> (ws >>. armAlternativesAfter (preturn ())))
    <?> "except"

/// `value` followed by the `except` arms `leadingWs` lets it be separated from, if there are any.
let private withExcept (leadingWs: Parser<unit, ParseState>) (valueParser: Parser<Expression, ParseState>) : Parser<Expression, ParseState> =
    withSpan valueParser .>>. opt (attempt (leadingWs >>. withSpan exceptArms))
    |>> fun ((value, valueText), exceptOpt) ->
        match exceptOpt with
        | Some (arms, armsText) -> Expression.at (SourceSpan.between valueText armsText) (Except(value, arms))
        | None -> value

// `for iterable | arms` runs the arms' bodies for each element (the element is matched against the arms, as
// the loops page shows with `| { < 5 } -> ... | _ -> ...`), and `for iterable -> body` ignores the element.
// Both are represented as a call to the built-in "__for" with the iterable and a one-parameter lambda,
// which the type checker and backend handle as a loop.
let forExpr : Parser<Expression, ParseState> =
    let keywordEnd = notFollowedBy (satisfy (fun c -> Char.IsLetterOrDigit c || c = '_'))
    let elementName = "__for_element"
    // The whitespace before the first arm is skipped outside the span, which starts at its `|`.
    let withArms =
        ws >>. withSpan matchArms |>> fun (arms, span) ->
            let element = Expression.at span (IdentifierExpr elementName)
            Expression.at span (Lambda([ (elementName, None) ], Expression.at span (Match([ element ], arms))))
    let withoutElement =
        ws >>. withSpan (pstring "->" >>. matchArmBody) |>> fun (body, span) ->
            Expression.at span (Lambda([ (elementName, None) ], body))
    spanned (
        pipe2
            (pstring "for" >>. keywordEnd >>. ws >>. expression)
            (attempt withArms <|> withoutElement)
            (fun iterable body -> FunctionCall("__for", None, [ iterable; body ])))
    <?> "for loop"

/// The column (counted from 1, as FParsec does) of the first character on the current line that is not a space
/// or tab: how far the line the parser is on is indented.
let private currentLineIndent: Parser<int64, ParseState> =
    fun stream ->
        let isOnLine (offset: int) =
            let c = stream.Peek(offset)
            c <> '\n' && c <> CharStream.EndOfStreamChar
        let isIndent (offset: int) =
            let c = stream.Peek(offset)
            c = ' ' || c = '\t'
        let mutable lineStart = 0
        while isOnLine (lineStart - 1) do
            lineStart <- lineStart - 1
        let mutable indent = 0
        while isIndent (lineStart + indent) do
            indent <- indent + 1
        Reply(int64 indent + 1L)

// Match expression parser. The scrutinees are any expressions written on the `match` line (`match a, b`), or
// a block on the lines after it. The block must be indented more than both the line `match` is on and the
// arms, so that it cannot be mistaken for either:
//
//   match
//       def trimmed = s \trim
//       trimmed \split(",")
//     | [a, b] -> ...
do
    matchExprRef :=
        let keywordEnd = notFollowedBy (satisfy (fun c -> Char.IsLetterOrDigit c || c = '_'))
        let sameLine = wsNoNl() >>. sepBy1 expression (pstring "," .>> ws) |>> fun scrutinees -> scrutinees, None
        let indentedBlock matchIndent =
            lookAhead (wsNoNl() >>. newline >>. wsNoNl() >>. getPosition) >>= fun blockStart ->
                if blockStart.Column <= matchIndent then
                    fail "the scrutinee of a match must be on the same line or indented more than the match"
                else
                    blockExpr () |>> fun block ->
                        // A block of one expression is just that expression.
                        let scrutinee =
                            match block.Kind with
                            | Block [ Kind (ExprStatement expr) ] -> expr
                            | _ -> block
                        [ scrutinee ], Some blockStart.Column
        let armsAfter blockColumn =
            match blockColumn with
            | None -> matchArms
            | Some column ->
                lookAhead (ws >>. getPosition) >>= fun firstArm ->
                    if firstArm.Column >= column then
                        fail "the arms of a match must be indented less than the block before them"
                    else
                        matchArms
        currentLineIndent >>= fun matchIndent ->
            spanned (
                pstring "match" >>. keywordEnd
                >>. (attempt (indentedBlock matchIndent) <|> sameLine)
                >>= fun (scrutinees, blockColumn) ->
                    armsAfter blockColumn |>> fun arms -> Match(scrutinees, arms))
            .>> skipMany (skipAnyOf " \t\r\n")
        <?> "match expression"

/// An operand of an operator, with the span of all the text it was written as. That is wider than the expression's
/// own span when the operand is in parentheses (`(1 + 2)`) or a lone spread (`[...xs]`), neither of which is a
/// node of its own. An operator spans from its first operand's text to its last one's, so it takes in the
/// delimiters, while an identifier in parentheses is still reported at the identifier.
type Operand = Expression * SourceSpan

// Operator precedence parser
let opp = new OperatorPrecedenceParser<Operand, unit, ParseState>()

let private binary op ((left, leftText): Operand) ((right, rightText): Operand) : Operand =
    let span = SourceSpan.between leftText rightText
    Expression.at span (BinaryOp(op, left, right)), span

// Record field parser: either "name = expr" or just "expr"
let recordField =
    (attempt (
        pipe2
            (identifierNoWs .>> ws .>> pstring "=" .>> ws)
            expression
            (fun name expr -> NamedField(name, expr))
    ) <|> (expression |>> PositionalField))
    <?> "record field"

// The fields of a record or tuple, or a call's arguments, separated by commas or written one per line:
//
//   R(
//     a = 1
//     b = 2
//   )
//
// When the first field starts its line, a field that starts a later line at the same indentation follows the
// field before it without a comma. A field may itself run over several lines (a lambda, a `match`), as long as
// its continuation lines are indented more than the fields.
let private byCommaOrLine (item: Parser<'a, ParseState>) : Parser<'a list, ParseState> =
    ws >>. getPosition .>>. currentLineIndent >>= fun (first, firstLineIndent) ->
        let comma = pstring "," .>> ws
        let nextLine =
            if first.Column <> firstLineIndent then pzero
            else
                ws >>. getPosition .>>. currentLineIndent >>= fun (pos, lineIndent) ->
                    if pos.Column = first.Column && lineIndent = pos.Column then preturn () else pzero
        pipe2
            item
            (many ((comma >>. item) <|> attempt (nextLine >>. item)))
            (fun head tail -> head :: tail)

let recordFields = byCommaOrLine recordField

// A lone positional field is just its expression, all-positional fields are a tuple, and any named field
// makes the whole thing a record.
let private fieldsToExpr (fields, span) =
    match fields with
    | [PositionalField single] -> single
    | _ ->
        let allPositional = fields |> List.forall (function PositionalField _ -> true | _ -> false)
        if allPositional then
            let exprs = fields |> List.map (function PositionalField e -> e | _ -> failwith "unexpected")
            Expression.at span (TupleExpr exprs)
        else
            Expression.at span (RecordExpr fields)

// Arguments in parentheses after a name, as in a call, a tag payload or a pipe target: `(a, b)`, `(x = 1)`.
let private parenArguments =
    between (pstring "(") (pstring ")") (ws >>. withSpan recordFields |>> fieldsToExpr .>> ws)

/// `???`, the placeholder for code not written yet. It is read as a name no other code can write, so every pass that
/// handles names handles it; the type checker reports it as not implemented.
let notImplemented = "???"

let private notImplementedExpr =
    spanned (pstring notImplemented >>% IdentifierExpr notImplemented)

// Unit literal: ()
let unitExpr =
    spanned (between (pstring "(") (ws >>. pstring ")") (preturn ()) |>> fun _ -> UnitExpr)

// Tuple/Record or parenthesized expression parser
// Handles: (x), (x, y), (x = 1), (x = 1, y = 2), (x = 1, 2), etc.
let tupleOrParenExpr =
    (withSpan (
        between
            (pstring "(")
            (ws >>. pstring ")")
            recordFields)
    |>> fieldsToExpr)
    <?> "tuple or record"

// List expression parser. `...expr` splices a list in place: `[a, ...xs, b]` is desugared to
// `[a] ++ xs ++ [b]`, so the rest of the compiler only ever sees list literals and concatenation. Like a record's
// fields, the items are separated by commas or written one per line, and the `]` may start a line:
//
//   [
//     a
//     ...xs
//   ]
let listExpr =
    let item =
        (pstring "..." >>. ws >>. expression |>> Choice2Of2)
        <|> (expression |>> Choice1Of2)
    withSpan (
        between
            (pstring "[")
            (ws >>. pstring "]")
            (ws >>. opt (byCommaOrLine item) |>> Option.defaultValue []))
    |>> fun (items, span) ->
        // Each segment is a run of elements or a spread list.
        let segments =
            items
            |> List.fold (fun segments item ->
                match item, segments with
                | Choice1Of2 element, Choice1Of2 elements :: rest -> Choice1Of2 (elements @ [ element ]) :: rest
                | Choice1Of2 element, _ -> Choice1Of2 [ element ] :: segments
                | Choice2Of2 spread, _ -> Choice2Of2 spread :: segments) []
            |> List.rev
            |> List.map (function
                | Choice1Of2 elements -> Expression.at span (ListExpr elements)
                | Choice2Of2 spread -> spread)
        match segments with
        | [] -> Expression.at span (ListExpr [])
        | _ -> segments |> List.reduceBack (fun left right -> Expression.at span (BinaryOp("++", left, right)))

// Tag expression parser: #tagName or #tagName(expr)
// The payload is written like a call's arguments: several values make a tuple, named ones a record, and none at
// all, `#some()`, the unit value, as `f()` passes it.
let tagExpr =
    spanned (
        pstring "#" >>. identifierNoWs >>= fun tagName ->
        opt (attempt unitExpr <|> parenArguments) |>> fun payloadOpt -> TagExpr(tagName, payloadOpt))

// If-expression parser: if condition then trueExpr else falseExpr
// `if cond then a else b`, or if-matching: `if value is pattern then body [else other]` (see the
// pattern-matching page), which is desugared to `match value | pattern -> body | _ -> other`, with `()` as
// the other arm when there is no `else`. `is` makes it plain that the pattern binds its names rather than
// comparing against existing ones.
// Either branch may be an indented block on the following lines, and the `else` may be left out when the
// `then` branch is unit (`if i > 0 then` followed by `println(...)` on the functions page); a missing
// `else` is `()`.
// The multi-branch form `if | cond1 -> a | value is pattern -> b | else -> c` tests each arm in order and is
// desugared to nested ifs and if-matches, so `else` (or `()` when it is left out) ends up in the innermost
// one. Nothing may follow the `else` arm, so a nested multi-branch if with an `else` cannot swallow the arms
// of an enclosing match.
let ifExpr =
    let keywordEnd = notFollowedBy (satisfy (fun c -> Char.IsLetterOrDigit c || c = '_'))
    let branch = attempt (blockExpr()) <|> (ws >>. expression)
    let patternBinding =
        attempt (expression .>> ws .>> pstring "is" .>> keywordEnd .>> ws) .>>. pattern
        |>> fun (value, pat) -> Choice2Of2 (pat, value)
    let condition = expression |>> Choice1Of2
    let head = patternBinding <|> condition
    // A head and the branch it guards. The names the head's pattern binds are declared by the branch, which is
    // the body of the match arm the head becomes.
    let headThen (branchParser: Parser<Expression, ParseState>) =
        collectingNames head .>>. branchParser
        >>= fun ((head, names), branch) -> declare ArmBody branch.Id names >>% (head, branch)
    // A left-out `else` is `()` made up at the `then` branch. An if-match's `else` becomes an arm whose wildcard
    // is made up at the `else` branch.
    let desugar span head (thenExpr: Expression) (elseOpt: Expression option) =
        let elseExpr = elseOpt |> Option.defaultWith (fun () -> Expression.from thenExpr UnitExpr)
        match head with
        | Choice1Of2 cond -> Expression.at span (IfExpr(cond, thenExpr, elseExpr))
        | Choice2Of2 (pat, value) ->
            Expression.at span (Match([ value ], [ [ pat ], thenExpr; [ Pattern.at elseExpr.Span WildcardPattern ], elseExpr ]))
    let elseKeyword = pstring "else" >>. keywordEnd
    let conditionArm =
        pstring "|" >>. ws >>. notFollowedBy elseKeyword >>. headThen (ws >>. pstring "->" >>. matchArmBody)
    let elseArm = pstring "|" >>. ws >>. elseKeyword >>. ws >>. pstring "->" >>. matchArmBody
    // Each form builds the if given the span of the whole `if ...`. An if nested in place of a later arm spans
    // from that arm's `|` to the end of the last arm.
    let multiBranch =
        pipe2
            (many1 (attempt (ws >>. withSpan conditionArm)))
            (opt (attempt (ws >>. withSpan elseArm)))
            (fun arms elseOpt span ->
                let lastText = match elseOpt with Some (_, text) -> text | None -> snd (List.last arms)
                let outer, inner = List.head arms, List.tail arms
                let rest =
                    List.foldBack (fun ((head, body), armText) rest ->
                        Some (desugar (SourceSpan.between armText lastText) head body rest)) inner (Option.map fst elseOpt)
                let (head, body), _ = outer
                desugar span head body rest)
    let singleBranch =
        pipe2
            (headThen (ws >>. pstring "then" >>. keywordEnd >>. branch))
            (opt (attempt (ws >>. elseKeyword >>. branch)))
            (fun (head, thenExpr) elseOpt span -> desugar span head thenExpr elseOpt)
    withSpan (pstring "if" >>. keywordEnd >>. ws >>. (multiBranch <|> singleBranch))
    |>> fun (build, span) -> build span
    <?> "if expression"

let workflowBindExpr =
    attempt (
        spanned (
            pipe2
                (identifierNoWs .>> pchar '!' .>> ws)
                expression
                (fun name expr -> WorkflowBindExpr(name, expr))))
    <?> "workflow bind"

let workflowReturnExpr =
    let isIdentChar c = isLetter c || isDigit c || c = '_'
    attempt (
        spanned (
            pstring "return"
            .>> notFollowedBy (satisfy isIdentChar)
            >>. ws
            >>. expression
            |>> WorkflowReturnExpr))
    <?> "workflow return"

// `try x` applies the workflow keyword `try` to `x`, the same as the older `try! x`; what it does is up to the
// workflow (see the workflows page of the docs). `try` is the only keyword written this way so far. Its value is
// on the same line, and `try` is reserved: `try` alone is not a name.
let workflowKeywordExpr =
    let keywordEnd = notFollowedBy (satisfy (fun c -> Char.IsLetterOrDigit c || c = '_'))
    spanned (
        attempt (pstring "try" .>> keywordEnd) >>. wsInline >>. expression
        |>> fun value -> WorkflowBindExpr("try", value))
    <?> "workflow keyword"

// Use binding parser: `use expr` or `use C(T) = expr`
let useBinding =
    // First try context instance: C(T) = expr or C = expr
    let contextInstance =
        pipe2
            (attempt (withSpan (identifierNoWs .>>. opt (between (pstring "(") (pstring ")") (sepBy1 (ws >>. typeExpr) (pstring "," .>> ws))))))
            (ws >>. pstring "=" >>. ws >>. expression)
            (fun ((name, typeArgsOpt), span) expr ->
                let contextType =
                    match typeArgsOpt with
                    | Some args -> TypeApply(name, args)
                    | None -> TypeName name
                UseContextInstance(TypeExpr.at span contextType, expr))
    attempt contextInstance <|> (expression |>> UseValue)

// Use-in expression: use X in expr
let useInExpr =
    spanned (
        pipe2
            (pstring "use" >>. ws >>. useBinding .>> ws .>> pstring "in" .>> ws)
            expression
            (fun binding body -> UseIn(binding, body)))
    <?> "use-in expression"

// Primary expression (literals, identifiers, function calls, lambdas, parenthesized expressions)
// Version without trailing whitespace consumption (for use in block contexts)

// Context member access: C(T).field or C(T).field(args) - qualified access to context functions
// ONLY matches when type parameters are present (parens required). Context names are capitalised, which is
// what tells `ToString(a).toString(x)` apart from a member of a call's result such as `openFile(path).contents`.
let contextMemberAccess =
    let contextName =
        identifierNoWs >>= fun name ->
            if Char.IsUpper name.[0] then preturn name else fail "context name (capitalised)"
    // Parse a context type application with required type args: C(T)
    let contextTypeApply =
        spannedType (
            pipe2
                contextName
                (between (pstring "(") (pstring ")") (sepBy1 (ws >>. typeExpr) (pstring "," .>> ws)))
                (fun name args -> TypeApply(name, args)))
    // Parse args as tuple-like: (expr, expr, ...)
    let callArgs =
        between (pstring "(") (pstring ")") 
            (opt (ws >>. (sepBy1 (ws >>. expression) (pstring "," .>> ws)) .>> ws))
    spanned (
        pipe3
            (contextTypeApply .>> pstring ".")
            identifierNoWs
            (opt callArgs)
            (fun ctxType field argsOpt ->
                match argsOpt with
                | Some (Some args) -> ContextMemberCall(ctxType, field, args)
                | Some None -> ContextMemberCall(ctxType, field, [])  // empty parens like f()
                | None -> ContextMemberAccess(ctxType, field)))

// A name applied to a value without parentheses, as in `yield 1`, is how a workflow keyword is used (see the
// workflows page of the docs). Only `try` is parsed that way so far, and without this any other name parses as
// an identifier and the failure surfaces far from here. So it fails at the name, and notes the failure in the
// state, which `parseModuleFileWithNames` reports in place of FParsec's error when the parse fails. Workflow
// blocks are lambdas, so only a name inside a lambda is taken for a keyword: outside one, a line such as
// `pattern string -> ...` is some other mistake.
let private workflowKeywordApplication =
    let reservedWords =
        set [ "and"; "as"; "context"; "def"; "else"; "except"; "export"; "for"; "if"; "import"; "in"; "is"; "match"
              "module"; "mut"; "not";"or"; "private"; "rec"; "return"; "then"; "try"; "type"; "use"; "where"; "with" ]
    let operandStart c = isLetter c || isDigit c || c = '_' || c = '"' || c = '#' || c = '(' || c = '['
    let keyword =
        identifierNoWs >>= fun name ->
            if Char.IsLower name.[0] && not (reservedWords.Contains name) then preturn name else pzero
    // What follows `x` in `x then` or `x and y` is the rest of an enclosing construct, not a value.
    let operand = followedBy (satisfy operandStart) >>. notFollowedBy (identifierNoWs >>= fun word ->
        if reservedWords.Contains word then preturn () else pzero)
    userStateSatisfies (fun state -> state.LambdaDepth > 0)
    >>. getPosition .>>. lookAhead (keyword .>> skipMany1 (anyOf " \t") .>> operand)
    .>>. getUserState
    >>= fun ((position, name), state) ->
        let message =
            $"`{name}` is applied to a value without parentheses, which is how a workflow keyword is used. The "
            + $"only workflow keywords so far are `try` and `return`. To call a function, write `{name}(...)`."
        match state.WorkflowKeyword.Value with
        | Some (furthest, _) when furthest.Index >= position.Index -> ()
        | _ -> state.WorkflowKeyword.Value <- Some (position, message)
        failFatally message

let primaryExprNoWs =
    choice [
        attempt interpolatedStringExprNoWs
        attempt workflowReturnExpr
        attempt workflowBindExpr
        workflowKeywordExpr
        attempt useInExpr
        attempt unitExpr
        attempt ifExpr  // if-then-else must be early to avoid partial matches
        attempt forExpr
        attempt matchExpr
        attempt lambda
        attempt contextMemberAccess  // C(T).field - must come before functionCall
        attempt functionCall
        attempt tagExpr  // #tag or #tag(expr)
        spanned (literalNoWs |>> LiteralExpr)
        listExpr
        tupleOrParenExpr
        workflowKeywordApplication
        notImplementedExpr
        spanned (identifierNoWs |>> IdentifierExpr)
    ]

do simpleExpressionRef := primaryExprNoWs

// Member access: parse base expression, then chain .field.field.field... or .0.1.2...
let memberAccessChain =
    let tupleIndex = many1Satisfy isDigit
    let fieldOrIndex = identifierNoWs <|> tupleIndex
    // `record with field = value, ...` copies a record with some fields replaced. The parser represents it
    // as a call to "__with" with the record and a record of the updated fields, which the type checker and
    // backend handle as a built-in (the same way `not` is a call to "not"). The record of updates spans from the
    // first update to the last.
    let withClause =
        let keywordEnd = notFollowedBy (satisfy (fun c -> Char.IsLetterOrDigit c || c = '_'))
        let update =
            (identifierNoWs .>> wsInline .>> pstring "=" .>> ws) .>>. expression
            |>> fun (field, value) -> NamedField(field, value)
        attempt (wsInline >>. pstring "with" >>. keywordEnd >>. ws >>. withSpan (sepBy1 update (pstring "," .>> ws)))
    // `..` is the range operator, not a member access.
    let dotField = attempt (pstring "." >>. notFollowedBy (pchar '.') >>. withSpan fieldOrIndex)
    // The argument a shorthand lambda's body leaves out is the first operand of that body, as `x` is in
    // `x % 2 == 0` or `x.name`. It takes up no text, so it is made up at the operator or `.` written in its place.
    let implicitArgumentOperand =
        getUserState >>= fun state ->
            match state.ImplicitArgumentNext with
            | Some span ->
                setUserState { state with ImplicitArgumentNext = None }
                >>% (Expression.at span (IdentifierExpr implicitArgument), span)
            | None -> pzero
    // Each access spans from the start of the base's text, so `(a + b).x` takes in the opening parenthesis.
    (implicitArgumentOperand <|> withSpan primaryExprNoWs) >>= fun (baseExpr, baseText) ->
        many dotField >>= fun fields ->
            let accessed, accessedText =
                fields
                |> List.fold (fun ((expr: Expression), text) (field, fieldSpan) ->
                    let span = SourceSpan.between text fieldSpan
                    Expression.at span (MemberAccess(expr, field, Some fieldSpan)), span) (baseExpr, baseText)
            opt withClause |>> function
                | Some (updates, updatesSpan) ->
                    let updated = Expression.at updatesSpan (RecordExpr updates)
                    Expression.at (SourceSpan.between accessedText updatesSpan) (FunctionCall("__with", None, [ accessed; updated ]))
                | None -> accessed

let primaryExpr = memberAccessChain .>> wsInline  // Consume trailing inline ws for operator parsing

// Boolean operations are the keywords `not`, `and` and `or` (lowest precedence; see the operators page of
// the docs). A keyword operator must not swallow the start of an identifier such as `order` or `android`.
// The symbolic `&&` and `||` predate the keywords and are kept as aliases that build the same tree.
let private keywordEnd = notFollowedBy (satisfy (fun c -> Char.IsLetterOrDigit c || c = '_'))
let private keywordOperatorEnd = keywordEnd >>. ws

// `not` binds tighter than every infix operator, so `not a == b` is `(not a) == b`, as `!` does elsewhere. That
// makes it part of the operand it applies to, so it is parsed here rather than by the operator precedence parser,
// and spans from the keyword.
let private operand : Parser<Operand, ParseState> =
    let notKeyword = attempt (withSpan (pstring "not" .>> keywordEnd)) .>> ws
    pipe2 (many notKeyword) (withSpan primaryExpr) (fun nots term ->
        List.foldBack (fun (_, notSpan) ((inner: Expression), innerText) ->
            let span = SourceSpan.between notSpan innerText
            Expression.at span (FunctionCall("not", None, [ inner ])), span) nots term)

opp.TermParser <- operand

// Define operators with precedence
// Multiplicative operators (higher precedence)
opp.AddOperator(InfixOperator("*", ws, 7, Associativity.Left, binary "*"))
opp.AddOperator(InfixOperator("/", ws, 7, Associativity.Left, binary "/"))
opp.AddOperator(InfixOperator("%", ws, 7, Associativity.Left, binary "%"))

// Additive operators
opp.AddOperator(InfixOperator("+", ws, 6, Associativity.Left, binary "+"))
// `-` must not take the start of a `->` (as in `for 1..2 -> ...`) or of a `--` comment.
let private minusEnd = notFollowedBy (anyOf ">-")
opp.AddOperator(InfixOperator("-", minusEnd >>. ws, 6, Associativity.Left, binary "-"))

// Concatenation (strings, lists): binds looser than arithmetic, tighter than comparison
opp.AddOperator(InfixOperator("++", ws, 5, Associativity.Right, binary "++"))

// Comparison operators (lower precedence)
opp.AddOperator(InfixOperator("<", ws, 4, Associativity.Left, binary "<"))
opp.AddOperator(InfixOperator(">", ws, 4, Associativity.Left, binary ">"))
opp.AddOperator(InfixOperator("<=", ws, 4, Associativity.Left, binary "<="))
opp.AddOperator(InfixOperator(">=", ws, 4, Associativity.Left, binary ">="))

// Equality operators
// A bounded range `a..b` (see the ranges page) is represented as a call to the built-in "__range". It binds
// looser than arithmetic, so `n - 1..n + 1` is `(n - 1)..(n + 1)`. `...` is list spread, not a range.
opp.AddOperator(InfixOperator("..", notFollowedBy (pchar '.') >>. ws, 4, Associativity.Left, fun (x, xText) (y, yText) ->
    let span = SourceSpan.between xText yText
    Expression.at span (FunctionCall("__range", None, [ x; y ])), span))
opp.AddOperator(InfixOperator("==", ws, 3, Associativity.Left, binary "=="))
opp.AddOperator(InfixOperator("!=", ws, 3, Associativity.Left, binary "!="))

opp.AddOperator(InfixOperator("and", keywordOperatorEnd, 2, Associativity.Left, binary "and"))
opp.AddOperator(InfixOperator("or", keywordOperatorEnd, 1, Associativity.Left, binary "or"))
opp.AddOperator(InfixOperator("&&", ws, 2, Associativity.Left, binary "and"))
opp.AddOperator(InfixOperator("||", ws, 1, Associativity.Left, binary "or"))

// Note: Comma is NOT an operator in the OPP
// Commas are handled explicitly in tuple/record literals and function calls
// For bare tuple expressions (def x = 1, 2), we handle it in defStatement

// Pipe parser - handles expr \func or expr \func(args)
let pipeTarget =
    pipe3
        qualifiedIdentifierWithSpan
        (opt parenArguments)
        (opt (attempt (wsInline >>. lambda)))
        (fun (funcName, funcRange) argOpt trailingLambdaOpt ->
            let baseArgs = match argOpt with Some arg -> [arg] | None -> []
            let args =
                match trailingLambdaOpt with
                | Some lambdaExpr -> baseArgs @ [lambdaExpr]
                | None -> baseArgs
            (funcName, funcRange, args)
        )

// Inline operator precedence parser (no newlines in operator whitespace)
let oppInline = new OperatorPrecedenceParser<Operand, unit, ParseState>()
oppInline.TermParser <- withSpan primaryExpr

oppInline.AddOperator(InfixOperator("*", wsInline, 7, Associativity.Left, binary "*"))
oppInline.AddOperator(InfixOperator("/", wsInline, 7, Associativity.Left, binary "/"))

oppInline.AddOperator(InfixOperator("+", wsInline, 6, Associativity.Left, binary "+"))
oppInline.AddOperator(InfixOperator("-", minusEnd >>. wsInline, 6, Associativity.Left, binary "-"))
oppInline.AddOperator(InfixOperator("++", wsInline, 5, Associativity.Right, binary "++"))

oppInline.AddOperator(InfixOperator("<", wsInline, 4, Associativity.Left, binary "<"))
oppInline.AddOperator(InfixOperator(">", wsInline, 4, Associativity.Left, binary ">"))
oppInline.AddOperator(InfixOperator("<=", wsInline, 4, Associativity.Left, binary "<="))
oppInline.AddOperator(InfixOperator(">=", wsInline, 4, Associativity.Left, binary ">="))

oppInline.AddOperator(InfixOperator("==", wsInline, 3, Associativity.Left, binary "=="))
oppInline.AddOperator(InfixOperator("!=", wsInline, 3, Associativity.Left, binary "!="))

/// `expr \f \g(a)`: each pipe spans from the start of `expr`'s text to the end of its own target.
let private applyPipes (value: Operand) pipes =
    pipes
    |> List.fold (fun ((acc: Expression), accText) ((funcName, nameSpan, args), targetSpan) ->
        let span = SourceSpan.between accText targetSpan
        Expression.at span (Pipe(acc, funcName, nameSpan, args)), span) value
    |> fst

let private pipeLeadingWs =
    skipMany (skipAnyOf " \t") >>.
    skipMany (attempt (newline >>. skipMany (skipAnyOf " \t")))

let pipeExpr =
    let pipeOp = pipeLeadingWs >>. pstring "\\" >>. wsInline >>. withSpan pipeTarget
    pipe2
        opp.ExpressionParser
        (many (attempt (pipeOp .>> wsInline)))
        applyPipes
    |> withExcept pipeLeadingWs

let inlinePipeExpr =
    let pipeOp = pstring "\\" >>. wsInline >>. withSpan pipeTarget
    pipe2
        oppInline.ExpressionParser
        (many (attempt (pipeOp .>> wsInline)))
        applyPipes
    |> withExcept wsInline

do inlineExpressionRef := inlinePipeExpr

// Expression parser that stops at newlines (for use in blocks)
do exprWithoutCrossingNewlinesRef :=
    attempt matchExpr <|> inlinePipeExpr

// Wire up the expression parser with piping support
// Prefer match expressions first to avoid parsing "match" as an identifier.
do expressionRef := (attempt matchExpr <|> pipeExpr)

// Wire up the lambda parser
do 
    let parameter =
        getPosition .>>. typedIdentifier >>= fun (start, (name, typeOpt)) -> noteName start name >>% (name, typeOpt)
    // A parameter may be bracketed, `{ (x: int) -> x + 1 }`, so the arrow after its annotation ends the parameters
    // rather than making the annotation a function type.
    let bracketedParameter = between (pstring "(" >>. wsInline) (wsInline >>. pstring ")") parameter
    // A parameter may be a pattern, `{ (a, b) -> a + b }`, `{ $Email(s) -> s }`, with the names it binds. A name or
    // `_` alone is an ordinary parameter.
    let patternParameter =
        collectingNames pattern >>= fun (parameter, names) ->
            match parameter.Kind with
            | IdentifierPattern _
            | WildcardPattern -> fail "a name, not a pattern"
            | _ -> preturn (Choice2Of2(parameter, names))
    let paramList =
        sepBy
            (attempt (bracketedParameter |>> Choice1Of2) <|> attempt patternParameter <|> (parameter |>> Choice1Of2))
            (pstring "," .>> ws)
    let arrow = wsInline >>. pstring "->" .>> wsInline
    
    // Lambda body: allow multi-line statement blocks or a single expression
    let blockBody =
        let matchStatement = wsInline >>. spannedStatement (matchExpr |>> ExprStatement) |>> List.singleton
        let statementLine =
            attempt (
                ws >>.
                (attempt matchStatement <|> statementItems)
            )
        let multiLine =
            attempt (
                skipMany (skipAnyOf " \t\r") >>. newline >>.
                ws >>.
                blockSpanned (many1 statementLine |>> List.concat) .>> ws
            )

        let singleLine =
            skipMany (skipAnyOf " \t\r\n") >>.
            expression .>>
            skipMany (skipAnyOf " \t\r\n")

        attempt multiLine <|> singleLine
    
    // Each form builds the lambda's kind given the span of the whole `{ ... }`, which is also where the
    // parameters and expressions a shorthand stands for are made up.
    // A pattern parameter is taken apart by a match with one arm at the top of the body, over the parameter that
    // stands in its place (`patternArgument`), so the pattern must be total as a destructuring def's is. The arm's
    // body, the lambda's, declares the names the patterns bind. `{ () -> ... }` is `{ ... }`: unit is what a lambda
    // without parameters takes.
    let lambdaWithParams =
        pipe2 (paramList .>> arrow .>> skipMany (skipAnyOf " \t")) blockBody (fun parameters body -> parameters, body)
        >>= fun (parameters, body) ->
            let destructured =
                parameters
                |> List.indexed
                |> List.choose (function
                    | index, Choice2Of2(pattern, names) -> Some(index + 1, pattern, names)
                    | _ -> None)
            match parameters, destructured with
            | [ Choice2Of2({ Kind = UnitPattern }, _) ], _ -> preturn (fun _ -> Lambda([], body))
            | _, [] -> preturn (fun _ -> Lambda(parameters |> List.choose (function Choice1Of2 named -> Some named | _ -> None), body))
            | _ ->
                let named =
                    parameters
                    |> List.mapi (fun index parameter ->
                        match parameter with
                        | Choice1Of2 named -> named
                        | Choice2Of2 _ -> patternArgument (index + 1), None)
                declare ArmBody body.Id (destructured |> List.collect (fun (_, _, names) -> names))
                >>% fun span ->
                    // Each scrutinee is made up where its pattern is written, so a pattern that is not total is
                    // reported there.
                    let scrutinees = destructured |> List.map (fun (position, pattern, _) -> Expression.at pattern.Span (IdentifierExpr (patternArgument position)))
                    let patterns = destructured |> List.map (fun (_, pattern, _) -> pattern)
                    Lambda(named, Expression.at span (Match(scrutinees, [ patterns, body ])))
    let lambdaNoParams = blockBody |>> (fun body _ -> Lambda([], body))
    // `{ }`, possibly spanning lines, is a lambda that does nothing
    let emptyLambda = skipMany (skipAnyOf " \t\r\n") >>. followedBy (pstring "}") >>% fun span -> Lambda([], Expression.at span UnitExpr)

    // The infix operators written with operator characters (see the operators page of the docs), longer ones before
    // the shorter ones they start with.
    let shorthandOperators = [ "++"; "+"; "-"; "*"; "/"; "%"; "<="; ">="; "<"; ">"; "=="; "!=" ]
    let shorthandOperator =
        notFollowedBy (pstring "->" <|> pstring "--")
        >>. choice (shorthandOperators |> List.map pstring)
        .>> notFollowedBy (anyOf "+-*/%^~<>=!")

    // Shorthand lambda: an operator alone stands for itself, so `{ + }` means `{ a, b -> a + b }`.
    let shorthandBinaryOp =
        shorthandOperator .>> ws .>> followedBy (pstring "}") |>> fun op span ->
            let made kind = Expression.at span kind
            Lambda(
                [ (implicitArgument, None); (implicitSecondArgument, None) ],
                made (BinaryOp(op, made (IdentifierExpr implicitArgument), made (IdentifierExpr implicitSecondArgument))))

    // Shorthand lambda: a body that starts with an operator or a member access has left out its first operand, which is
    // the argument. The rest of the body follows as it would after that operand, so `{ % 2 == 0 }` means
    // `{ x -> x % 2 == 0 }` and `{ .age > 18 }` means `{ x -> x.age > 18 }`.
    let shorthandWithArgument =
        lookAhead (withSpan (attempt shorthandOperator <|> (pstring "." .>> followedBy identifierNoWs)))
        >>= fun (_, operatorSpan) -> updateUserState (fun state -> { state with ImplicitArgumentNext = Some operatorSpan })
        >>. expression .>> skipMany (skipAnyOf " \t\r\n")
        |>> fun body _ -> Lambda([ (implicitArgument, None) ], body)

    // Shorthand match lambda: { | pat -> expr ... }
    let shorthandMatchLambda =
        matchArms |>> (fun arms span ->
            let made kind = Expression.at span kind
            let maxArity =
                arms
                |> List.map (fun (patterns, _) -> patterns.Length)
                |> List.max
            let argNames =
                [1 .. maxArity]
                |> List.map (fun index -> ($"arg{index}", None))
            let scrutinees =
                argNames
                |> List.map (fun (name, _) -> made (IdentifierExpr name))
            Lambda(argNames, made (Match(scrutinees, arms))))

    // The names noted while a lambda is parsed are its parameters: whatever is nested in it takes its own.
    lambdaRef :=
        collectingNames (
            withSpan (
                between
                    (pstring "{" .>> updateUserState (fun state -> { state with LambdaDepth = state.LambdaDepth + 1 }))
                    (pstring "}" .>> updateUserState (fun state -> { state with LambdaDepth = state.LambdaDepth - 1 })) (
                    skipMany (skipAnyOf " \t") >>. choice [
                        attempt emptyLambda
                        attempt shorthandBinaryOp
                        attempt shorthandWithArgument
                        attempt shorthandMatchLambda
                        attempt lambdaWithParams
                        lambdaNoParams
                    ] .>> skipMany (skipAnyOf " \t\r\n")
                )))
        >>= (fun ((build, span), parameters) ->
            let lambda = Expression.at span (build span)
            declare LambdaParameters lambda.Id parameters >>% lambda)
        <?> "lambda expression"

// Wire up the function call parser
do 
    // Trailing lambda: function can be called with a lambda after parentheses
    // Examples: f(a, b) { x -> x + 1 } or f { x -> x + 1 } (no parens needed if only lambda arg)
    // Note: f(a, b) creates single tuple argument: FunctionCall("f", None, [TupleExpr[a; b]])

    let callWithParensAndTrailing =
        spanned (
            pipe3
                qualifiedIdentifierWithSpan
                (between (pstring "(") (pstring ")")
                    (opt (ws >>. (withSpan recordFields |>> fieldsToExpr) .>> ws)))
                (opt (attempt (skipMany (skipAnyOf " \t") >>. lambda)))
                (fun (name, nameSpan) exprOpt trailingLambdaOpt ->
                    let baseArgs = match exprOpt with Some e -> [e] | None -> []
                    match trailingLambdaOpt with
                    | Some lambdaExpr -> FunctionCall(name, nameSpan, baseArgs @ [lambdaExpr])
                    | None -> FunctionCall(name, nameSpan, baseArgs)))

    let callWithOnlyTrailing =
        attempt (
            spanned (
                qualifiedIdentifierWithSpan >>= fun (name, nameSpan) ->
                    skipMany (skipAnyOf " \t") >>.
                    lookAhead (pchar '{') >>.
                    lambda |>> fun lambdaExpr ->
                        FunctionCall(name, nameSpan, [lambdaExpr]))
        )
    
    functionCallRef :=
        callWithOnlyTrailing <|> callWithParensAndTrailing
        <?> "function call"

// Wire up statement parser
do
    let statementTail =
        wsNoNl() >>. opt (attempt (inlineCommentNoNl ())) >>% ()

    let importStatement =
        pstring "import" >>. wsNoNl() >>. importItems |>> ImportStatement

    let useStatement =
        (pstring "use" >>. ws >>. useBinding .>> notFollowedBy (ws >>. pstring "in"))
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
                (parseDefWith lineStart.Column typedQualifiedIdentifier defExprParser)
                (fun exportOpt (name, typeOpt, expr) -> DefStatement(exportOpt.IsSome, name, typeOpt, expr))

    // Each def a destructuring def stands for spans the whole `def (...) = value`.
    let destructuringDefStatement =
        getPosition >>= fun lineStart ->
            withSpan (destructuringDefWith lineStart.Column defExprParser)
            |>> fun ((isExport, bindings), span) ->
                let span = match List.tryHead bindings with Some (_, value) -> endingAt value.Span span | None -> span
                bindings |> List.mapi (fun index (name, value) -> Statement.at span (DefStatement(isExport && index > 0, name, None, value)))
    // `rec name = { ... }` is a local definition that can refer to itself (see the functions page). It is a
    // def whose value is wrapped in a call to the built-in "__rec", which tells the backend to compile the
    // lambda as a function that may call itself.
    let recStatement =
        let keywordEnd = notFollowedBy (satisfy (fun c -> Char.IsLetterOrDigit c || c = '_'))
        getPosition >>= fun lineStart ->
            (pstring "rec" >>. keywordEnd >>. ws >>. getPosition .>>. defNameAndType lineStart.Column typedQualifiedIdentifier)
            >>= fun (start, (name, typeOpt)) ->
                defEqualsContinuation lineStart.Column >>. pstring "=" >>. wsNoNl() >>. defExprParser
                >>= fun expr ->
                    let value = Expression.from expr (FunctionCall("__rec", None, [ expr ]))
                    declare DefValue value.Id [ name, nameSpan start name ] >>% DefStatement(false, name, typeOpt, value)

    let exprStatement =
        wsNoNl()
        >>. exprWithoutCrossingNewlines
        .>> statementTail
        |>> ExprStatement  // Consume trailing inline comments without consuming newlines
    
    statementItemsRef := (attempt destructuringDefStatement <|> (statement |>> List.singleton)) <?> "statement"
    statementRef :=
        spannedStatement (attempt importStatement <|> attempt typeDefStatement <|> attempt defStatement <|> attempt recStatement <|> attempt useStatement <|> exprStatement)
        <?> "statement"

/// Runs `p` and makes what it built a top-level item spanning the text it consumed.
let private spannedItem (p: Parser<TopLevelItemKind, ParseState>) : Parser<TopLevelItem, ParseState> =
    withSpan p |>> fun (kind, span) -> TopLevelItem.at span kind

// Parser for module declaration
let moduleDecl: Parser<TopLevelItem, ParseState> =
    spannedItem (pstring "module" >>. ws >>. identifier |>> ModuleDecl)
    <?> "module declaration"

let private valueDefExprParser =
    attempt (blockExpr()) <|> (wsInline >>. singleOrTuple (sepBy1 expression (pstring "," .>> ws)))

// Parser for value definition (top-level)
let valueDef: Parser<TopLevelItem, ParseState> =
    getPosition >>= fun lineStart ->
        spannedItem (
            pipe2
                (opt (attempt (pstring "export" >>. ws)))
                (parseDefWith lineStart.Column typedQualifiedIdentifier valueDefExprParser)
                (fun exportOpt (name, typeOpt, expr) -> Def(ValueDef(exportOpt.IsSome, name, typeOpt, expr))))
    <?> "value definition"

// Top-level `def (x, y) = ...`, as the defs it desugars into (see `destructuringBindings`). Each spans the whole
// `def (...) = value`.
let valueDestructuringDef: Parser<TopLevelItem list, ParseState> =
    getPosition >>= fun lineStart ->
        withSpan (destructuringDefWith lineStart.Column valueDefExprParser)
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

// Parser for an attribute line such as `@entry`; it annotates the next top-level item
let attributeItem: Parser<TopLevelItem, ParseState> =
    spannedItem (pchar '@' >>. identifierNoWs |>> Attribute)
    <?> "attribute"

let private importLine: Parser<TopLevelItem, ParseState> =
    spannedItem (attempt (pstring "import" >>. wsNoNl() >>. importItems) |>> Import)

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
        (pstring "module" >>. ws >>. identifierNoWs .>> wsNoNl() .>> pstring "=") >>= fun name ->
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

let private qualifyModuleName (prefix: string) (name: string) =
    if String.IsNullOrWhiteSpace prefix then name else $"{prefix}.{name}"

/// `typeExpr` with the types `typeMap` qualifies renamed, and the value of each `typeof` rewritten by `rewriteValue`.
let private rewriteTypeExprInModule (typeMap: Map<string, string>) (rewriteValue: Expression -> Expression) (typeExpr: TypeExpr) : TypeExpr =
    let rec rewrite (t: TypeExpr) = { t with Kind = rewriteKind t.Kind }
    and rewriteKind kind =
        match kind with
        | TypeName name ->
            match typeMap |> Map.tryFind name with
            | Some qualified -> TypeName qualified
            | None -> TypeName name
        | TypeVar _
        | TypeUnit -> kind
        | TypeTuple items -> TypeTuple (items |> List.map rewrite)
        | TypeRecord fields ->
            TypeRecord (
                fields
                |> List.map (function
                    | TypeField(name, isMutable, isOptional, typeOpt, defaultOpt) -> TypeField(name, isMutable, isOptional, typeOpt |> Option.map rewrite, defaultOpt)
                    | TypeMember(name, typeOpt) -> TypeMember(name, typeOpt |> Option.map rewrite)))
        | TypeApply(name, args) ->
            let mappedName = typeMap |> Map.tryFind name |> Option.defaultValue name
            TypeApply(mappedName, args |> List.map rewrite)
        | TypeFunc(inputTy, outputTy) -> TypeFunc(rewrite inputTy, rewrite outputTy)
        | TypeTag(name, payload) -> TypeTag(name, payload |> Option.map rewrite)
        | TypeUnion items -> TypeUnion (items |> List.map rewrite)
        | TypeIntersection items -> TypeIntersection (items |> List.map rewrite)
        | TypeOptional inner -> TypeOptional (rewrite inner)
        | TypeContext items -> TypeContext (items |> List.map rewrite)
        | TypeWithContext(items, inner) -> TypeWithContext(items |> List.map rewrite, rewrite inner)
        | TypeConstraint(name, baseTy, predicate) -> TypeConstraint(name, rewrite baseTy, predicate)
        | TypeWhere(baseTy, predicate) -> TypeWhere(rewrite baseTy, predicate)
        | TypeOf value -> TypeOf (rewriteValue value)
    rewrite typeExpr

let private rewriteExpressionInModule (valueMap: Map<string, string>) (typeMap: Map<string, string>) (expr: Expression) : Expression =
    let rewriteQualifiedHead (name: string) =
        if name.Contains(".") then
            let dot = name.IndexOf('.')
            let head = name.Substring(0, dot)
            let tail = name.Substring(dot + 1)
            match typeMap |> Map.tryFind head with
            | Some qualifiedHead -> $"{qualifiedHead}.{tail}"
            | None -> name
        else
            name

    let rewriteName (name: string) =
        match valueMap |> Map.tryFind name with
        | Some qualified -> qualified
        | None ->
            match typeMap |> Map.tryFind name with
            | Some qualifiedType -> qualifiedType
            | None -> rewriteQualifiedHead name

    let rec rewrite (e: Expression) = { e with Kind = rewriteKind e }
    and rewriteKind e =
        match e.Kind with
        | IdentifierExpr name -> IdentifierExpr(rewriteName name)
        | FunctionCall(name, range, args) -> FunctionCall(rewriteName name, range, args |> List.map rewrite)
        | Lambda(args, body) -> Lambda(args, rewrite body)
        | BinaryOp(op, left, right) -> BinaryOp(op, rewrite left, rewrite right)
        | Pipe(value, funcName, range, args) -> Pipe(rewrite value, rewriteName funcName, range, args |> List.map rewrite)
        | Block statements -> Block (statements |> List.map (fun statement -> { statement with Kind = rewriteStatement statement.Kind }))
        | Match(values, arms) -> Match(values |> List.map rewrite, arms |> List.map (fun (patterns, body) -> patterns |> List.map (Pattern.mapExpressions rewrite), rewrite body))
        | Except(value, arms) -> Except(rewrite value, arms |> List.map (fun (patterns, body) -> patterns |> List.map (Pattern.mapExpressions rewrite), rewrite body))
        | TupleExpr items -> TupleExpr (items |> List.map rewrite)
        | RecordExpr fields ->
            RecordExpr (
                fields
                |> List.map (function
                    | NamedField(name, value) -> NamedField(name, rewrite value)
                    | PositionalField value -> PositionalField (rewrite value)))
        | ListExpr items -> ListExpr (items |> List.map rewrite)
        | TagExpr(name, payload) -> TagExpr(name, payload |> Option.map rewrite)
        | IfExpr(cond, ifTrue, ifFalse) -> IfExpr(rewrite cond, rewrite ifTrue, rewrite ifFalse)
        | MemberAccess(baseExpr, field, range) -> MemberAccess(rewrite baseExpr, field, range)
        | ContextMemberAccess(ctxType, field) -> ContextMemberAccess(rewriteType ctxType, field)
        | ContextMemberCall(ctxType, field, args) -> ContextMemberCall(rewriteType ctxType, field, args |> List.map rewrite)
        | UseIn(binding, body) ->
            let mappedBinding =
                match binding with
                | UseValue value -> UseValue (rewrite value)
                | UseContextInstance(ctxType, value) -> UseContextInstance(rewriteType ctxType, rewrite value)
            UseIn(mappedBinding, rewrite body)
        | WorkflowBindExpr(name, value) -> WorkflowBindExpr(name, rewrite value)
        | WorkflowReturnExpr value -> WorkflowReturnExpr (rewrite value)
        | InterpolatedString parts -> InterpolatedString (parts |> List.map (function | StringText text -> StringText text | StringExpr value -> StringExpr (rewrite value)))
        | UnitExpr
        | LiteralExpr _ -> e.Kind
    and rewriteStatement kind =
        match kind with
        | DefStatement(isExport, name, typeOpt, valueExpr) -> DefStatement(isExport, name, typeOpt |> Option.map rewriteType, rewrite valueExpr)
        | TypeDefStatement(name, modifiers, typeParams, typeExpr) ->
            let mappedParams = typeParams |> List.map (fun (paramName, paramTypeOpt) -> paramName, paramTypeOpt |> Option.map rewriteType)
            TypeDefStatement(name, modifiers, mappedParams, rewriteType typeExpr)
        | ImportStatement items -> ImportStatement items
        | UseStatement binding ->
            let mapped =
                match binding with
                | UseValue boundExpr -> UseValue (rewrite boundExpr)
                | UseContextInstance(ctxType, boundExpr) -> UseContextInstance(rewriteType ctxType, rewrite boundExpr)
            UseStatement mapped
        | ExprStatement bodyExpr -> ExprStatement (rewrite bodyExpr)
    and rewriteType typeExpr = rewriteTypeExprInModule typeMap rewrite typeExpr
    rewrite expr

let rec private expandModuleBlock (prefix: string) (items: TopLevelItem list) : TopLevelItem list =
    let localTypeNames =
        items
        |> List.choose (function
            | Kind (Def (TypeDef(name, _, _, _))) when not (name.Contains(".")) -> Some name
            | _ -> None)
        |> Set.ofList

    let typeMap =
        localTypeNames
        |> Seq.map (fun name -> name, qualifyModuleName prefix name)
        |> Map.ofSeq

    let localValueNames =
        items
        |> List.choose (function
            | Kind (Def (ValueDef(_, name, _, _))) when not (name.Contains(".")) -> Some name
            | _ -> None)
        |> Set.ofList

    let valueMap =
        localValueNames
        |> Seq.map (fun name -> name, qualifyModuleName prefix name)
        |> Map.ofSeq

    let rewriteType = rewriteTypeExprInModule typeMap (rewriteExpressionInModule valueMap typeMap)

    let qualifyDefName (name: string) =
        if name.Contains(".") then
            let dot = name.IndexOf('.')
            let head = name.Substring(0, dot)
            let tail = name.Substring(dot + 1)
            match typeMap |> Map.tryFind head with
            | Some qualifiedHead -> $"{qualifiedHead}.{tail}"
            | None -> qualifyModuleName prefix name
        else
            qualifyModuleName prefix name

    let qualifyTypeName (name: string) =
        if name.Contains(".") then qualifyModuleName prefix name else typeMap |> Map.tryFind name |> Option.defaultValue (qualifyModuleName prefix name)

    items
    |> List.collect (fun item ->
        let keep kind = [ { item with Kind = kind } ]
        match item.Kind with
        | ModuleDecl _ -> []
        | ModuleBlock(name, nested) ->
            // The nested block becomes a module declaration, written where the block was.
            let nestedPrefix = qualifyModuleName prefix name
            TopLevelItem.at item.Span (ModuleDecl nestedPrefix) :: expandModuleBlock nestedPrefix nested
        | Import _
        | Attribute _ -> [ item ]
        | Expr expr -> keep (Expr (rewriteExpressionInModule valueMap typeMap expr))
        | Def (ValueDef(isExport, name, typeOpt, expr)) ->
            keep (Def (ValueDef(isExport, qualifyDefName name, typeOpt |> Option.map rewriteType, rewriteExpressionInModule valueMap typeMap expr)))
        | Def (TypeDef(name, modifiers, typeParams, body)) ->
            let mappedTypeParams = typeParams |> List.map (fun (paramName, paramTypeOpt) -> paramName, paramTypeOpt |> Option.map rewriteType)
            keep (Def (TypeDef(qualifyTypeName name, modifiers, mappedTypeParams, rewriteType body))))

let private expandMidFileModules (items: TopLevelItem list) : TopLevelItem list =
    let rec loop (currentModule: string option) (remaining: TopLevelItem list) (acc: TopLevelItem list) =
        match remaining with
        | [] -> List.rev acc
        | item :: rest ->
            match item.Kind with
            | ModuleDecl name -> loop (Some name) rest (item :: acc)
            | ModuleBlock(name, innerItems) ->
                let prefix =
                    match currentModule with
                    | Some root when not (String.IsNullOrWhiteSpace root) -> qualifyModuleName root name
                    | _ -> name
                let expanded = TopLevelItem.at item.Span (ModuleDecl prefix) :: expandModuleBlock prefix innerItems
                loop currentModule rest ((List.rev expanded) @ acc)
            | _ -> loop currentModule rest (item :: acc)
    loop None items []

// Public API for parsing

/// Why a module could not be parsed, and where.
type ParseError = { Message: string; Span: SourceSpan }

/// What the parser skips that the formatter has to put back, and where it is. Neither span includes the final line
/// break.
type Trivia =
    /// A `--` comment, its text starting with `--`, and spanning to the end of its line. `trailing` when code comes
    /// before it on its line.
    | LineComment of text: string * trailing: bool * span: SourceSpan
    /// A run of this many consecutive blank lines (nothing but spaces and tabs), outside string literals, spanning
    /// from the start of the first to the end of the last.
    | BlankLines of count: int * span: SourceSpan

    member this.Span =
        match this with
        | LineComment(_, _, span)
        | BlankLines(_, span) -> span

/// A module as written, for tools that print it back, such as the formatter.
type SourceModule =
    { /// The top-level items as parsed: unlike `parseModuleFile`, `module name =` blocks are kept as they are, not
      /// expanded into qualified definitions.
      Items: TopLevelItem list
      /// The comments and runs of blank lines, in source order.
      Trivia: Trivia list
      /// The source the spans index into: line endings normalised to `\n`, trailing whitespace removed.
      Source: string }

/// A `::` directive at the top of a script, which says what a package manifest would: `::linter default`.
type Directive =
    { /// The word after `::`: `linter`, say. Empty when there is none.
      Name: string
      /// The words after the name.
      Arguments: string list
      /// The line, from `::` to its end.
      Span: SourceSpan }

/// The lines at the top of a file that are not code: an optional `#!` line, then `::` directives, among blank lines
/// and `--` comments. They end at the first line of code, so they are read without parsing the file, and the parser
/// skips them. `::` only ever follows a name inside brackets, so no line of code starts with it.
module Directives =
    /// Each line of `source` (line breaks `\n`), as the offsets it starts and ends at, without its line break.
    let private lines (source: string) =
        let mutable start = 0
        [ while start <= source.Length do
              let finish = match source.IndexOf('\n', start) with -1 -> source.Length | i -> i
              yield start, finish
              start <- finish + 1 ]

    /// The `#!` line and the directive lines at the top of `source`, by line number, with their offsets.
    let private headerLines (source: string) =
        lines source
        |> List.indexed
        |> List.takeWhile (fun (line, (start, finish)) ->
            let text = source.Substring(start, finish - start)
            let trimmed = text.Trim()
            text.StartsWith "::" || (line = 0 && text.StartsWith "#!") || trimmed = "" || trimmed.StartsWith "--")
        |> List.filter (fun (line, (start, _)) ->
            String.CompareOrdinal(source, start, "::", 0, 2) = 0 || (line = 0 && String.CompareOrdinal(source, start, "#!", 0, 2) = 0))

    let private normalize (source: string) = source.Replace("\r\n", "\n").Replace("\r", "\n")

    /// The directives at the top of the file at `filePath`, whose text is `source`.
    let read (filePath: string) (source: string) : Directive list =
        let source = normalize source
        headerLines source
        |> List.filter (fun (_, (start, _)) -> String.CompareOrdinal(source, start, "::", 0, 2) = 0)
        |> List.map (fun (line, (start, finish)) ->
            let text = source.Substring(start, finish - start).TrimEnd()
            let name, arguments =
                match text.Substring(2).Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries) |> List.ofArray with
                | name :: arguments -> name, arguments
                | [] -> "", []
            { Name = name
              Arguments = arguments
              Span = { File = filePath; Start = start; End = start + text.Length; StartLine = line; StartColumn = 0 } })

    /// Where the `#!` line and directives at the top of `source` (line breaks `\n`) end: the end of the last of them,
    /// without its line break, or 0 if it has none.
    let headerEnd (source: string) : int =
        match List.tryLast (headerLines source) with
        | Some (_, (_, finish)) -> finish
        | None -> 0

    /// `source` (line breaks `\n`) with its `#!` line and directives made blank, for the parser, which skips them.
    let blank (source: string) : string =
        match headerLines source with
        | [] -> source
        | header ->
            let chars = source.ToCharArray()
            for _, (start, finish) in header do
                for i in start .. finish - 1 do
                    chars[i] <- ' '
            String chars

let private normalizeSource (input: string) =
    input.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd()

/// Parses normalised source, returning the top-level items as written and the final state.
let private runModuleParser (filePath: string) (normalized: string) : Result<TopLevelItem list * ParseState, ParseError> =
    let initialState = ParseState.empty ()
    // A parse fails at a point: its span is the character there, or nothing at the end of the input.
    let errorAt (position: Position) message =
        { Message = message
          Span = spanOf position (min normalized.Length (int position.Index + 1)) }
    match runParserOnString moduleParser initialState filePath (Directives.blank normalized) with
    | ParserResult.Success(result, state, _) -> Result.Ok (result, state)
    // A directive after the code has started is not skipped, and fails where it starts.
    | ParserResult.Failure(_, error, _) when error.Position.Column = 1L && String.CompareOrdinal(normalized, int error.Position.Index, "::", 0, 2) = 0 ->
        Result.Error (errorAt error.Position "A directive (a `::` line) must come before the first line of code")
    | ParserResult.Failure(errorMsg, error, _) ->
        // The failure of a workflow keyword is backtracked over, and FParsec reports some enclosing construct
        // instead (often the whole def), so it is reported where it happened.
        match initialState.WorkflowKeyword.Value with
        | Some (position, message) ->
            use stream = new CharStream<unit>(normalized, 0, normalized.Length)
            stream.Name <- filePath
            let focused = ParserError(position, null, ErrorMessageList(ErrorMessage.Message message))
            Result.Error (errorAt position (focused.ToString stream))
        | None -> Result.Error (errorAt error.Position errorMsg)

/// Parses the source of the file at `filePath`, as `parseModuleFile` does, together with the names its nodes
/// declare and the text of each `module name =` block in it.
let parseModuleFileWithNames (filePath: string) (input: string) : Result<Module * DeclaredNames * (ModuleName * SourceSpan) list, ParseError> =
    runModuleParser filePath (normalizeSource input)
    |> Result.map (fun (items, state) -> expandMidFileModules items, state.Declared, state.ModuleBlocks)

/// The comments and runs of blank lines in `source`, in source order, given the comments the parser skipped and
/// where the text of its string literals is (each as start and end offsets).
let private collectTrivia (filePath: string) (source: string) (comments: (int * int) list) (stringTexts: (int * int) list) : Trivia list =
    // The offset each line starts at.
    let lineStarts =
        [| yield 0
           for i in 0 .. source.Length - 1 do
               if source[i] = '\n' then yield i + 1 |]
    let lineEnd line =
        if line + 1 < lineStarts.Length then lineStarts[line + 1] - 1 else source.Length
    let spanAt line start finish =
        { File = filePath; Start = start; End = finish; StartLine = line; StartColumn = start - lineStarts[line] }
    let lineOf offset =
        match Array.BinarySearch(lineStarts, offset) with
        | found when found >= 0 -> found
        | notFound -> ~~~notFound - 1
    let isBlank line =
        source.AsSpan(lineStarts[line], lineEnd line - lineStarts[line]).Trim(" \t".AsSpan()).IsEmpty
    // A line whose preceding line break is inside a string literal is part of that string.
    let inString line =
        line > 0 && stringTexts |> List.exists (fun (start, finish) -> start <= lineStarts[line] - 1 && lineStarts[line] - 1 < finish)

    let commentTrivia =
        comments
        // FParsec restores the state when it backtracks, so a comment is noted once; this is only a safety net.
        |> List.distinct
        |> List.map (fun (start, finish) ->
            let line = lineOf start
            let before = source.AsSpan(lineStarts[line], start - lineStarts[line])
            LineComment(source.Substring(start, finish - start), not (before.Trim(" \t".AsSpan()).IsEmpty), spanAt line start finish))

    let blankTrivia =
        [ if source.Length > 0 then yield! [ 0 .. lineStarts.Length - 1 ] ]
        |> List.filter (fun line -> isBlank line && not (inString line))
        // Consecutive blank lines make one run: each run is a list of its lines, last first.
        |> List.fold
            (fun runs line ->
                match runs with
                | (previous :: _ as run) :: rest when previous = line - 1 -> (line :: run) :: rest
                | _ -> [ line ] :: runs)
            []
        |> List.map (fun run ->
            let first = List.last run
            let last = List.head run
            BlankLines(run.Length, spanAt first lineStarts[first] (lineEnd last)))

    commentTrivia @ blankTrivia |> List.sortBy (fun (trivia: Trivia) -> trivia.Span.Start)

/// Parses the source of the file at `filePath` as it is written, keeping its comments and blank lines, for tools
/// that print it back (the formatter). The items are not rewritten: see `SourceModule`.
let parseModuleWithTrivia (filePath: string) (input: string) : Result<SourceModule, ParseError> =
    let normalized = normalizeSource input
    runModuleParser filePath normalized
    |> Result.map (fun (items, state) ->
        { Items = items
          Trivia = collectTrivia filePath normalized state.Comments state.StringTexts
          Source = normalized })

/// Parses the source of the file at `filePath`, which is what the spans of the module's nodes refer to. Line
/// endings are normalised to `\n` first, so span offsets are into the normalised source (lines and columns are
/// unaffected).
let parseModuleFile (filePath: string) (input: string) : Result<Module, string> =
    parseModuleFileWithNames filePath input
    |> Result.map (fun (module', _, _) -> module')
    |> Result.mapError (fun error -> error.Message)


/// Parses source that has no file of its own.
let parseModule (input: string) : Result<Module, string> = parseModuleFile "" input

