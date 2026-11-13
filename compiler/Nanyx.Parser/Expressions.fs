/// Expressions: blocks, match, loops, records, lists, tags, operators, pipes, lambdas and calls.
[<AutoOpen>]
module Parser.Program.Expressions

open System
open FParsec
// After the libraries, so that the parser's names shadow theirs (identifier, Attribute) as they did in one file.
open Parser.Program

// The forward refs filled in earlier files, in the order they were filled when the parser was one file. In a
// library a file's top-level code runs only when one of its own values is first used, so this file, which every
// parse goes through (see `TopLevel`), fills them.
do fillPatternRef ()
do fillTypeRefs ()

// Interpolated double-quoted strings with {expr} holes. `\{` is a brace in the text (see `StringEscapes`).
let interpolatedStringExprNoWs: Parser<Expression, ParseState> =
    let textPart = stringText (doubleQuotedText "{") |>> StringText
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
    attempt (blockExpr()) <|> (wsInline >>. exprWithoutCrossingNewlines)

let typeDefCore: Parser<Definition, ParseState> =
    let typeModifiers =
        many (attempt (
            choice [
                pstring "export" >>. ws >>% Export
                pstring "context" >>. ws >>% Context
            ]))

    let typeParams =
        between (pstring "(") (pstring ")") (sepBy1 (wsInline >>. typeParam) (pstring "," .>> wsInline))

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

    // `scoped export context $Memory = ()`: `scoped` comes first, then `export`.
    let contextModifiers =
        opt (attempt (withSpan (pstring "scoped" .>> keywordEnd) .>> ws)) .>>. opt (attempt (pstring "export" >>. ws))
        |>> fun (scopedOpt, exportOpt) ->
            (scopedOpt |> Option.map (snd >> Scoped) |> Option.toList) @ (if exportOpt.IsSome then [Export] else [])

    let contextDef =
        pipe5
            contextModifiers
            (pstring "context" >>. ws >>. (getPosition .>>. contextName))
            (opt (attempt (wsInline >>. typeParams)))
            (wsInline >>. pstring "=" >>. ws >>. opt (attempt (pstring "private" >>. ws)))
            typeExprWithWhere
            (fun baseMods (start, name) paramsOpt privateOpt typeValue ->
                let typeParams = defaultArg paramsOpt []
                let modifiers = applyPrivate (Context :: baseMods) privateOpt
                (start, name), TypeDef(name, modifiers, typeParams, typeValue))
        >>= declaringTypeName

    let typeDefWithModifiers =
        pipe5
            typeModifiers
            (pstring "type" >>. ws >>. (getPosition .>>. typeNameIdentifier))
            (opt (attempt (wsInline >>. typeParams)))
            (wsInline >>. pstring "=" >>. ws >>. opt (attempt (pstring "private" >>. ws)))
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

/// The indentation of the line the first of a run of arms is on, read without consuming anything. It is what
/// `armIndentedFrom` measures the later arms against.
let private firstArmIndent: Parser<int64, ParseState> =
    lookAhead (ws >>. currentLineIndent)

