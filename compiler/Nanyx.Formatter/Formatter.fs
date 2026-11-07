/// Printing a parsed module back as Nanyx source, with its comments.
module Nanyx.Formatter.Formatter

open System
open System.Globalization
open System.Text.RegularExpressions
open Parser.Program

type Options =
    { /// The width lines are laid out to fit.
      LineWidth: int
      /// The spaces each level of indentation adds.
      IndentSize: int }

module Options =
    let defaults = { LineWidth = 100; IndentSize = 2 }

/// Where an expression is printed, which decides whether it needs parentheses (the parser keeps none).
type private Position =
    { /// On the spine of a statement, an arm's body or a top-level expression. The parser reads those without crossing
      /// lines, with an operator table that has no `%`, `..`, `and`, `or` or `not`, so those need parentheses there.
      Inline: bool
      /// Nothing follows it in the enclosing construct, so a construct that takes everything after it (a `match`, an
      /// `if`, a `with`) needs no parentheses.
      Tail: bool
      /// The loosest operator it may be without parentheses: 0 takes a pipe, 9 only an operand of `.` or `not`.
      Prec: int }

/// Inside brackets, where anything goes.
let private full = { Inline = false; Tail = true; Prec = 0 }

/// A statement, an arm's body or a top-level expression.
let private statementPosition = { Inline = true; Tail = true; Prec = 0 }

/// An operand of `.` or `not`: a primary expression.
let private operandPosition = { Inline = false; Tail = false; Prec = 9 }

let private precedence op =
    match op with
    | "*" | "/" | "%" -> 7
    | "+" | "-" -> 6
    | "++" -> 5
    | "<" | ">" | "<=" | ">=" -> 4
    | "==" | "!=" -> 3
    | "and" -> 2
    | "or" -> 1
    | _ -> 6

/// The operators the parser takes where it does not cross lines (see `Position.Inline`).
let private inlineOperators = set [ "*"; "/"; "+"; "-"; "++"; "<"; ">"; "<="; ">="; "=="; "!=" ]

/// The name a destructuring def binds its whole value to (see `Syntax`).
let private tupleTemp = Regex(@"^__tuple_\d+_\d+$")

/// What an `if` arm tests: a condition, or a value against a pattern (`if x is #some(y) then ...`).
type private IfHead =
    | Condition of Expression
    | Binding of Expression * Pattern

type private Def = bool * Identifier * TypeExpr option * Expression