/// `arm`, after the whitespace before it, unless its `|` starts a line indented less than `armsIndent`. Such an
/// arm belongs to an enclosing match, so that a match (or a multi-branch if) nested in an arm's body ends at the
/// arms indented back to the outer one:
///
///   match xs
///     | [a] ->
///         match a
///           | 0 -> "zero"
///           | _ -> "one"
///     | _ -> "other"
let private armIndentedFrom (armsIndent: int64) (arm: Parser<'a, ParseState>) : Parser<'a, ParseState> =
    ws >>. getPosition .>>. currentLineIndent >>= fun (start, lineIndent) ->
        if start.Column = lineIndent && lineIndent < armsIndent then
            fail "an arm indented less than the first arm belongs to an enclosing match"
        else
            arm

/// One or more arms, each possibly an or-pattern, flattened into plain arms. The arms end at a `|` that starts a
/// line indented less than the line the first arm is on (see `armIndentedFrom`).
let matchArms : Parser<(Pattern list * Expression) list, ParseState> =
    firstArmIndent >>= fun armsIndent ->
        many1 (attempt (armIndentedFrom armsIndent matchArmAlternatives)) |>> List.concat

// `value except pattern -> replacement`, or `value except` followed by `| pattern -> replacement` arms as a match
// has them, replaces the cases of `value` the arms match and passes every other case through. It binds looser
// than every operator and pipe, and may start a line of its own, as a pipe may: `xs \find(p)` and then
// `except #notFound -> #none` on the next line. Its arms take one value each.
let private exceptArms : Parser<(Pattern list * Expression) list, ParseState> =
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
// `for xs, 0.. | x, i -> ...` zips: each arm matches one element of each iterable. All are represented as a call
// to the built-in "__for" with the iterables and a lambda of one element of each (see `ForSyntax`), which the type
// checker and backend handle as a loop.
let forExpr : Parser<Expression, ParseState> =
    let elements count = List.init count (fun index -> (ForSyntax.element (index + 1), None))
    // The whitespace before the first arm is skipped outside the span, which starts at its `|`.
    let withArms count =
        ws >>. withSpan matchArms |>> fun (arms, span) ->
            let scrutinees = elements count |> List.map (fun (name, _) -> Expression.at span (IdentifierExpr name))
            Expression.at span (Lambda(elements count, Expression.at span (Match(scrutinees, arms)), false))
    let withoutElement count =
        ws >>. withSpan (pstring "->" >>. matchArmBody) |>> fun (body, span) ->
            Expression.at span (Lambda(elements count, body, false))
    let iterables = sepBy1 expression (attempt (ws >>. pchar ',' >>. ws))
    spanned (
        (pstring "for" >>. keywordEnd >>. ws >>. iterables) >>= fun iterables ->
            (attempt (withArms iterables.Length) <|> withoutElement iterables.Length)
            |>> fun body -> FunctionCall(ForSyntax.builtIn, None, iterables @ [ body ]))
    <?> "for loop"

// `while condition -> body` runs the body for as long as the condition is true, evaluating it again before each time
// round. It is a call to the built-in "__while" with the condition and the body (see `WhileSyntax`).
let whileExpr : Parser<Expression, ParseState> =
    spanned (
        pstring "while" >>. keywordEnd >>. ws >>. expression .>>. (ws >>. pstring "->" >>. matchArmBody)
        |>> fun (condition, body) -> FunctionCall(WhileSyntax.builtIn, None, [ condition; body ]))
    <?> "while loop"

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
        let sameLine = wsInline >>. sepBy1 expression (pstring "," .>> ws) |>> fun scrutinees -> scrutinees, None
        let indentedBlock matchIndent =
            lookAhead (wsInline >>. newline >>. wsInline >>. getPosition) >>= fun blockStart ->
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
let opp = new OperatorPrecedenceParser<Operand, SourceSpan, ParseState>()


let private binary op (opSpan: SourceSpan) ((left, leftText): Operand) ((right, rightText): Operand) : Operand =
    let span = SourceSpan.between leftText rightText
    Expression.at span (BinaryOp(op, Some opSpan, left, right)), span

// Record field parser: either "name = expr" or just "expr"
let recordField =
    (attempt (
        pipe2
            (nameNoWs .>> ws .>> pstring "=" .>> ws)
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

// The arguments of a call `f(fields) { ... }`. The trailing lambda is the last positional argument, as it is in a pipe
// (see `pipeCallArguments`): `f(a, b) { ... }` is `f(a, b, { ... })`, and `f(a, y = 1) { ... }` is
// `f(a, { ... }, y = 1)`.
let private withTrailingLambda (fields, span) (lambda: Expression) =
    let positional, named = fields |> List.partition (function PositionalField _ -> true | NamedField _ -> false)
    match named with
    | [] -> (positional |> List.choose (function PositionalField e -> Some e | NamedField _ -> None)) @ [ lambda ]
    | _ -> [ Expression.at span (RecordExpr(positional @ [ PositionalField lambda ] @ named)) ]

// The fields in parentheses after a name, as in a call, a tag payload or a pipe target: `(a, b)`, `(x = 1)`.
let private parenFields =
    noteFailureInside (between (pstring "(") (pstring ")") (ws >>. withSpan recordFields .>> ws))

// Arguments in parentheses after a name, as one expression (see `fieldsToExpr`).
let internal parenArguments = parenFields |>> fieldsToExpr

/// `???`, the placeholder for code not written yet. It is read as a name no other code can write, so every pass that
/// handles names handles it; the type checker reports it as not implemented.
let notImplemented = "???"

let private notImplementedExpr =
    spanned (pstring notImplemented >>% IdentifierExpr notImplemented)

// Unit literal: ()
let unitExpr =
    spanned (between (pstring "(") (ws >>. pstring ")") (preturn ()) |>> fun _ -> UnitExpr)

// Tuple/Record or parenthesized expression parser
// Handles: (x), (x, y), (x = 1), (x = 1, y = 2), (x = 1, 2), etc. With a spread, `(...a, f = v, ...b)`, it is a call
// to the built-in "__spread" (see `SpreadSyntax`), whose other fields are all named.
let tupleOrParenExpr =
    let part = (pstring "..." >>. ws >>. expression |>> Choice2Of2) <|> (recordField |>> Choice1Of2)
    (withSpan (
        noteFailureInside (
            between
                (pstring "(")
                (ws >>. pstring ")")
                (byCommaOrLine part)))
    >>= fun (parts, span) ->
        let fields = parts |> List.choose (function Choice1Of2 field -> Some field | Choice2Of2 _ -> None)
        if fields.Length = parts.Length then preturn (fieldsToExpr (fields, span))
        elif fields |> List.exists (function PositionalField _ -> true | NamedField _ -> false) then
            fail "a record with a spread names each of its other fields, as `name = value`"
        else
            let parts = parts |> List.map (function Choice1Of2 field -> field | Choice2Of2 spread -> PositionalField spread)
            preturn (Expression.at span (FunctionCall(SpreadSyntax.builtIn, None, [ Expression.at span (RecordExpr parts) ]))))
    <?> "tuple or record"

// List expression parser. `...expr` splices a list in place: `[a, ...xs, b]` is desugared to
// `[a] ++ xs ++ [b]`, so the rest of the compiler only ever sees list literals and concatenation. Like a record's
// fields, the items are separated by commas or written one per line, and the `]` may start a line:
//
//   [
//     a
//     ...xs
//   ]
//
// When every item is an entry, `key => value`, the literal is a map: a call to the prelude's `__map` with the list of
// the entries as tuples (see `MapSyntax`).
let listExpr =
    let item =
        (pstring "..." >>. ws >>. expression |>> Choice2Of2)
        <|> (expression .>>. opt (attempt (wsInline >>. pstring "=>") >>. ws >>. expression) |>> Choice1Of2)
    let list (items: Choice<Expression, Expression> list) (span: SourceSpan) =
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
        | _ -> segments |> List.reduceBack (fun left right -> Expression.at span (BinaryOp("++", None, left, right)))
    withSpan (
        noteFailureInside (
            between
                (pstring "[")
                (ws >>. pstring "]")
                (ws >>. opt (byCommaOrLine item) |>> Option.defaultValue [])))
    >>= fun (items, span) ->
        let entries = items |> List.choose (function Choice1Of2 (key, Some value) -> Some (key, value) | _ -> None)
        if entries.IsEmpty then
            items
            |> List.map (function Choice1Of2 (element, _) -> Choice1Of2 element | Choice2Of2 spread -> Choice2Of2 spread)
            |> fun items -> preturn (list items span)
        elif entries.Length = items.Length then
            let tuples = entries |> List.map (fun (key, value) -> Expression.from key (TupleExpr [ key; value ]))
            preturn (Expression.at span (FunctionCall(MapSyntax.builtIn, None, [ Expression.at span (ListExpr tuples) ])))
        else
            fail "a map literal's items are all entries, written `key => value`"

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
// of an enclosing match, and one without an `else` ends at an arm indented less than its first (see
// `armIndentedFrom`).
let ifExpr =
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
        firstArmIndent >>= fun armsIndent ->
            pipe2
                (many1 (attempt (armIndentedFrom armsIndent (withSpan conditionArm))))
                (opt (attempt (armIndentedFrom armsIndent (withSpan elseArm))))
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
            pstring WorkflowSyntax.returnKeyword
            .>> notFollowedBy (satisfy isIdentChar)
            >>. ws
            >>. expression
            |>> WorkflowReturnExpr))
    <?> "workflow return"

// `try x` applies the workflow keyword `try` to `x`, the same as the older `try! x`; what it does is up to the
// workflow (see the workflows page of the docs). `try` is the only keyword written this way so far. Its value is
// on the same line, and `try` is reserved: `try` alone is not a name.
let workflowKeywordExpr =
    spanned (
        attempt (pstring WorkflowSyntax.tryKeyword .>> keywordEnd) >>. wsInline >>. expression
        |>> fun value -> WorkflowBindExpr(WorkflowSyntax.tryKeyword, value))
    <?> "workflow keyword"

// The value a `use` loads: `use addInt`, or an instance written out, `use Add(Point)(zero = ..., ...)` (see
// `UseInstanceSyntax`). `use C(T) = expr`, an older way to load an instance, fails at the context's name, saying what to
// write instead.
let useValue =
    let typeArgs = between (pstring "(") (pstring ")") (sepBy1 (ws >>. typeExpr) (pstring "," .>> ws))
    let instanceHead = withSkippedString (fun head _ -> head) (identifierNoWs .>> opt typeArgs)
    let oldInstance =
        getPosition .>>. lookAhead (instanceHead .>> ws .>> pstring "=" .>> notFollowedBy (anyOf "=>"))
        >>= fun (position, head) ->
            reportFocused position
                ($"`use {head} = ...` is not a way to load an instance: write `use {head}(...)`, or define the instance "
                 + $"with `def name: {head} = (...)` and load it with `use name`.")
    let context =
        let contextName = identifierNoWs >>= fun name -> if Char.IsUpper name.[0] then preturn name else fail "context name (capitalised)"
        spannedType (pipe2 contextName typeArgs (fun name args -> TypeApply(name, args)))
    let instance =
        attempt (withSpan (context .>>. parenFields))
        |>> fun ((context, (fields, membersSpan)), span) ->
            let members = Expression.at membersSpan (RecordExpr fields)
            let definition = Statement.at span (DefStatement(false, UseInstanceSyntax.name, Some context, members))
            let result = Statement.at span (ExprStatement (Expression.at span (IdentifierExpr UseInstanceSyntax.name)))
            Expression.at span (Block [ definition; result ])
    instance <|> oldInstance <|> expression

// `do` followed by an indented block of statements is that block, as in F#: a way to start a block wherever an
// expression goes, for instance to keep the defs in it from the statements after it. It is not a loop. A `do`
// without a block fails at the `do` (see `reportFocused`).
let doExpr =
    attempt (getPosition .>> pstring "do" .>> keywordEnd)
    >>= fun position ->
        attempt (blockExpr ())
        <|> reportFocused position "`do` starts a block: put its statements on the lines after it, indented"
    <?> "do block"

// `use X in body` loads `X` for `body`: an expression, on the same line or the next, or a block of statements on the
// lines after `in`, indented more than the line `use` is on. A block of one expression is just that expression.
let useInExpr =
    let indentedBlock useIndent =
        lookAhead (wsInline >>. newline >>. wsInline >>. getPosition) >>= fun blockStart ->
            if blockStart.Column <= useIndent then
                fail "the block after `in` must be indented more than the line `use` is on"
            else
                blockExpr () |>> fun block ->
                    match block.Kind with
                    | Block [ Kind (ExprStatement expr) ] -> expr
                    | _ -> block
    currentLineIndent >>= fun useIndent ->
        spanned (
            pipe2
                (pstring "use" >>. keywordEnd >>. ws >>. useValue .>> ws .>> pstring "in" .>> keywordEnd)
                (attempt (indentedBlock useIndent) <|> (ws >>. expression))
                (fun value body -> UseIn(value, body)))
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
            nameNoWs
            (opt callArgs)
            (fun ctxType field argsOpt ->
                match argsOpt with
                | Some (Some args) -> ContextMemberCall(ctxType, field, args)
                | Some None -> ContextMemberCall(ctxType, field, [])  // empty parens like f()
                | None -> ContextMemberAccess(ctxType, field)))