/// Prints a module as Nanyx source. The layout is plain: one construct per line where the syntax has lines (blocks,
/// match arms, imports in brackets) and everything else on one line. The parser's sugar (`for`, `a..b`, `with`,
/// `rec`, `if ... is`, or-patterns, list spreads, shorthand lambdas, destructuring defs) is printed as written, using
/// the source to tell sugar from the code it stands for where their trees are the same. Comments are put back where
/// they were, or, where the layout has no line for them, on a line of their own before the line they were in.
let format (options: Options) (parsed: SourceModule) : string =
    let source = parsed.Source
    let text = Doc.text
    let cat = Doc.concat
    let hard = Doc.hardLine
    let nest doc = Doc.nest options.IndentSize doc
    let parens doc = cat [ text "("; doc; text ")" ]
    let commaJoin docs = Doc.join (text ", ") docs

    let lineStarts =
        [| yield 0
           for i in 0 .. source.Length - 1 do
               if source[i] = '\n' then yield i + 1 |]

    let lineOf offset =
        match Array.BinarySearch(lineStarts, offset) with
        | found when found >= 0 -> found
        | notFound -> ~~~notFound - 1

    let valid (span: SourceSpan) =
        not (SourceSpan.isNone span) && span.Start <= span.End && span.End <= source.Length

    let startsWith (span: SourceSpan) (s: string) =
        valid span && span.Start + s.Length <= source.Length && String.CompareOrdinal(source, span.Start, s, 0, s.Length) = 0

    /// The first character at or after `offset` that is not whitespace.
    let nextChar offset =
        let mutable i = offset
        while i < source.Length && Char.IsWhiteSpace source[i] do
            i <- i + 1
        if i >= 0 && i < source.Length then source[i] else '\000'

    /// The last character before `offset` that is not whitespace.
    let previousChar offset =
        let mutable i = offset - 1
        while i >= 0 && i < source.Length && Char.IsWhiteSpace source[i] do
            i <- i - 1
        if i >= 0 && i < source.Length then source[i] else '\000'

    // Comments are placed once each; whatever no construct places goes before the line it was in.
    let comments =
        parsed.Trivia
        |> List.choose (function
            | LineComment(commentText, _, span) -> Some(commentText.TrimEnd(), span)
            | BlankLines _ -> None)
        |> Array.ofList

    let placed = Array.zeroCreate<bool> comments.Length

    let blankRuns =
        parsed.Trivia
        |> List.choose (function
            | BlankLines(_, span) -> Some span
            | LineComment _ -> None)

    let unplacedIn (start: int) (finish: int) =
        [ for i in 0 .. comments.Length - 1 do
              let span = snd comments[i]
              if not placed[i] && start <= span.Start && span.Start < finish then yield i ]

    let hasCommentsIn start finish = not (List.isEmpty (unplacedIn start finish))

    /// The offset of the end of the line `offset` is on.
    let lineEnd offset =
        let line = lineOf offset
        if line + 1 < lineStarts.Length then lineStarts[line + 1] - 1 else source.Length

    /// Children laid out one per line between the offsets `start` and `finish`, each given as its start and end offsets
    /// and a function building its document. The comments among them are put back: a comment after a child on its
    /// last line stays at the end of that line, and any other on a line of its own. A comment inside a child that the
    /// child does not place itself goes on a line of its own before it, or after it when the child placed comments
    /// that come before it, so that comments stay in order.
    ///
    /// With `blankLines`, for where the parser allows blank lines between children (the top level, a module block and
    /// a block of statements, but not a list of imports): a child that spans more than one line gets a
    /// blank line before it (and its leading comments) and after it, except at the start and end and between two
    /// imports; between children that each fit on one line, a blank line (however many there were) is kept, and none
    /// is added.
    let rec sequence (blankLines: bool) (start: int) (finish: int) (children: (int * int * (unit -> Doc)) list) : Doc =
        sequenceOf blankLines start finish (children |> List.map (fun (childStart, childEnd, build) -> childStart, childEnd, false, build))

    /// `sequence`, with each child saying whether it is an import.
    and sequenceOf (blankLines: bool) (start: int) (finish: int) (children: (int * int * bool * (unit -> Doc)) list) : Doc =
        let parts = ResizeArray<Doc>()
        let mutable empty = true
        let mutable blank = false

        let emit (doc: Doc) =
            if not empty then
                parts.Add hard
                if blank then parts.Add hard
            parts.Add doc
            empty <- false
            blank <- false

        let placeComment i =
            placed[i] <- true
            emit (text (fst comments[i]))

        // The comments, and the author's blank lines, between the offsets `a` and `b`, in order.
        let between a b =
            let blanks =
                if blankLines then
                    blankRuns |> List.filter (fun span -> a <= span.Start && span.Start < b) |> List.map (fun span -> span.Start, None)
                else
                    []
            let inBetween = unplacedIn a b |> List.map (fun i -> (snd comments[i]).Start, Some i)
            List.sortBy fst (blanks @ inBetween) |> List.map snd

        let emitBetween entries (separate: bool) =
            if blankLines && separate && not empty then blank <- true
            for entry in entries do
                match entry with
                | Some i -> placeComment i
                | None -> if not empty then blank <- true

        let mutable previous = start
        let mutable previousMultiLine = false
        let mutable previousImport = false
        for childStart, childEnd, isImport, build in children do
            let childStart = max childStart previous
            let childEnd = max childEnd childStart
            let leading = between previous childStart
            let placedBefore = Array.copy placed
            let doc = build ()
            let firstPlacedByChild =
                seq { for i in 0 .. comments.Length - 1 do if placed[i] && not placedBefore[i] then yield (snd comments[i]).Start }
                |> Seq.fold min Int32.MaxValue
            let orphans = unplacedIn childStart childEnd
            let before, after = orphans |> List.partition (fun i -> (snd comments[i]).Start < firstPlacedByChild)
            let multiLine = Doc.hasHardLine doc || not after.IsEmpty
            emitBetween leading ((multiLine || previousMultiLine) && not (isImport && previousImport))
            for i in before do
                placeComment i
            emit doc
            for i in after do
                placeComment i
            if childEnd > childStart then
                match unplacedIn childEnd (lineEnd (childEnd - 1) + 1) with
                | i :: _ when after.IsEmpty ->
                    placed[i] <- true
                    parts.Add(Doc.lineSuffix (fst comments[i]))
                | i :: _ -> placeComment i
                | [] -> ()
            previous <- childEnd
            previousMultiLine <- multiLine
            previousImport <- isImport
        emitBetween (between previous finish) previousMultiLine
        cat (List.ofSeq parts)

    /// The runs of top-level items or statements that make up one def each: a destructuring `def (a, b) = value` is
    /// parsed as a def of a temporary and one def per name, all with the same span. Each run is its first index, its
    /// length and whether it is a destructuring def.
    let destructuringRuns (items: (SourceSpan * Def option) list) : (int * int * bool) list =
        let items = Array.ofList items
        let rec rootedAt temp (e: Expression) =
            match e.Kind with
            | MemberAccess(target, _, _) -> rootedAt temp target
            | IdentifierExpr n -> n = temp
            | _ -> false
        let runs = ResizeArray()
        let mutable i = 0
        while i < items.Length do
            match items[i] with
            | span, Some(_, temp, None, _) when tupleTemp.IsMatch temp ->
                let mutable j = i + 1
                let belongs k =
                    match items[k] with
                    | otherSpan, Some(_, _, None, value) -> otherSpan = span && rootedAt temp value
                    | _ -> false
                while j < items.Length && belongs j do
                    j <- j + 1
                runs.Add((i, j - i, true))
                i <- j
            | _ ->
                runs.Add((i, 1, false))
                i <- i + 1
        List.ofSeq runs

    /// The pattern of a destructuring def as written, with its spacing made regular.
    let writtenPattern (span: SourceSpan) =
        if not (valid span) then
            None
        else
            let defAt = source.IndexOf("def", span.Start, StringComparison.Ordinal)
            let openAt = if defAt >= 0 && defAt < span.End then source.IndexOf('(', defAt) else -1
            if openAt < 0 || openAt >= span.End then
                None
            else
                let mutable depth = 0
                let mutable closeAt = -1
                let mutable i = openAt
                while closeAt < 0 && i < span.End do
                    if source[i] = '(' then depth <- depth + 1
                    elif source[i] = ')' then
                        depth <- depth - 1
                        if depth = 0 then closeAt <- i
                    i <- i + 1
                if closeAt < 0 then
                    None
                else
                    Some(Regex.Replace(source.Substring(openAt, closeAt - openAt + 1), @"\s+", "").Replace(",", ", "))

    /// The pattern of a destructuring def, rebuilt from the defs it stands for when the source cannot be read.
    let rebuiltPattern (bindings: Def list) =
        let rec path (e: Expression) acc =
            match e.Kind with
            | MemberAccess(target, field, _) -> path target (int field :: acc)
            | _ -> acc
        let rec render (entries: (int list * Identifier) list) =
            let maxIndex = entries |> List.map (fst >> List.head) |> List.fold max 0
            let element k =
                match entries |> List.tryFind (fun (p, _) -> p = [ k ]) with
                | Some(_, n) -> n
                | None ->
                    let nested = entries |> List.filter (fun (p, _) -> List.head p = k && p.Length > 1) |> List.map (fun (p, n) -> List.tail p, n)
                    if nested.IsEmpty then "_" else render nested
            "(" + String.Join(", ", [ for k in 0..maxIndex -> element k ]) + ")"
        match bindings |> List.map (fun (_, n, _, value) -> path value [], n) |> List.filter (fun (p, _) -> not p.IsEmpty) with
        | [] -> "()"
        | entries -> render entries

    let rec expr (position: Position) (e: Expression) : Doc =
        if needsParentheses position e then parens (exprBare full e) else exprBare position e

    and needsParentheses (position: Position) (e: Expression) =
        match e.Kind with
        | BinaryOp(op, _, _) -> precedence op < position.Prec || (position.Inline && not (inlineOperators.Contains op))
        | FunctionCall("__range", _, [ _; _ ]) -> 4 < position.Prec || position.Inline
        | FunctionCall("not", _, [ _ ]) -> 8 < position.Prec || position.Inline
        | Pipe _ -> 0 < position.Prec
        | _ when isOpenEnded e -> not position.Tail || position.Prec > 8
        | _ -> false

    /// Whether the construct takes everything after it, as the last arm of a `match` takes the rest of the line.
    and isOpenEnded (e: Expression) =
        match e.Kind with
        | Match _
        | IfExpr _
        | UseIn _
        | WorkflowBindExpr _
        | WorkflowReturnExpr _ -> true
        | FunctionCall(("__for" | "__with"), _, [ _; _ ]) -> true
        | _ -> false

    and exprBare (position: Position) (e: Expression) : Doc =
        match e.Kind with
        | LiteralExpr literal -> literalDoc e.Span literal
        | UnitExpr -> text "()"
        | IdentifierExpr n -> text n
        | BinaryOp(op, l, r) ->
            match spreadSegments e with
            | Some segments -> listDoc e.Span segments
            | None -> binary position op l r
        | FunctionCall("not", _, [ operand ]) -> cat [ text "not "; expr operandPosition operand ]
        | FunctionCall("__range", _, [ a; b ]) ->
            cat [ expr { position with Tail = false; Prec = 4 } a; text ".."; expr { position with Prec = 5 } b ]
        | FunctionCall("__with", _, [ record; updates ]) when (match updates.Kind with RecordExpr fields -> fields |> List.forall (function NamedField _ -> true | _ -> false) | _ -> false) ->
            let fields = match updates.Kind with RecordExpr fields -> fields | _ -> []
            let count = fields.Length
            let update index field =
                match field with
                | NamedField(n, value) -> cat [ text n; text " = "; expr { full with Tail = (index = count - 1) } value ]
                | PositionalField value -> expr full value
            cat [ expr operandPosition record; text " with "; commaJoin (List.mapi update fields) ]
        | FunctionCall("__for", _, [ iterable; loop ]) when (match loop.Kind with Lambda([ ("__for_element", None) ], _) -> true | _ -> false) ->
            forDoc iterable loop
        | FunctionCall(n, nameSpan, args) -> cat [ text n; callArguments e (nameEnd nameSpan (e.Span.Start + n.Length)) args ]
        | Lambda(parameters, body) -> lambdaDoc e parameters body
        | Pipe(value, n, nameSpan, args) ->
            cat [ expr { position with Tail = false; Prec = 0 } value; text " \\"; text n; pipeArguments e (nameEnd nameSpan value.Span.End) args ]
        | Block _ -> block e
        | Match(scrutinees, arms) ->
            match ifArm e with
            | Some _ -> ifDoc e
            | None -> matchDoc e scrutinees arms
        | IfExpr _ -> ifDoc e
        | TupleExpr items -> fieldsDoc e.Span.Start e.Span.End (List.map PositionalField items)
        | RecordExpr fields -> fieldsDoc e.Span.Start e.Span.End fields
        | ListExpr items -> listDoc e.Span [ Choice1Of2 items ]
        | TagExpr(n, None) -> text ("#" + n)
        | TagExpr(n, Some payload) -> cat [ text ("#" + n); parenFields payload (e.Span.Start + 1 + n.Length) e.Span.End ]
        | MemberAccess(target, field, _) -> cat [ expr operandPosition target; text "."; text field ]
        | ContextMemberAccess(context, field) -> cat [ contextHead context; text "."; text field ]
        | ContextMemberCall(context, field, args) ->
            let count = args.Length
            let argument index arg = expr { full with Tail = (index = count - 1) } arg
            cat [ contextHead context; text "."; text field; text "("; commaJoin (List.mapi argument args); text ")" ]
        | UseIn(binding, body) -> cat [ text "use "; useBinding { full with Tail = false } binding; text " in "; expr full body ]
        | WorkflowBindExpr(n, value) ->
            let keyword = if n = "try" && not (startsWith e.Span "try!") then "try " else n + "! "
            cat [ text keyword; expr full value ]
        | WorkflowReturnExpr value -> cat [ text "return "; expr full value ]
        | InterpolatedString parts ->
            let part p =
                match p with
                | StringText s -> Doc.textOrVerbatim s
                | StringExpr inner -> cat [ text "{"; expr full inner; text "}" ]
            cat [ text "\""; cat (List.map part parts); text "\"" ]

    and literalDoc (span: SourceSpan) (literal: Literal) : Doc =
        if valid span && span.End > span.Start then
            Doc.textOrVerbatim (source.Substring(span.Start, span.End - span.Start))
        else
            match literal with
            | StringLit s -> Doc.textOrVerbatim ("\"" + s + "\"")
            | IntLit i -> text (string i)
            | FloatLit f -> text (f.ToString("R", CultureInfo.InvariantCulture))
            | BoolLit b -> text (if b then "true" else "false")

    and binary (position: Position) (op: string) (l: Expression) (r: Expression) : Doc =
        let p = precedence op
        let leftPrec, rightPrec = if op = "++" then p + 1, p else p, p + 1
        // `&&` and `||` build the same tree as `and` and `or`; the source says which was written.
        let between =
            if valid l.Span && valid r.Span && l.Span.End <= r.Span.Start then
                source.Substring(l.Span.End, r.Span.Start - l.Span.End).Trim(' ', '\t', '\n', '(', ')')
            else
                ""
        let written =
            match op with
            | "and" when between = "&&" -> "&&"
            | "or" when between = "||" -> "||"
            | _ -> op
        cat [ expr { position with Tail = false; Prec = leftPrec } l; text $" {written} "; expr { position with Prec = rightPrec } r ]

    /// `[a, ...xs, b]` is parsed as `[a] ++ xs ++ [b]`, the list runs spanning the whole literal. The segments, each a
    /// run of elements or a list spread, when `e` is such a literal.
    and spreadSegments (e: Expression) : Choice<Expression list, Expression> list option =
        match e.Kind with
        | BinaryOp("++", l, r) when valid e.Span && source[e.Span.Start] = '[' && (l.Span = e.Span || (valid l.Span && l.Span.Start > e.Span.Start)) ->
            let segment (x: Expression) =
                match x.Kind with
                | ListExpr items when x.Span = e.Span -> Choice1Of2 items
                | _ -> Choice2Of2 x
            let rec flatten (x: Expression) =
                match x.Kind with
                | BinaryOp("++", a, b) when x.Span = e.Span -> segment a :: flatten b
                | _ -> [ segment x ]
            Some(segment l :: flatten r)
        | _ -> None

    and listDoc (span: SourceSpan) (segments: Choice<Expression list, Expression> list) : Doc =
        let items =
            segments
            |> List.collect (function
                | Choice1Of2 elements -> elements |> List.map (fun element -> false, element)
                | Choice2Of2 spread -> [ true, spread ])
        let count = items.Length
        let itemDoc index (spread, item: Expression) =
            cat [ (if spread then text "..." else Doc.empty); expr { full with Tail = (index = count - 1) } item ]
        if valid span && hasCommentsIn span.Start span.End then
            // One item per line; the `]` cannot start a line, so it follows the last item.
            let children =
                items
                |> List.mapi (fun index ((_, item: Expression) as entry) ->
                    item.Span.Start, item.Span.End, fun () -> cat [ itemDoc index entry; (if index < count - 1 then text "," else Doc.empty) ])
            cat [ text "["; nest (cat [ hard; sequence false span.Start span.End children ]); text "]" ]
        else
            cat [ text "["; commaJoin (List.mapi itemDoc items); text "]" ]

    /// Fields in brackets: a record, a tuple, or a call's arguments. With a comment among them, one field per line,
    /// without commas.
    and fieldsDoc (start: int) (finish: int) (fields: RecordField list) : Doc =
        let count = fields.Length
        let fieldDoc index field =
            let position = { full with Tail = (index = count - 1) }
            match field with
            | NamedField(n, value) -> cat [ text n; text " = "; expr position value ]
            | PositionalField value -> expr position value
        let valueOf field =
            match field with
            | NamedField(_, value)
            | PositionalField value -> value
        if hasCommentsIn start finish then
            let children =
                fields
                |> List.mapi (fun index field ->
                    let value = valueOf field
                    value.Span.Start, value.Span.End, fun () -> fieldDoc index field)
            cat [ text "("; nest (cat [ hard; sequence false start finish children ]); hard; text ")" ]
        else
            cat [ text "("; commaJoin (List.mapi fieldDoc fields); text ")" ]

    /// A call's or a tag's single bracketed argument, between the offsets `start` (after the name) and `finish`:
    /// several values are a tuple, named ones a record.
    and parenFields (argument: Expression) (start: int) (finish: int) : Doc =
        match argument.Kind with
        | TupleExpr items -> fieldsDoc start finish (List.map PositionalField items)
        | RecordExpr fields -> fieldsDoc start finish fields
        | _ -> fieldsDoc start finish [ PositionalField argument ]

    /// Where the name of a call or a pipe's target ends.
    and nameEnd (nameSpan: SourceSpan option) (fallback: int) =
        match nameSpan with
        | Some span when valid span -> span.End
        | _ -> fallback

    /// Whether a lambda argument follows the brackets of a call whose name ends at `afterName` (`f { x -> x }`), rather
    /// than being in them.
    and isTrailingLambda (afterName: int) (argument: Expression) =
        match argument.Kind with
        | Lambda _ -> not (valid argument.Span && afterName <= argument.Span.Start && nextChar afterName = '(')
        | _ -> false

    and callArguments (call: Expression) (afterName: int) (args: Expression list) : Doc =
        match args with
        | [] -> text "()"
        | _ -> pipeArguments call afterName args

    and pipeArguments (call: Expression) (afterName: int) (args: Expression list) : Doc =
        match args with
        | [] -> Doc.empty
        | [ argument ] when isTrailingLambda afterName argument -> cat [ text " "; expr full argument ]
        | [ argument ] -> parenFields argument afterName call.Span.End
        | [ argument; lambda ] -> cat [ parenFields argument afterName lambda.Span.Start; text " "; expr full lambda ]
        | _ -> cat [ text "("; commaJoin (List.map (expr full) args); text ")" ]

    and lambdaDoc (e: Expression) (parameters: (Identifier * TypeExpr option) list) (body: Expression) : Doc =
        match shorthand e parameters body with
        | Some doc -> doc
        | None ->
            let parameterDoc (n, typeOpt) =
                match typeOpt with
                | Some t -> cat [ text n; text ": "; typeExpr t ]
                | None -> text n
            let head =
                match parameters with
                | [] -> text "{"
                | _ -> cat [ text "{ "; commaJoin (List.map parameterDoc parameters); text " ->" ]
            match body.Kind with
            // The statements of a lambda are read regardless of lines and indentation, so the comments anywhere between
            // its braces go among them.
            | Block statements -> cat [ head; nest (cat [ hard; statementsInSequence true e.Span.Start e.Span.End statements ]); hard; text "}" ]
            | _ ->
                let bodyDoc = expr full body
                // A body that starts like a shorthand lambda (`{ - 1 }`, `{ .name }`) needs the arrow.
                let head =
                    let first = (Doc.render Int32.MaxValue bodyDoc).TrimStart()
                    if List.isEmpty parameters && first.Length > 0 && "*/+-<>=!.|".Contains first[0] then text "{ ->" else head
                if Doc.hasHardLine bodyDoc then cat [ head; text " "; bodyDoc; hard; text "}" ]
                else cat [ head; text " "; bodyDoc; text " }" ]

    /// The shorthand lambdas the parser expands, made up at the span of the whole lambda: `{ }`, `{ * }`, `{ * 2 }`,
    /// `{ .name }` and `{ | pattern -> ... }`.
    and shorthand (e: Expression) parameters (body: Expression) : Doc option =
        let made (x: Expression) = valid e.Span && x.Span = e.Span
        match parameters, body.Kind with
        | [], UnitExpr when made body -> Some(text "{ }")
        | [ ("x", None); ("y", None) ], BinaryOp(op, l, r) when made body && made l && made r && l.Kind = IdentifierExpr "x" && r.Kind = IdentifierExpr "y" ->
            Some(text $"{{ {op} }}")
        | [ ("x", None) ], BinaryOp(op, l, right) when made body && made l && l.Kind = IdentifierExpr "x" ->
            Some(cat [ text $"{{ {op} "; expr operandPosition right; text " }" ])
        | [ ("x", None) ], MemberAccess(target, field, None) when made body && made target && target.Kind = IdentifierExpr "x" ->
            Some(text $"{{ .{field} }}")
        | _, Match(scrutinees, arms) when
            made body
            && not parameters.IsEmpty
            && parameters = List.init parameters.Length (fun i -> $"arg{i + 1}", None)
            && scrutinees.Length = parameters.Length
            && List.forall2 (fun (s: Expression) (n, _) -> made s && s.Kind = IdentifierExpr n) scrutinees parameters
            ->
            let armsDoc = armsInSequence e.Span.Start e.Span.End arms
            let single = (List.distinctBy (fun (_, b: Expression) -> b.Id) arms).Length = 1
            if single && not (Doc.hasHardLine armsDoc) then Some(cat [ text "{ "; armsDoc; text " }" ])
            else Some(cat [ text "{"; nest (cat [ hard; armsDoc ]); hard; text "}" ])
        | _ -> None

    /// Match arms one per line. Arms that share one body are the alternatives of an or-pattern, `| a | b -> body`.
    and armsInSequence (start: int) (finish: int) (arms: MatchArm list) : Doc =
        let groups =
            arms
            |> List.fold
                (fun groups (patterns, body: Expression) ->
                    match groups with
                    | (alternatives, shared: Expression) :: rest when obj.ReferenceEquals(shared, body) -> (alternatives @ [ patterns ], shared) :: rest
                    | _ -> ([ patterns ], body) :: groups)
                []
            |> List.rev
        let count = groups.Length
        groups
        |> List.mapi (fun index (alternatives, body) ->
            let armStart =
                match alternatives with
                | (first :: _) :: _ when valid first.Span -> first.Span.Start
                | _ -> body.Span.Start
            armStart, body.Span.End, fun () ->
                let patterns = alternatives |> List.map (fun ps -> commaJoin (List.map pattern ps)) |> Doc.join (text " | ")
                cat [ text "| "; patterns; armBody (index = count - 1) body ])
        |> sequence false start finish

    /// `-> body`: an indented block on the lines after it, or an expression on the same line.
    and armBody (isLast: bool) (body: Expression) : Doc =
        match body.Kind with
        | Block _ -> cat [ text " ->"; nest (cat [ hard; block body ]) ]
        | _ -> cat [ text " -> "; expr { statementPosition with Tail = isLast } body ]

    and matchDoc (e: Expression) (scrutinees: Expression list) (arms: MatchArm list) : Doc =
        let armsDoc = nest (cat [ hard; armsInSequence e.Span.Start e.Span.End arms ])
        match scrutinees with
        // A block scrutinee is indented more than the arms, which are indented more than the `match`.
        | [ scrutinee ] when (match scrutinee.Kind with Block _ -> true | _ -> false) ->
            cat [ text "match"; Doc.nest (2 * options.IndentSize) (cat [ hard; block scrutinee ]); armsDoc ]
        | _ -> cat [ text "match "; commaJoin (List.map (expr { full with Tail = false }) scrutinees); armsDoc ]

    /// An `if` as its head, its `then` branch and its `else` branch if it has one (a left-out `else` is `()` made up at
    /// the `then` branch). `if x is pattern then a else b` is parsed as a match of `x` against the pattern and `_`.
    and ifArm (e: Expression) : (IfHead * Expression * Expression option) option =
        let elseOf (thenBranch: Expression) (elseBranch: Expression) =
            if elseBranch.Kind = UnitExpr && elseBranch.Span = thenBranch.Span then None else Some elseBranch
        match e.Kind with
        | IfExpr(condition, thenBranch, elseBranch) -> Some(Condition condition, thenBranch, elseOf thenBranch elseBranch)
        | Match([ value ], [ [ p ], thenBranch; [ wildcard ], elseBranch ]) when
            wildcard.Kind = WildcardPattern
            && wildcard.Span = elseBranch.Span
            && (startsWith e.Span "if" || startsWith e.Span "|")
            && not (obj.ReferenceEquals(thenBranch, elseBranch))
            ->
            Some(Binding(value, p), thenBranch, elseOf thenBranch elseBranch)
        | _ -> None

    and ifHead (head: IfHead) : Doc =
        match head with
        | Condition condition -> expr { full with Tail = false } condition
        | Binding(value, p) -> cat [ expr { full with Tail = false } value; text " is "; pattern p ]

    and ifDoc (e: Expression) : Doc =
        let head, thenBranch, elseBranch = (ifArm e).Value
        // `if | a -> x | b -> y | else -> z` is parsed as nested ifs, each after the first spanning from its `|`.
        if startsWith e.Span "if" && nextChar (e.Span.Start + 2) = '|' then
            let rec chain arms (head, body, rest: Expression option) =
                match rest with
                | Some r when startsWith r.Span "|" && (ifArm r).IsSome -> chain ((head, body) :: arms) (ifArm r).Value
                | _ -> List.rev ((head, body) :: arms), rest
            let arms, elseBranch = chain [] (head, thenBranch, elseBranch)
            let headStart head =
                match head with
                | Condition condition -> condition.Span.Start
                | Binding(value, _) -> value.Span.Start
            let conditionArms =
                arms
                |> List.mapi (fun index (head, body: Expression) ->
                    let isLast = index = arms.Length - 1 && elseBranch.IsNone
                    headStart head, body.Span.End, fun () -> cat [ text "| "; ifHead head; armBody isLast body ])
            let elseArm =
                match elseBranch with
                | Some body -> [ body.Span.Start, body.Span.End, fun () -> cat [ text "| else"; armBody true body ] ]
                | None -> []
            cat [ text "if"; nest (cat [ hard; sequence false e.Span.Start e.Span.End (conditionArms @ elseArm) ]) ]
        else
            let isBlock (x: Expression) = match x.Kind with Block _ -> true | _ -> false
            let thenDoc =
                if isBlock thenBranch then cat [ text " then"; nest (cat [ hard; block thenBranch ]) ]
                else cat [ text " then "; expr { full with Tail = elseBranch.IsNone } thenBranch ]
            let elseDoc =
                match elseBranch with
                | None -> Doc.empty
                | Some elseBranch ->
                    let keyword = if isBlock thenBranch then cat [ hard; text "else" ] else text " else"
                    if isBlock elseBranch then cat [ keyword; nest (cat [ hard; block elseBranch ]) ]
                    else cat [ keyword; text " "; expr full elseBranch ]
            cat [ text "if "; ifHead head; thenDoc; elseDoc ]

    /// `for xs | arms` and `for xs -> body`, parsed as a call to `__for` with a lambda of the element.
    and forDoc (iterable: Expression) (loop: Expression) : Doc =
        let head = cat [ text "for "; expr { full with Tail = false } iterable ]
        match loop.Kind with
        | Lambda(_, body) ->
            match body.Kind with
            | Match([ element ], arms) when element.Kind = IdentifierExpr "__for_element" && body.Span = loop.Span ->
                cat [ head; nest (cat [ hard; armsInSequence loop.Span.Start loop.Span.End arms ]) ]
            | _ -> cat [ head; armBody true body ]
        | _ -> head

    and useBinding (position: Position) (binding: UseBinding) : Doc =
        match binding with
        | UseValue value -> expr position value
        | UseContextInstance(context, value) -> cat [ contextHead context; text " = "; expr position value ]

    /// A context and its type arguments, as in `C(T).field` and `use C(T) = ...`.
    and contextHead (context: TypeExpr) : Doc =
        match context.Kind with
        | TypeApply(n, args) -> cat [ text n; text "("; commaJoin (List.map typeExpr args); text ")" ]
        | _ -> typeExpr context

    /// An indented block (a def's value, an arm's body, an `if` branch).
    and block (b: Expression) : Doc =
        match b.Kind with
        | Block statements -> statementsInSequence true b.Span.Start b.Span.End statements
        | _ -> expr full b

    and statementsInSequence (blankLines: bool) (start: int) (finish: int) (statements: Statement list) : Doc =
        let defOf (s: Statement) =
            match s.Kind with
            | DefStatement(isExport, n, t, value) -> Some(isExport, n, t, value)
            | _ -> None
        let all = Array.ofList statements
        destructuringRuns (statements |> List.map (fun s -> s.Span, defOf s))
        |> List.map (fun (index, count, isDestructuring) ->
            let first = all[index]
            if isDestructuring then
                first.Span.Start, first.Span.End, fun () -> destructuringDef [ for k in index .. index + count - 1 -> (defOf all[k]).Value ] first.Span
            else
                first.Span.Start, first.Span.End, fun () -> statement first)
        |> sequence blankLines start finish

    and statement (s: Statement) : Doc =
        match s.Kind with
        | DefStatement(false, n, t, value) when (match value.Kind with FunctionCall("__rec", _, [ _ ]) -> true | _ -> false) ->
            match value.Kind with
            | FunctionCall(_, _, [ inner ]) -> defDoc false "rec" n t inner
            | _ -> defDoc false "def" n t value
        | DefStatement(isExport, n, t, value) -> defDoc isExport "def" n t value
        | TypeDefStatement(n, modifiers, parameters, body) -> typeDefDoc n modifiers parameters body
        | ImportStatement items -> importDoc s.Span.Start s.Span.End items
        | UseStatement binding -> cat [ text "use "; useBinding full binding ]
        | ExprStatement value -> expr statementPosition value

    and defDoc (isExport: bool) (keyword: string) (name: string) (typeOpt: TypeExpr option) (value: Expression) : Doc =
        let head =
            cat [
                (if isExport then text "export " else Doc.empty)
                text $"{keyword} {name}"
                (match typeOpt with
                 | Some t -> cat [ text ": "; signature t ]
                 | None -> Doc.empty)
                text " ="
            ]
        match value.Kind with
        | Block _ -> cat [ head; nest (cat [ hard; block value ]) ]
        | _ -> cat [ head; text " "; expr full value ]

    and destructuringDef (group: Def list) (span: SourceSpan) : Doc =
        let _, _, _, value = List.head group
        let bindings = List.tail group
        let isExport = bindings |> List.exists (fun (isExport, _, _, _) -> isExport)
        let patternText = writtenPattern span |> Option.defaultWith (fun () -> rebuiltPattern bindings)
        defDoc isExport "def" patternText None value

    and pattern (p: Pattern) : Doc =
        match p.Kind with
        | LiteralPattern literal -> literalDoc p.Span literal
        | IdentifierPattern n -> text n
        | WildcardPattern -> text "_"
        | ElsePattern -> text "else"
        | TuplePattern ps -> cat [ text "("; commaJoin (List.map pattern ps); text ")" ]
        | RecordPattern(n, ps) -> cat [ text n; text "("; commaJoin (List.map pattern ps); text ")" ]
        | RecordMemberPattern fields ->
            cat [ text "("; commaJoin (fields |> List.map (fun (n, p) -> cat [ text n; text " = "; pattern p ])); text ")" ]
        | ListPattern(ps, rest) ->
            let restDoc =
                match rest with
                | Some (Kind WildcardPattern) -> [ text "..." ]
                | Some r -> [ cat [ text "..."; pattern r ] ]
                | None -> []
            cat [ text "["; commaJoin (List.map pattern ps @ restDoc); text "]" ]
        | ListSplatMiddle(before, after) ->
            cat [ text "["; commaJoin (List.map pattern before @ [ text "..." ] @ List.map pattern after); text "]" ]
        | RangePattern(a, b) -> cat [ expr full a; text ".."; expr full b ]
        | GuardPattern(op, value) ->
            if valid p.Span && p.Span.Start < source.Length && source[p.Span.Start] <> '{' then cat [ text op; text " "; expr full value ]
            else cat [ text "{ "; text op; text " "; expr full value; text " }" ]
        | TagPattern(n, None) -> text ("#" + n)
        | TagPattern(n, Some payload) ->
            let afterName = p.Span.Start + 1 + n.Length
            if valid p.Span && afterName < source.Length && source[afterName] <> '(' then
                cat [ text $"#{n} "; pattern payload ]
            else
                match payload.Kind with
                | TuplePattern ps -> cat [ text $"#{n}("; commaJoin (List.map pattern ps); text ")" ]
                | _ -> cat [ text $"#{n}("; pattern payload; text ")" ]

    /// A def's annotation, or anywhere the parser reads a list of types: `,` binds tighter than `->`, so
    /// `int, int -> int` is a function of two parameters.
    and signature (t: TypeExpr) : Doc =
        match t.Kind with
        | TypeWithContext(contexts, inner) -> cat [ contextSet contexts; text " "; typeList inner ]
        | _ -> typeList t

    and typeList (t: TypeExpr) : Doc =
        match t.Kind with
        | TypeTuple items -> commaJoin (List.map listItem items)
        | TypeFunc(parameter, result) when (match parameter.Kind with TypeTuple _ -> true | _ -> false) ->
            let items = match parameter.Kind with TypeTuple items -> items | _ -> []
            cat [ commaJoin (List.map listItem items); text " -> "; typeExpr result ]
        | _ -> typeExpr t

    /// One of a list of types. A union is bracketed for readability, though it need not be.
    and listItem (t: TypeExpr) : Doc =
        match t.Kind with
        | TypeFunc _
        | TypeTuple _
        | TypeWithContext _
        | TypeUnion _ -> parenType t
        | _ -> typeExpr t

    and typeExpr (t: TypeExpr) : Doc =
        match t.Kind with
        | TypeWithContext(contexts, inner) -> cat [ contextSet contexts; text " "; arrowType inner ]
        | _ -> arrowType t

    and arrowType (t: TypeExpr) : Doc =
        match t.Kind with
        | TypeFunc(parameter, result) ->
            let parameterDoc =
                match parameter.Kind with
                | TypeTuple _
                | TypeFunc _
                | TypeWithContext _
                | TypeUnion _ -> parenType parameter
                | _ -> unionType parameter
            cat [ parameterDoc; text " -> "; typeExpr result ]
        | _ -> unionType t

    and unionType (t: TypeExpr) : Doc =
        match t.Kind with
        | TypeUnion items ->
            items
            |> List.map (fun item ->
                match item.Kind with
                | TypeUnion _
                | TypeFunc _
                | TypeWithContext _ -> parenType item
                | _ -> intersectionType item)
            |> Doc.join (text " | ")
        | _ -> intersectionType t

    and intersectionType (t: TypeExpr) : Doc =
        match t.Kind with
        | TypeIntersection items ->
            items
            |> List.map (fun item ->
                match item.Kind with
                | TypeIntersection _
                | TypeUnion _
                | TypeFunc _
                | TypeWithContext _ -> parenType item
                | _ -> postfixType item)
            |> Doc.join (text " & ")
        | _ -> postfixType t

    and postfixType (t: TypeExpr) : Doc =
        match t.Kind with
        | TypeOptional inner ->
            let innerDoc =
                match inner.Kind with
                | TypeOptional _
                | TypeUnion _
                | TypeIntersection _
                | TypeFunc _
                | TypeWithContext _ -> parenType inner
                | _ -> atomType inner
            cat [ innerDoc; text "?" ]
        | _ -> atomType t

    and parenType (t: TypeExpr) : Doc =
        match t.Kind with
        | TypeTuple items -> cat [ text "("; commaJoin (List.map listItem items); text ")" ]
        | _ -> cat [ text "("; signature t; text ")" ]

    and atomType (t: TypeExpr) : Doc =
        match t.Kind with
        | TypeName n
        | TypeVar n -> text n
        | TypeUnit -> text "()"
        | TypeTuple _ -> parenType t
        | TypeRecord fields -> cat [ text "("; commaJoin (List.map recordTypeField fields); text ")" ]
        | TypeApply(n, args) -> cat [ text n; text "("; typeArguments args; text ")" ]
        | TypeTag(n, None) -> text ("#" + n)
        | TypeTag(n, Some payload) -> cat [ text $"#{n}("; tagPayload payload; text ")" ]
        | TypeContext items -> contextSet items
        | TypeConstraint(n, baseType, predicate) ->
            cat [ text n; text " :: "; typeExpr baseType; text " where "; expr full predicate ]
        | TypeWhere(baseType, predicate) -> cat [ typeExpr baseType; text " where "; expr full predicate ]
        | TypeOf value -> cat [ text "typeof "; expr full value ]
        | TypeFunc _
        | TypeUnion _
        | TypeIntersection _
        | TypeOptional _
        | TypeWithContext _ -> parenType t

    /// Type arguments are a list of types, so one function argument needs no brackets: `map(a -> b)`.
    and typeArguments (args: TypeExpr list) : Doc =
        match args with
        | [ single ] ->
            match single.Kind with
            | TypeTuple _
            | TypeWithContext _ -> parenType single
            | _ -> typeExpr single
        | _ -> commaJoin (List.map listItem args)

    and tagPayload (payload: TypeExpr) : Doc =
        let isPosition (n: string) = n.Length > 0 && Seq.forall Char.IsDigit n
        match payload.Kind with
        | TypeRecord fields when fields |> List.forall (function TypeField(n, false, _, Some _, None) -> not (isPosition n) | _ -> false) ->
            fields
            |> List.map (function
                | TypeField(n, _, isOptional, Some t, _) -> cat [ text n; (if isOptional then text "?" else Doc.empty); text ": "; typeExpr t ]
                | _ -> Doc.empty)
            |> commaJoin
        | TypeWithContext _ -> parenType payload
        | _ -> typeList payload

    and recordTypeField (field: TypeRecordField) : Doc =
        match field with
        | TypeField(n, false, false, Some t, None) when n.Length > 0 && Seq.forall Char.IsDigit n -> typeExpr t
        | TypeField(n, isMutable, isOptional, typeOpt, defaultValue) ->
            cat [
                (if isMutable then text "mut " else Doc.empty)
                text n
                (if isOptional then text "?" else Doc.empty)
                (match typeOpt with
                 | Some t -> cat [ text ": "; typeExpr t ]
                 | None -> Doc.empty)
                (match defaultValue with
                 | Some value -> cat [ text " = "; expr { full with Tail = false } value ]
                 | None -> Doc.empty)
            ]
        | TypeMember(n, Some t) -> cat [ text $"type {n} = "; typeExpr t ]
        | TypeMember(n, None) -> text $"type {n}"

    and contextSet (items: TypeExpr list) : Doc =
        let item (t: TypeExpr) =
            match t.Kind with
            | TypeApply(n, args) -> cat [ text n; text "("; typeArguments args; text ")" ]
            | _ -> typeExpr t
        cat [ text "["; Doc.join (text " + ") (List.map item items); text "]" ]

    and typeDefDoc (name: Identifier) (modifiers: TypeDefModifier list) (parameters: (Identifier * TypeExpr option) list) (body: TypeExpr) : Doc =
        let isPrivate = List.contains Private modifiers
        // `context Name = ...` and `context type Name = ...` both give `[Context]`; the modifiers' order is kept.
        let head =
            match modifiers |> List.filter ((<>) Private) with
            | [ Context ] -> text $"context {name}"
            | [ Context; Export ] -> text $"export context {name}"
            | others ->
                let modifier m =
                    match m with
                    | Export -> text "export "
                    | Context -> text "context "
                    | Private -> Doc.empty
                cat [ cat (List.map modifier others); text $"type {name}" ]
        let parameter (p, typeOpt) =
            match typeOpt with
            | Some t -> cat [ text p; text " :: "; typeExpr t ]
            | None -> text p
        let parametersDoc =
            match parameters with
            | [] -> Doc.empty
            | _ -> cat [ text "("; commaJoin (List.map parameter parameters); text ")" ]
        cat [ head; parametersDoc; text " = "; (if isPrivate then text "private " else Doc.empty); typeExpr body ]

    and importDoc (start: int) (finish: int) (items: ImportItem list) : Doc =
        let item (i: ImportItem) =
            cat [
                (match i.Source with
                 | ModuleImport m -> text m
                 | PathImport p -> text ("\"" + p + "\""))
                (match i.Alias with
                 | Some alias -> text $" as {alias}"
                 | None -> Doc.empty)
            ]
        match items with
        | [ single ] -> cat [ text "import "; item single ]
        | _ ->
            let children = items |> List.map (fun i -> i.Span.Start, i.Span.End, fun () -> item i)
            cat [ text "import ("; nest (cat [ hard; sequence false start finish children ]); hard; text ")" ]

    /// The items of the module, or of a module block.
    let rec topLevel (blankLines: bool) (start: int) (finish: int) (items: TopLevelItem list) : Doc =
        let defOf (i: TopLevelItem) =
            match i.Kind with
            | Def(ValueDef(isExport, n, t, value)) -> Some(isExport, n, t, value)
            | _ -> None
        let isAttribute (i: TopLevelItem) =
            match i.Kind with
            | Attribute _ -> true
            | _ -> false
        let isImport (i: TopLevelItem) =
            match i.Kind with
            | Import _ -> true
            | _ -> false
        let all = Array.ofList items
        destructuringRuns (items |> List.map (fun i -> i.Span, defOf i))
        |> List.map (fun (index, count, isDestructuring) ->
            let first = all[index]
            if isDestructuring then
                first.Span.Start, first.Span.End, first, fun () -> destructuringDef [ for k in index .. index + count - 1 -> (defOf all[k]).Value ] first.Span
            else
                first.Span.Start, first.Span.End, first, fun () -> item first)
        // An attribute line belongs to the item after it, so nothing comes between them.
        |> List.foldBack
            (fun (childStart, childEnd, first: TopLevelItem, build) children ->
                match children with
                | (_, nextEnd, nextIsImport, build') :: rest when isAttribute first ->
                    (childStart, nextEnd, nextIsImport, fun () -> cat [ build (); hard; build' () ]) :: rest
                | _ -> (childStart, childEnd, isImport first, build) :: children)
            <| []
        |> sequenceOf blankLines start finish

    and item (i: TopLevelItem) : Doc =
        match i.Kind with
        | ModuleDecl n -> text $"module {n}"
        | ModuleBlock(n, items) -> cat [ text $"module {n} ="; nest (cat [ hard; topLevel true i.Span.Start i.Span.End items ]) ]
        | Def(ValueDef(isExport, n, t, value)) -> defDoc isExport "def" n t value
        | Def(TypeDef(n, modifiers, parameters, body)) -> typeDefDoc n modifiers parameters body
        | Import items -> importDoc i.Span.Start i.Span.End items
        | Expr value -> expr statementPosition value
        | Attribute n -> text $"@{n}"

    Doc.render options.LineWidth (topLevel true 0 source.Length parsed.Items)

/// Formats the source of the file at `filePath`, or says why it does not parse.
let formatSource (options: Options) (filePath: string) (input: string) : Result<string, ParseError> =
    parseModuleWithTrivia filePath input |> Result.map (format options)