// A name applied to a value without parentheses, as in `yield 1`, is how a workflow keyword is used (see the
// workflows page of the docs). Only `try` is parsed that way so far, and without this any other name parses as
// an identifier and the failure surfaces far from here. So it fails at the name (see `reportFocused`). Workflow
// blocks are lambdas, so only a name inside a lambda is taken for a keyword: outside one, a line such as
// `pattern string -> ...` is some other mistake.
let private workflowKeywordApplication =
    let reservedWords =
        set [ "and"; "as"; "context"; "def"; "do"; "else";"except"; "export"; "for"; "if"; "import"; "in"; "is"; "match"
              "module"; "mut"; "not";"or"; "private"; "rec"; "return"; "then"; "try"; "type"; "use"; "where"; "while"; "with" ]
    let operandStart c = isLetter c || isDigit c || c = '_' || c = '"' || c = '#' || c = '(' || c = '['
    let keyword =
        identifierNoWs >>= fun name ->
            if Char.IsLower name.[0] && not (reservedWords.Contains name) then preturn name else pzero
    // What follows `x` in `x then` or `x and y` is the rest of an enclosing construct, not a value.
    let operand = followedBy (satisfy operandStart) >>. notFollowedBy (identifierNoWs >>= fun word ->
        if reservedWords.Contains word then preturn () else pzero)
    userStateSatisfies (fun state -> state.LambdaDepth > 0)
    >>. getPosition .>>. lookAhead (keyword .>> skipMany1 (anyOf " \t") .>> operand)
    >>= fun (position, name) ->
        reportFocused position
            ($"`{name}` is applied to a value without parentheses, which is how a workflow keyword is used. The "
             + $"only workflow keywords so far are `try` and `return`. To call a function, write `{name}(...)`.")

let primaryExprNoWs =
    choice [
        attempt interpolatedStringExprNoWs
        attempt workflowReturnExpr
        attempt workflowBindExpr
        workflowKeywordExpr
        mutExpr
        attempt useInExpr
        attempt unitExpr
        attempt ifExpr  // if-then-else must be early to avoid partial matches
        doExpr
        attempt forExpr
        attempt whileExpr
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
        spanned (nameNoWs |>> IdentifierExpr)
    ]


// Member access: parse base expression, then chain .field.field.field... or .0.1.2...
let memberAccessChain =
    let tupleIndex = many1Satisfy isDigit
    let fieldOrIndex = nameNoWs <|> tupleIndex
    // `record with field = value, ...` copies a record with some fields replaced. The parser represents it
    // as a call to "__with" with the record and a record of the updated fields, which the type checker and
    // backend handle as a built-in (the same way `not` is a call to "not"). The record of updates spans from the
    // first update to the last.
    //
    // The updates may instead be written in parentheses, laid out as a record's fields are, separated by commas or
    // one per line: `record with (x = 1, y = 2)`. Then the record of updates spans from `(` to `)`.
    let withClause =
        let update =
            (nameNoWs .>> wsInline .>> pstring "=" .>> ws) .>>. expression
            |>> fun (field, value) -> NamedField(field, value)
        let updateBlock = withSpan (noteFailureInside (between (pstring "(") (ws >>. pstring ")") recordFields))
        let updates = updateBlock <|> withSpan (sepBy1 update (pstring "," .>> ws))
        attempt (wsInline >>. pstring "with" >>. keywordEnd >>. ws >>. updates)
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
                    Expression.at (SourceSpan.between accessedText updatesSpan) (FunctionCall(WithSyntax.builtIn, None, [ accessed; updated ]))
                | None -> accessed

let primaryExpr = memberAccessChain .>> wsInline  // Consume trailing inline ws for operator parsing

// Boolean operations are the keywords `not`, `and` and `or` (see `binaryOperators` and the operators page of the docs).

// `not` binds tighter than every infix operator, so `not a == b` is `(not a) == b`, as `!` does elsewhere. That
// makes it part of the operand it applies to, so it is parsed here rather than by the operator precedence parser,
// and spans from the keyword.
//
// Prefix `-` negates in the same way: `-x * y` is `(-x) * y`. It is written against its operand, so `- x` is not a
// negation, and `-5` stays one literal. In a shorthand lambda's body the operator is the infix one, so `{ -x }` is
// still `{ it -> it - x }`.
let private notKeyword = attempt (withSpan (pstring "not" .>> keywordEnd)) .>> ws |>> fun (_, span) -> NotSyntax.builtIn, span

let private minus =
    userStateSatisfies (fun state -> state.ImplicitArgumentNext.IsNone)
    >>. attempt (withSpan (pchar '-' .>> notFollowedBy (satisfy (fun c -> c = '>' || c = '-' || Char.IsDigit c || Char.IsWhiteSpace c))))
    |>> fun (_, span) -> NegateSyntax.builtIn, span

/// A primary expression after any number of `prefix`es, each a call to the built-in it names.
let private prefixed (prefix: Parser<Identifier * SourceSpan, ParseState>) : Parser<Operand, ParseState> =
    pipe2 (many prefix) (withSpan primaryExpr) (fun prefixes term ->
        List.foldBack (fun (builtIn, prefixSpan) ((inner: Expression), innerText) ->
            let span = SourceSpan.between prefixSpan innerText
            Expression.at span (FunctionCall(builtIn, None, [ inner ])), span) prefixes term)

/// What a `\` applies the value before it to.
type private PipeStep =
    /// `\f`, `\f(args)` or `\f(args) { ... }`: a call of `f` with the value as its first argument.
    | PipeCall of Identifier * SourceSpan option * Expression list
    /// `\match` and then arms, as a match has them: a match on the value.
    | PipeMatch of MatchArm list

let private pipeTarget =
    let matchStep = pstring "match" >>. keywordEnd >>. matchArms |>> PipeMatch
    // Each value in the brackets is an argument of its own, so that a tuple written in brackets of its own is one:
    // `\f(a, b)` passes `a` and `b`, and `\f((a, b))` the tuple. Named fields keep all the fields as one record, and
    // so does a record written first in brackets of its own, which would otherwise be read as that record of named
    // fields (see `pipeCallArguments`): `\f((y = 1))` passes the record.
    let writtenArguments (fields, span) =
        match fields with
        | PositionalField (Kind (RecordExpr _)) :: _ -> [ Expression.at span (RecordExpr fields) ]
        | _ when fields |> List.forall (function PositionalField _ -> true | NamedField _ -> false) ->
            fields |> List.choose (function PositionalField e -> Some e | NamedField _ -> None)
        | _ -> [ Expression.at span (RecordExpr fields) ]
    let callStep =
        pipe3
            qualifiedIdentifierWithSpan
            (opt parenFields)
            (opt (attempt (wsInline >>. lambda)))
            (fun (funcName, funcRange) fieldsOpt trailingLambdaOpt ->
                let baseArgs = match fieldsOpt with Some fields -> writtenArguments fields | None -> []
                let args =
                    match trailingLambdaOpt with
                    | Some lambdaExpr -> baseArgs @ [lambdaExpr]
                    | None -> baseArgs
                PipeCall(funcName, funcRange, args))
    attempt matchStep <|> callStep

/// `expr \f \g(a)`: each pipe spans from the start of `expr`'s text to the end of its own target.
let private applyPipes (value: Operand) pipes =
    pipes
    |> List.fold (fun ((acc: Expression), accText) (step, targetSpan) ->
        let span = SourceSpan.between accText targetSpan
        let kind =
            match step with
            | PipeCall(funcName, nameSpan, args) -> Pipe(acc, funcName, nameSpan, args)
            | PipeMatch arms -> Match([ acc ], arms)
        Expression.at span kind, span) value

let private pipeLeadingWs =
    skipMany (skipAnyOf " \t") >>.
    skipMany (attempt (newline >>. skipMany (skipAnyOf " \t")))

/// The space before a pipe or an `except` that continues an expression statement: spaces on the same line, or line
/// breaks (and blank or comment lines) up to a line indented more than `lineIndent`, the indent of the line the
/// statement starts on. A line indented no more than that starts something else.
let private continuationWs (lineIndent: int64) =
    wsInline
    >>. optional (attempt (
        skipMany1 (attempt (blockLineEnd () >>. wsInline)) >>. getPosition >>= fun position ->
            if position.Column > lineIndent then preturn () else pzero))

/// `value` and then any number of `\` steps, each after `pipeWs`. A pipe binds tighter than every binary operator, so it
/// is part of the operand: `xs \min ?? 0` is `(xs \min) ?? 0`, and `a + b \f` is `a + (b \f)`. A prefix operator is part
/// of the value it pipes: `-x \abs` is `(-x) \abs`.
let private withPipes (pipeWs: Parser<unit, ParseState>) (value: Parser<Operand, ParseState>) : Parser<Operand, ParseState> =
    let pipeOp = pipeWs >>. pstring "\\" >>. wsInline >>. withSpan pipeTarget
    pipe2 value (many (attempt (pipeOp .>> wsInline))) applyPipes

let private operand : Parser<Operand, ParseState> =
    prefixed (notKeyword <|> minus <|> bang) |> withPipes pipeLeadingWs .>> symbolicBoolean

opp.TermParser <- operand

addBinaryOperators opp ws binary

// A bounded range `a..b` (see the ranges page) is represented as a call to the built-in "__range". It binds
// looser than arithmetic, so `n - 1..n + 1` is `(n - 1)..(n + 1)`. `...` is list spread, not a range. A `..` with
// nothing after it on its line but the rest of an enclosing construct is the unbounded `a..` (see `withOpenRange`).
let private rangeHasEnd =
    notFollowedBy (wsInline >>. (skipNewline <|> eof <|> skipAnyOf "|)]},\\" <|> skipString "->" <|> skipString "--"))
opp.AddOperator(InfixOperator("..", operatorSpan ".." (notFollowedBy (pchar '.') >>. rangeHasEnd >>. ws), rangePrecedence, Associativity.None, (), fun _ (x, xText) (y, yText) ->
    let span = SourceSpan.between xText yText
    Expression.at span (FunctionCall(RangeSyntax.builtIn, None, [ x; y ])), span))

// Note: Comma is NOT an operator in the OPP
// Commas are handled explicitly in tuple/record literals and function calls
// For bare tuple expressions (def x = 1, 2), we handle it in defStatement

// Inline operator precedence parser (no newlines in operator whitespace). It parses a block's statements and a match
// arm's body, so it has every operator `opp` has but `..` (see `binaryOperators`), each taking only the space on its
// line after it.
let oppInline = new OperatorPrecedenceParser<Operand, SourceSpan, ParseState>()
let private notKeywordInline =
    attempt (withSpan (pstring "not" .>> keywordEnd)) .>> wsInline |>> fun (_, span) -> NotSyntax.builtIn, span
// A pipe continues an operand on the lines after it when they are indented more than the line it starts on.
oppInline.TermParser <-
    currentLineIndent >>= fun lineIndent ->
        prefixed (notKeywordInline <|> minus <|> bang) |> withPipes (continuationWs lineIndent) .>> symbolicBoolean

addBinaryOperators oppInline wsInline binary

/// `a..`, a range without an end: the start, taking in every operator before it as `a..b` does, and then `..`.
let private withOpenRange (valueParser: Parser<Operand, ParseState>) : Parser<Operand, ParseState> =
    valueParser .>>. opt (attempt (withSpan (pstring ".." .>> notFollowedBy (pchar '.'))) .>> wsInline)
    |>> function
        | (first, firstText), Some (_, dotsSpan) ->
            let span = SourceSpan.between firstText dotsSpan
            Expression.at span (FunctionCall(RangeSyntax.builtIn, None, [ first ])), span
        | operand, None -> operand

// The operands take the pipes after them, so the only pipes left here are those after an open range: `1.. \take(3)`.
let pipeExpr =
    withPipes pipeLeadingWs (withOpenRange (reportingConflicts opp)) |>> fst
    |> withExcept pipeLeadingWs
    |> withAssignment expression

/// An expression that stops at the end of its line, except that a pipe or an `except` may continue it on the lines
/// after, indented more than the line it starts on (see `continuationWs`).
let inlinePipeExpr =
    currentLineIndent >>= fun lineIndent ->
        reportingConflicts oppInline |>> fst
        |> withExcept (continuationWs lineIndent)
        |> withAssignment exprWithoutCrossingNewlines


// Expression parser that stops at newlines (for use in blocks)
do exprWithoutCrossingNewlinesRef :=
    attempt matchExpr <|> inlinePipeExpr

// Wire up the expression parser with piping support
// Prefer match expressions first to avoid parsing "match" as an identifier.
do expressionRef := (attempt matchExpr <|> pipeExpr)

// Wire up the lambda parser
do 
    let parameterOf annotation =
        getPosition .>>. typedIdentifier annotation >>= fun (start, (name, typeOpt)) -> noteName start name >>% (name, typeOpt)
    let parameter = parameterOf parameterType
    // A parameter may be bracketed, `{ (x: int) -> x + 1 }`, and its annotation may then be a function type without
    // brackets of its own, `{ (g: int -> int) -> g(1) }`. Outside brackets, the arrow after an annotation ends the
    // parameters.
    let bracketedParameter = between (pstring "(" >>. wsInline) (wsInline >>. pstring ")") (parameterOf typeExpr)
    // `mut cell` takes a cell (see `CellPassingSyntax`), and `mut cell: int` one that holds an int. It is a single
    // name, so a pattern after `mut` is an error.
    let cellParameter =
        attempt (pstring "mut" >>. keywordEnd >>. ws)
        >>. (followedBy (satisfy (fun c -> Char.IsLetter c || c = '_' || c = '`'))
             <|> (getPosition >>= fun position ->
                     reportFocused position "`mut` on a parameter takes a single name, not a pattern: take the cell as `mut cell`, and its value apart in the body"))
        >>. parameterOf parameterType
        |>> fun (name, held) -> name, Some (CellPassingSyntax.parameterType held)
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
            ((cellParameter |>> Choice1Of2) <|> attempt (bracketedParameter |>> Choice1Of2) <|> attempt patternParameter <|> (parameter |>> Choice1Of2))
            (pstring "," .>> ws)
    let arrow = wsInline >>. pstring "->" .>> wsInline
    // The lambda's result annotation, `{ (x: int): int -> ... }`, which the `->` after it ends (see
    // `LambdaResultSyntax`).
    let resultAnnotation = opt (attempt (wsInline >>. pstring ":" >>. ws >>. parameterType))
    let withResult (resultType: TypeExpr option) (body: Expression) =
        match resultType with
        | Some resultType ->
            let made kind = Expression.at body.Span kind
            made (Block [ Statement.at body.Span (DefStatement(false, LambdaResultSyntax.name, Some resultType, body))
                          Statement.at body.Span (ExprStatement (made (IdentifierExpr LambdaResultSyntax.name))) ])
        | None -> body

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
    // Names and then arms, `{ m, f -> | #some(v), _ -> f(v) | other, _ -> other }`, match the arms against the
    // parameters in order, which stay in scope in each arm unless it binds the same name.
    let lambdaWithParamsAndArms =
        pipe2 (sepBy1 (attempt bracketedParameter <|> parameter) (pstring "," .>> ws) .>> arrow) (ws >>. withSpan matchArms)
            (fun named (arms, armsSpan) span ->
                let scrutinees = named |> List.map (fun (name, _) -> Expression.at armsSpan (IdentifierExpr name))
                Lambda(named, Expression.at span (Match(scrutinees, arms)), false))
    // A pattern parameter is taken apart by a match with one arm at the top of the body, over the parameter that
    // stands in its place (`patternArgument`), so the pattern must be total as a destructuring def's is. The arm's
    // body, the lambda's, declares the names the patterns bind. `{ () -> ... }` has no parameters, as `{ ... }` has,
    // but is marked as taking unit: only `{ ... }` ignores a parameter its expected type gives it.
    let lambdaWithParams =
        pipe3 paramList (resultAnnotation .>> arrow .>> skipMany (skipAnyOf " \t")) blockBody (fun parameters resultType body -> parameters, withResult resultType body)
        >>= fun (parameters, body) ->
            let destructured =
                parameters
                |> List.indexed
                |> List.choose (function
                    | index, Choice2Of2(pattern, names) -> Some(index + 1, pattern, names)
                    | _ -> None)
            match parameters, destructured with
            | [ Choice2Of2({ Kind = UnitPattern }, _) ], _ -> preturn (fun _ -> Lambda([], body, true))
            | _, [] -> preturn (fun _ -> Lambda(parameters |> List.choose (function Choice1Of2 named -> Some named | _ -> None), body, false))
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
                    Lambda(named, Expression.at span (Match(scrutinees, [ patterns, body ])), false)
    let lambdaNoParams = blockBody |>> (fun body _ -> Lambda([], body, false))
    // `{ }`, possibly spanning lines, is a lambda that does nothing
    let emptyLambda = skipMany (skipAnyOf " \t\r\n") >>. followedBy (pstring "}") >>% fun span -> Lambda([], Expression.at span UnitExpr, false)

    // The infix operators written with operator characters (see `binaryOperators`), longer ones before the shorter
    // ones they start with.
    let shorthandOperators =
        binaryOperators
        |> List.map _.Text
        |> List.filter (fun text -> not (Char.IsLetter text[0]))
        |> List.sortByDescending _.Length
    let shorthandOperator =
        notFollowedBy (pstring "->" <|> pstring "--")
        >>. choice (shorthandOperators |> List.map pstring)
        .>> notFollowedBy (anyOf "+-*/%^~<>=!?")

    // Shorthand lambda: an operator alone stands for itself, so `{ + }` means `{ a, b -> a + b }`.
    let shorthandBinaryOp =
        withSpan shorthandOperator .>> ws .>> followedBy (pstring "}") |>> fun (op, opSpan) span ->
            let made kind = Expression.at span kind
            Lambda(
                [ (implicitArgument, None); (implicitSecondArgument, None) ],
                made (BinaryOp(op, Some opSpan, made (IdentifierExpr implicitArgument), made (IdentifierExpr implicitSecondArgument))), false)

    // Shorthand lambda: a body that starts with an operator or a member access has left out its first operand, which is
    // the argument. The rest of the body follows as it would after that operand, so `{ % 2 == 0 }` means
    // `{ x -> x % 2 == 0 }` and `{ .age > 18 }` means `{ x -> x.age > 18 }`.
    let shorthandWithArgument =
        lookAhead (withSpan (attempt shorthandOperator <|> (pstring "." .>> followedBy identifierNoWs)))
        >>= fun (_, operatorSpan) -> updateUserState (fun state -> { state with ImplicitArgumentNext = Some operatorSpan })
        >>. expression .>> skipMany (skipAnyOf " \t\r\n")
        |>> fun body _ -> Lambda([ (implicitArgument, None) ], body, false)

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
            Lambda(argNames, made (Match(scrutinees, arms)), false))

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
                        attempt lambdaWithParamsAndArms
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
    // Note: f(a, b) creates single tuple argument: FunctionCall("f", None, [TupleExpr[a; b]]), and f(a, b) { ... }
    // passes the lambda after them: FunctionCall("f", None, [a; b; lambda]) (see `withTrailingLambda`)

    // `(args)`, and then a trailing lambda if there is one: the arguments of a call.
    let parenArgumentsAndTrailing =
        pipe2
            (noteFailureInside (
                between (pstring "(") (pstring ")")
                    (opt (ws >>. withSpan recordFields .>> ws))))
            (opt (attempt (skipMany (skipAnyOf " \t") >>. lambda)))
            (fun fieldsOpt trailingLambdaOpt ->
                match fieldsOpt, trailingLambdaOpt with
                | Some fields, Some lambdaExpr -> withTrailingLambda fields lambdaExpr
                | Some fields, None -> [ fieldsToExpr fields ]
                | None, Some lambdaExpr -> [ lambdaExpr ]
                | None, None -> [])

    // A call on a call's result, `f()(args)`, is a block that calls the result by a made-up name (see
    // `CallResultSyntax`). Each call spans from the start of the first.
    let callResult (callee: Expression) (args: Expression list, span: SourceSpan) =
        let span = SourceSpan.between callee.Span span
        let definition = Statement.at callee.Span (DefStatement(false, CallResultSyntax.name, None, callee))
        let call = Statement.at span (ExprStatement (Expression.at span (FunctionCall(CallResultSyntax.name, None, args))))
        Expression.at span (Block [ definition; call ])

    let callWithParensAndTrailing =
        pipe2
            (spanned (
                pipe2 qualifiedIdentifierWithSpan parenArgumentsAndTrailing (fun (name, nameSpan) args -> FunctionCall(name, nameSpan, args))))
            (many (withSpan parenArgumentsAndTrailing))
            (List.fold callResult)

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
