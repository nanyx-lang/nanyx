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
    | "**" -> 8
    | "*" | "/" | "%" -> 7
    | "+" | "-" -> 6
    | "++" -> 5
    | "<" | ">" | "<=" | ">=" -> 4
    | "==" | "!=" -> 3
    | "and" -> 2
    | "or" -> 1
    | _ -> 6

/// The operators the parser takes where it does not cross lines (see `Position.Inline`).
let private inlineOperators = set [ "**"; "*"; "/"; "+"; "-"; "++"; "<"; ">"; "<="; ">="; "=="; "!=" ]

/// The operators that group to the right: `a ++ b ++ c` is `a ++ (b ++ c)`, and `2 ** 3 ** 2` is `2 ** (3 ** 2)`.
let private rightAssociative = set [ "++"; "**" ]

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

    /// The text of a double-quoted string that starts at `start` in the source, as it is written there, escapes and all:
    /// up to the next `"` or `{` that is not escaped. The parser decodes escapes, so where that is not found, or does
    /// not stand for `decoded`, the text is written with escapes of its own.
    let writtenStringText (start: int option) (decoded: string) =
        let written =
            start
            |> Option.bind (fun start ->
                let mutable i = start
                while i < source.Length && source[i] <> '"' && source[i] <> '{' do
                    i <-
                        if source[i] <> '\\' then i + 1
                        // `\u{...}` runs to its `}`.
                        elif String.CompareOrdinal(source, i + 1, "u{", 0, 2) = 0 && source.IndexOf('}', i) > i then source.IndexOf('}', i) + 1
                        else i + 2
                let raw = source.Substring(start, min i source.Length - start)
                if StringEscapes.decode raw = Some decoded then Some raw else None)
        Doc.textOrVerbatim (written |> Option.defaultWith (fun () -> StringEscapes.escape decoded))

    /// Where the text after the interpolation hole `value` starts: past the `}` that closes the hole.
    let afterHole (value: Expression) =
        let mutable i = value.Span.End
        while valid value.Span && i < source.Length && Char.IsWhiteSpace source[i] do
            i <- i + 1
        if valid value.Span && i < source.Length && source[i] = '}' then Some (i + 1) else None

    /// Where the `\match` is, if the match `e` on `value` was written `value \match | ...`, rather than `match value | ...`.
    let pipedMatchAt (e: Expression) (value: Expression) =
        if not (valid e.Span && valid value.Span) || startsWith e.Span "match" then
            None
        else
            let mutable i = value.Span.End
            while i < e.Span.End && (Char.IsWhiteSpace source[i] || source[i] = ')') do
                i <- i + 1
            if i + 6 <= source.Length && String.CompareOrdinal(source, i, "\\match", 0, 6) = 0 then Some i else None

    let isPipedMatch (e: Expression) (value: Expression) = (pipedMatchAt e value).IsSome

    // Comments are placed once each; whatever no construct places goes before the line it was in. A doc comment's
    // continuation lines are set one indent step past its first line.
    let comments =
        parsed.Trivia
        |> List.choose (function
            | LineComment(commentText, _, span) when commentText.StartsWith "--|" ->
                Some(DocComments.relaidOut options.IndentSize commentText, span)
            | LineComment(commentText, _, span) -> Some(commentText.TrimEnd(), span)
            | BlankLines _ -> None)
        |> Array.ofList

    // The `#!` line and directives at the top are kept as they are, with the comments among them.
    let headerEnd = Directives.headerEnd source

    let placed = comments |> Array.map (fun (_, span) -> span.Start < headerEnd)

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

    /// A new id for a `NamedGroup`.
    let nextGroupId =
        let last = ref 0
        fun () ->
            last.Value <- last.Value + 1
            last.Value

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
    /// a block of statements, but not a list of imports): a child that spans more than one line gets a blank line
    /// before it (and its leading comments) and after it, except at the start and end and between two imports;
    /// between children that each fit on one line, a blank line (however many there were) is kept, and none is added.
    /// Which children span lines is decided when the document is laid out (see `Doc.Stack`), so a child that breaks
    /// because it is long counts too.
    let rec sequence (blankLines: bool) (start: int) (finish: int) (children: (int * int * (unit -> Doc)) list) : Doc =
        sequenceOf blankLines start finish (children |> List.map (fun (childStart, childEnd, build) -> childStart, childEnd, false, build))

    /// `sequence`, with each child saying whether it is an import.
    and sequenceOf (blankLines: bool) (start: int) (finish: int) (children: (int * int * bool * (unit -> Doc)) list) : Doc =
        let entries = ResizeArray<StackEntry>()

        let placeComment leading i =
            placed[i] <- true
            entries.Add(StackComment(fst comments[i], leading))

        // The comments, and the author's blank lines, between the offsets `a` and `b`, in order.
        let between a b =
            let blanks =
                if blankLines then
                    blankRuns |> List.filter (fun span -> a <= span.Start && span.Start < b) |> List.map (fun span -> span.Start, None)
                else
                    []
            let inBetween = unplacedIn a b |> List.map (fun i -> (snd comments[i]).Start, Some i)
            for _, entry in List.sortBy fst (blanks @ inBetween) do
                match entry with
                | Some i -> placeComment true i
                | None -> entries.Add StackBlank

        let mutable previous = start
        for childStart, childEnd, isImport, build in children do
            let childStart = max childStart previous
            let childEnd = max childEnd childStart
            between previous childStart
            let placedBefore = Array.copy placed
            let doc = build ()
            let firstPlacedByChild =
                seq { for i in 0 .. comments.Length - 1 do if placed[i] && not placedBefore[i] then yield (snd comments[i]).Start }
                |> Seq.fold min Int32.MaxValue
            let orphans = unplacedIn childStart childEnd
            let before, after = orphans |> List.partition (fun i -> (snd comments[i]).Start < firstPlacedByChild)
            for i in before do
                placeComment true i
            let trailing =
                match unplacedIn childEnd (lineEnd (max childStart (childEnd - 1)) + 1) with
                | i :: _ when childEnd > childStart -> Some i
                | _ -> None
            match trailing with
            | Some i when after.IsEmpty ->
                placed[i] <- true
                entries.Add(StackItem(cat [ doc; Doc.lineSuffix (fst comments[i]) ], isImport))
            | _ ->
                entries.Add(StackItem(doc, isImport))
                for i in after do
                    placeComment false i
                trailing |> Option.iter (placeComment false)
            previous <- childEnd
        between previous finish
        Stack(blankLines, List.ofSeq entries)

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

    /// The pattern of a destructuring def as written, from after `def` to the `=` outside any brackets, with its
    /// spacing made regular. It keeps the form it was written in: `def (x, y) =` or `def x, y =`.
    let writtenPattern (span: SourceSpan) =
        if not (valid span) then
            None
        else
            let defAt = source.IndexOf("def", span.Start, StringComparison.Ordinal)
            if defAt < 0 || defAt >= span.End then
                None
            else
                let startAt = defAt + 3
                let mutable depth = 0
                let mutable equalsAt = -1
                let mutable i = startAt
                while equalsAt < 0 && i < span.End do
                    match source[i] with
                    | '(' -> depth <- depth + 1
                    | ')' -> depth <- depth - 1
                    | '=' when depth = 0 -> equalsAt <- i
                    | _ -> ()
                    i <- i + 1
                if equalsAt < 0 then
                    None
                else
                    Some(Regex.Replace(source.Substring(startAt, equalsAt - startAt), @"\s+", "").Replace(",", ", "))

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

    /// A lambda's parameters as written, each with the pattern it was written as, if any, and its body as written. A
    /// pattern parameter (`{ (a, b) -> a + b }`) is taken apart by a one-arm match the parser puts at the top of the
    /// body, over the parameter that stands in its place (`patternArgument`).
    let writtenLambda (e: Expression) (parameters: (Identifier * TypeExpr option) list) (body: Expression) =
        let plain () = parameters |> List.map (fun parameter -> parameter, None), body
        match body.Kind with
        | Match(scrutinees, [ patterns, inner ]) when valid e.Span && body.Span = e.Span && scrutinees.Length = patterns.Length ->
            let taken =
                List.zip scrutinees patterns
                |> List.choose (fun (scrutinee, p) ->
                    match scrutinee.Kind with
                    | IdentifierExpr n -> Some(n, p)
                    | _ -> None)
                |> Map.ofList
            let written =
                parameters |> List.mapi (fun index (n, t) -> (n, t), (if n = patternArgument (index + 1) then Map.tryFind n taken else None))
            if taken.Count = scrutinees.Length && (List.choose snd written).Length = taken.Count then written, inner else plain ()
        | _ -> plain ()

    /// Whether the lambda `e`, which has no parameters, was written with a unit pattern for them, `{ () -> ... }`.
    let writtenWithUnit (e: Expression) =
        if not (valid e.Span) || source[e.Span.Start] <> '{' then
            false
        else
            let afterBrace = source.Substring(e.Span.Start + 1, e.Span.End - e.Span.Start - 1).TrimStart()
            afterBrace.StartsWith "(" && afterBrace.Substring(1).TrimStart().StartsWith ")"
            && afterBrace.Substring(1).TrimStart().Substring(1).TrimStart().StartsWith "->"

    let rec expr (position: Position) (e: Expression) : Doc =
        if needsParentheses position e then parens (exprBare full e) else exprBare position e

    and needsParentheses (position: Position) (e: Expression) =
        match e.Kind with
        | BinaryOp(op, _, _) -> precedence op < position.Prec || (position.Inline && not (inlineOperators.Contains op))
        | RangeSyntax.Call _ -> 4 < position.Prec || position.Inline
        | FunctionCall("not", _, [ _ ]) -> 8 < position.Prec || position.Inline
        | FunctionCall(name, _, [ _ ]) when name = NegateSyntax.builtIn -> 8 < position.Prec
        | Pipe _ -> 0 < position.Prec
        | _ when isOpenEnded e -> not position.Tail || position.Prec > 8
        | _ -> false

    /// Whether the construct takes everything after it, as the last arm of a `match` takes the rest of the line.
    and isOpenEnded (e: Expression) =
        match e.Kind with
        | Match _
        | Except _
        | IfExpr _
        | UseIn _
        | WorkflowBindExpr _
        | WorkflowReturnExpr _ -> true
        | FunctionCall("__with", _, [ _; _ ]) -> true
        | ForSyntax.Call _ -> true
        | _ -> false

    and exprBare (position: Position) (e: Expression) : Doc =
        match e.Kind with
        | LiteralExpr literal -> literalDoc e.Span literal
        | UnitExpr -> text "()"
        | IdentifierExpr n when n = implicitArgument -> Doc.empty
        | IdentifierExpr n when n = notImplemented -> text n
        | IdentifierExpr n -> text (Names.written n)
        | BinaryOp(op, l, r) ->
            match spreadSegments e with
            | Some segments -> listDoc e.Span segments
            | None -> binary position op l r
        | FunctionCall("not", _, [ operand ]) -> cat [ text "not "; expr operandPosition operand ]
        // `-` is written against its operand. A number or another negation after it is parenthesized, since `-5` is a
        // literal and `--` starts a comment.
        | FunctionCall(name, _, [ operand ]) when name = NegateSyntax.builtIn ->
            let joinsTheMinus =
                match operand.Kind with
                | LiteralExpr(IntLit _ | FloatLit _) -> true
                | FunctionCall(inner, _, [ _ ]) -> inner = NegateSyntax.builtIn
                | _ -> false
            cat [ text "-"; (if joinsTheMinus then parens (exprBare full operand) else expr operandPosition operand) ]
        | RangeSyntax.Call(a, Some b) ->
            cat [ expr { position with Tail = false; Prec = 4 } a; text ".."; expr { position with Prec = 5 } b ]
        | RangeSyntax.Call(a, None) -> cat [ expr { position with Tail = false; Prec = 4 } a; text ".." ]
        | FunctionCall("__with", _, [ record; updates ]) when (match updates.Kind with RecordExpr fields -> fields |> List.forall (function NamedField _ -> true | _ -> false) | _ -> false) ->
            let fields = match updates.Kind with RecordExpr fields -> fields | _ -> []
            let count = fields.Length
            let update index field =
                match field with
                | NamedField(n, value) -> cat [ text (Names.written n); text " = "; expr { full with Tail = (index = count - 1) } value ]
                | PositionalField value -> expr full value
            cat [ expr operandPosition record; text " with "; commaJoin (List.mapi update fields) ]
        | ForSyntax.Call(iterables, loop, parameters, _) when parameters = List.init iterables.Length (fun index -> (ForSyntax.element (index + 1), None)) ->
            forDoc iterables loop
        | MapSyntax.Call entries -> mapDoc e.Span entries
        | FunctionCall(n, nameSpan, args) ->
            let written = Names.written n
            cat [ text written; callArguments e (nameEnd nameSpan (e.Span.Start + written.Length)) args ]
        | Lambda(parameters, body) -> lambdaDoc e parameters body false
        | Pipe _ -> pipeChain position e
        // A block where a def's value, an arm's body or a branch would not take one is a `do` block.
        | Block _ -> cat [ text "do"; nest (cat [ hard; block e ]) ]
        | Match([ scrutinee ], arms) when isPipedMatch e scrutinee -> pipeChain position e
        | Match(scrutinees, arms) ->
            match ifArm e with
            | Some _ -> ifDoc e
            | None -> matchDoc e scrutinees arms
        | Except(value, arms) -> exceptDoc position e value arms
        | IfExpr _ -> ifDoc e
        | TupleExpr items -> fieldsDoc e.Span.Start e.Span.End (List.map PositionalField items)
        | RecordExpr fields -> fieldsDoc e.Span.Start e.Span.End fields
        | ListExpr items -> listDoc e.Span [ Choice1Of2 items ]
        | TagExpr(n, None) -> text ("#" + n)
        | TagExpr(n, Some (Kind UnitExpr)) -> text $"#{n}()"
        | TagExpr(n, Some payload) -> cat [ text ("#" + n); parenFields payload (e.Span.Start + 1 + n.Length) e.Span.End ]
        | MemberAccess(target, field, _) ->
            // A tuple's index, `.0`, is written as it is, not in backticks.
            let isIndex = field <> "" && String.forall Char.IsDigit field
            cat [ expr operandPosition target; text "."; text (if isIndex then field else Names.written field) ]
        | ContextMemberAccess(context, field) -> cat [ contextHead context; text "."; text (Names.written field) ]
        | ContextMemberCall(context, field, args) ->
            let count = args.Length
            let argument index arg = expr { full with Tail = (index = count - 1) } arg
            cat [ contextHead context; text "."; text (Names.written field); text "("; commaJoin (List.mapi argument args); text ")" ]
        | UseIn(value, body) ->
            let head = cat [ text "use "; expr { full with Tail = false } value; text " in" ]
            match body.Kind with
            | Block _ -> cat [ head; nest (cat [ hard; block body ]) ]
            | _ -> cat [ head; text " "; expr full body ]
        | WorkflowBindExpr(n, value) ->
            let keyword = if n = "try" && not (startsWith e.Span "try!") then "try " else n + "! "
            cat [ text keyword; expr full value ]
        | WorkflowReturnExpr value -> cat [ text "return "; expr full value ]
        | InterpolatedString parts ->
            // Each part of text starts after the opening quote or the hole before it.
            let part start p =
                match p with
                | StringText s -> writtenStringText start s, None
                | StringExpr inner -> cat [ text "{"; expr full inner; text "}" ], afterHole inner
            let opening = if startsWith e.Span "\"" then Some (e.Span.Start + 1) else None
            cat [ text "\""; cat (List.mapFold part opening parts |> fst); text "\"" ]

    and literalDoc (span: SourceSpan) (literal: Literal) : Doc =
        if valid span && span.End > span.Start then
            Doc.textOrVerbatim (source.Substring(span.Start, span.End - span.Start))
        else
            match literal with
            | StringLit s -> Doc.textOrVerbatim ("\"" + StringEscapes.escape s + "\"")
            | IntLit i -> text (string i)
            | FloatLit f -> text (f.ToString("R", CultureInfo.InvariantCulture))
            | BoolLit b -> text (if b then "true" else "false")

    /// An operator as written: `&&` and `||` build the same tree as `and` and `or`, and the source says which it was.
    and writtenOperator (op: string) (l: Expression) (r: Expression) =
        let between =
            if valid l.Span && valid r.Span && l.Span.End <= r.Span.Start then
                source.Substring(l.Span.End, r.Span.Start - l.Span.End).Trim(' ', '\t', '\n', '(', ')')
            else
                ""
        match op with
        | "and" when between = "&&" -> "&&"
        | "or" when between = "||" -> "||"
        | _ -> op

    /// A chain of operators of one precedence, `a ++ b ++ c`, as its operands and the operators between them. In a
    /// position that may cross lines, as many operands go on a line as fit, each line ending in an operator (the
    /// parser takes a line break after an operator, not before one), and the lines after the first indented once.
    and binary (position: Position) (op: string) (l: Expression) (r: Expression) : Doc =
        let p = precedence op
        let isRight = rightAssociative.Contains op
        // A node of the chain: same precedence, needing no brackets where it is, and not a list with spreads.
        let continues (x: Expression) =
            match x.Kind with
            | BinaryOp(op', _, _) ->
                precedence op' = p
                && rightAssociative.Contains op' = isRight
                && not (position.Inline && not (inlineOperators.Contains op'))
                && (spreadSegments x).IsNone
            | _ -> false
        let rec flattenLeft (x: Expression) =
            match x.Kind with
            | BinaryOp(op', a, b) when continues x ->
                let operands, operators = flattenLeft a
                operands @ [ b ], operators @ [ writtenOperator op' a b ]
            | _ -> [ x ], []
        let rec flattenRight (x: Expression) =
            match x.Kind with
            | BinaryOp(op', a, b) when continues x ->
                let operands, operators = flattenRight b
                a :: operands, writtenOperator op' a b :: operators
            | _ -> [ x ], []
        let operands, operators =
            if isRight then
                let rest, operators = flattenRight r
                l :: rest, writtenOperator op l r :: operators
            else
                let first, operators = flattenLeft l
                first @ [ r ], operators @ [ writtenOperator op l r ]
        let count = operands.Length
        let operandDoc index operand =
            let isLast = index = count - 1
            let prec =
                if isRight then (if isLast then p else p + 1)
                else (if index = 0 then p else p + 1)
            expr { position with Tail = isLast && position.Tail; Prec = prec } operand
        let docs = List.mapi operandDoc operands
        // A shorthand lambda's implicit argument, the first operand, is printed as nothing, and so is the space after it.
        let leftOut index = index = 0 && (List.head operands).Kind = IdentifierExpr implicitArgument
        if position.Inline then
            List.zip (List.take (count - 1) docs) operators
            |> List.mapi (fun index (doc, operator) -> if leftOut index then [ text $"{operator} " ] else [ doc; text $" {operator} " ])
            |> List.concat
            |> fun parts -> cat (parts @ [ List.last docs ])
        else
            let items =
                List.zip (List.take (count - 1) docs) operators
                |> List.mapi (fun index (doc, operator) -> [ (if leftOut index then text operator else cat [ doc; text $" {operator}" ]); Doc.line ])
                |> List.concat
            nest (Fill(items @ [ List.last docs ]))

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
            let children =
                items
                |> List.mapi (fun index ((_, item: Expression) as entry) -> item.Span.Start, item.Span.End, fun () -> itemDoc index entry)
            cat [ text "["; nest (cat [ hard; sequence false span.Start span.End children ]); hard; text "]" ]
        else
            bracketed "[" "]" (List.mapi itemDoc items)

    /// A map literal, `[k => v, ...]`, laid out as a list's items are.
    and mapDoc (span: SourceSpan) (entries: (Expression * Expression) list) : Doc =
        let count = entries.Length
        let entryDoc index (key: Expression, value: Expression) =
            cat [ expr { full with Tail = false } key; text " => "; expr { full with Tail = (index = count - 1) } value ]
        if valid span && hasCommentsIn span.Start span.End then
            let children =
                entries
                |> List.mapi (fun index ((key: Expression, value: Expression) as entry) -> key.Span.Start, value.Span.End, fun () -> entryDoc index entry)
            cat [ text "["; nest (cat [ hard; sequence false span.Start span.End children ]); hard; text "]" ]
        else
            bracketed "[" "]" (List.mapi entryDoc entries)

    /// Items between brackets, on one line separated by commas if they fit, or else one per line, separated by line
    /// breaks, with the closing bracket on a line of its own (as a record's fields or a list's items may be written):
    ///
    ///   use Log(
    ///     log = { message ->
    ///       println(message)
    ///     }
    ///   )
    and bracketed (opening: string) (closing: string) (items: Doc list) : Doc =
        match items with
        | [] -> text (opening + closing)
        | _ ->
            let separator = cat [ IfBreak(Doc.empty, text ","); Doc.line ]
            Doc.group (cat [ text opening; nest (cat [ Doc.softLine; Doc.join separator items ]); Doc.softLine; text closing ])

    /// Fields in brackets: a record, a tuple, or a call's arguments, laid out by `bracketed`, or one per line when there
    /// is a comment among them.
    and fieldsDoc (start: int) (finish: int) (fields: RecordField list) : Doc =
        let count = fields.Length
        let fieldDoc index field =
            let position = { full with Tail = (index = count - 1) }
            match field with
            | NamedField(n, value) -> cat [ text (Names.written n); text " = "; expr position value ]
            | PositionalField value -> expr position value
        let valueOf field =
            match field with
            | NamedField(_, value)
            | PositionalField value -> value
        let onePerLine (build: int -> RecordField -> Doc) =
            let children =
                fields
                |> List.mapi (fun index field ->
                    let value = valueOf field
                    value.Span.Start, value.Span.End, fun () -> build index field)
            cat [ text "("; nest (cat [ hard; sequence false start finish children ]); hard; text ")" ]
        // With a comment among them, the fields are built inside the sequence, which places the comments.
        if hasCommentsIn start finish then onePerLine fieldDoc else bracketed "(" ")" (List.mapi fieldDoc fields)

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

    /// Whether `argument`, one of the values a call's brackets were parsed into, was written after them instead: a
    /// trailing lambda ends where its call does, and one in the brackets ends before the `)`.
    and endsCall (call: Expression) (argument: Expression) =
        match argument.Kind with
        | Lambda _ -> valid argument.Span && valid call.Span && argument.Span.End >= call.Span.End
        | _ -> false

    and pipeArguments (call: Expression) (afterName: int) (args: Expression list) : Doc =
        let withTrailing (fields: RecordField list) (lambda: Expression) =
            cat [ fieldsDoc afterName lambda.Span.Start fields; text " "; expr full lambda ]
        match args with
        | [] -> Doc.empty
        | [ argument ] when isTrailingLambda afterName argument -> cat [ text " "; expr full argument ]
        // `f(a, y = 1) { ... }`: the lambda was put among the named fields' positional ones. They go back in the order
        // they were written.
        | [ Kind (RecordExpr fields) ] when fields |> List.exists (function PositionalField e -> endsCall call e | NamedField _ -> false) ->
            let lambda = fields |> List.pick (function PositionalField e when endsCall call e -> Some e | _ -> None)
            let fieldStart = function PositionalField e -> e.Span.Start | NamedField(_, e) -> e.Span.Start
            let written = fields |> List.filter (function PositionalField e -> not (obj.ReferenceEquals(e, lambda)) | NamedField _ -> true)
            withTrailing (written |> List.sortBy fieldStart) lambda
        | [ argument ] -> parenFields argument afterName call.Span.End
        // A pipe keeps its brackets' values as one argument, before the lambda: `x \f(a, b) { ... }`.
        | [ argument; lambda ] when (match call.Kind with Pipe _ -> true | _ -> false) ->
            cat [ parenFields argument afterName lambda.Span.Start; text " "; expr full lambda ]
        // `f(a, b) { ... }`, whose brackets' values and lambda are the call's arguments. A tuple among them was written
        // in brackets of its own, as in `f((a, b)) { ... }`.
        | _ when endsCall call (List.last args) ->
            let lambda = List.last args
            withTrailing (args |> List.take (args.Length - 1) |> List.map PositionalField) lambda
        | _ -> cat [ text "("; commaJoin (List.map (expr full) args); text ")" ]

    /// A lambda. With `bodyOnOwnLine`, a body that is an expression goes on a line of its own after the `->`, as the
    /// one statement of a block (see `defDoc`).
    and lambdaDoc (e: Expression) (parameters: (Identifier * TypeExpr option) list) (body: Expression) (bodyOnOwnLine: bool) : Doc =
        match shorthand e parameters body with
        | Some doc -> doc
        | None ->
            let parameters, body = writtenLambda e parameters body
            // An annotated parameter is bracketed, so an arrow after its annotation is not read as part of it.
            let parameterDoc ((n, typeOpt), patternOpt) =
                match patternOpt, typeOpt with
                | Some p, _ -> pattern p
                | None, Some t -> cat [ text "("; text n; text ": "; typeExpr t; text ")" ]
                | None, None -> text n
            let head =
                match parameters with
                | [] when writtenWithUnit e -> text "{ () ->"
                | [] -> text "{"
                | _ -> cat [ text "{ "; commaJoin (List.map parameterDoc parameters); text " ->" ]
            // A body on its own line is read as a statement, which can't take every operator (`b == 1 or b == 2` is
            // three statements there), so where the layout is ours to choose, a body that would need parentheses
            // there stays on the `->`'s line instead, as does a block of just that body in parentheses.
            let bracketedAsStatement (x: Expression) = needsParentheses statementPosition x && not (needsParentheses full x)
            let body =
                match body.Kind with
                | Block [ { Kind = ExprStatement single } ] when
                    bodyOnOwnLine && bracketedAsStatement single && not (valid e.Span && hasCommentsIn e.Span.Start e.Span.End) -> single
                | _ -> body
            match body.Kind with
            // The statements of a lambda are read regardless of lines and indentation, so the comments anywhere between
            // its braces go among them.
            | Block statements -> cat [ head; nest (cat [ hard; statementsInSequence true e.Span.Start e.Span.End statements ]); hard; text "}" ]
            | _ when bodyOnOwnLine && not (bracketedAsStatement body) -> cat [ head; nest (cat [ hard; expr statementPosition body ]); hard; text "}" ]
            | _ ->
                let bodyDoc = expr full body
                // A body that starts like a shorthand lambda (`{ - 1 }`, `{ .name }`) needs the arrow.
                let head =
                    let first = (Doc.render Int32.MaxValue bodyDoc).TrimStart()
                    if List.isEmpty parameters && not (writtenWithUnit e) && first.Length > 0 && "*/+-<>=!.|".Contains first[0] then text "{ ->" else head
                // A body that spans lines is indented once, and the `}` goes back to the lambda's line's indent, so
                // the braces of `{ x -> proc {` ... `}` do not end up one above the other.
                Doc.group (cat [ head; text " "; nest bodyDoc; Doc.line; text "}" ])

    /// The shorthand lambdas the parser expands: `{ }`, `{ * }`, a body that leaves out its first operand (`{ * 2 }`,
    /// `{ .name }`, which the implicit argument is printed as nothing in), and `{ | pattern -> ... }`.
    and shorthand (e: Expression) parameters (body: Expression) : Doc option =
        let made (x: Expression) = valid e.Span && x.Span = e.Span
        match parameters, body.Kind with
        | [], UnitExpr when made body -> Some(text "{ }")
        | [ (first, None); (second, None) ], BinaryOp(op, l, r) when
            first = implicitArgument && second = implicitSecondArgument && l.Kind = IdentifierExpr first && r.Kind = IdentifierExpr second ->
            Some(text $"{{ {op} }}")
        | [ (only, None) ], _ when only = implicitArgument ->
            Some(Doc.group (cat [ text "{ "; nest (expr full body); Doc.line; text "}" ]))
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

    /// A pipe chain, `value \f \g(x)`, perhaps with `\match` steps. It stays on one line when it was written on one and
    /// fits; otherwise each step goes on a line of its own, indented once from the line the chain starts on, as the
    /// parser needs a pipe that continues a statement to be indented more than the statement's line. A chain that
    /// takes up lines anyway, with a lambda that spans lines in it, is broken only if it was written so. A step after
    /// a `\match` always goes on a line of its own, since the arms take the rest of their lines. A chain in a position
    /// that something follows on its line stays on one line.
    and pipeChain (position: Position) (e: Expression) : Doc =
        // The value at the start of the chain, and each step with the expression that is the chain up to it: the value
        // it pipes, the offset of its `\` or name in the source, and its document.
        let rec collect (e: Expression) steps =
            match e.Kind with
            | Pipe(value, n, nameSpan, args) ->
                let at = match nameSpan with Some span when valid span -> span.Start | _ -> -1
                let doc = cat [ text "\\"; text n; pipeArguments e (nameEnd nameSpan value.Span.End) args ]
                collect value ((value, at, false, doc) :: steps)
            | Match([ value ], arms) ->
                match pipedMatchAt e value with
                | Some at ->
                    let doc = cat [ text "\\match"; nest (cat [ hard; armsInSequence value.Span.End e.Span.End arms ]) ]
                    collect value ((value, at, true, doc) :: steps)
                | None -> e, steps
            | _ -> e, steps
        let root, steps = collect e []
        let rootDoc = expr { position with Tail = false; Prec = 0 } root
        let isMatch = steps |> List.map (fun (_, _, isMatch, _) -> isMatch)
        let afterMatch = false :: List.truncate (steps.Length - 1) isMatch
        // Whether the step at `at` starts a line of its own after the value it pipes.
        let startsLine (value: Expression) at = at >= 0 && valid value.Span && value.Span.End > 0 && lineOf at > lineOf (value.Span.End - 1)
        let writtenOnLines =
            List.zip steps afterMatch |> List.exists (fun ((value, at, _, _), afterMatch) -> not afterMatch && startsLine value at)
        let stepDocs = steps |> List.map (fun (_, _, _, doc) -> doc)
        // The comments between a step and the value it pipes: one at the end of the value's last line stays there, and
        // any other goes on a line of its own before the step.
        let commentsBefore (value: Expression) at =
            if at < 0 || not (valid value.Span) then
                Doc.empty
            else
                cat [ for i in unplacedIn value.Span.End at ->
                          placed[i] <- true
                          let commentText, span = comments[i]
                          if lineOf span.Start = lineOf (max 0 (value.Span.End - 1)) then Doc.lineSuffix commentText
                          else cat [ hard; Doc.comment commentText ] ]
        if position.Tail && writtenOnLines then
            cat [ rootDoc; nest (cat [ for value, at, _, doc in steps -> cat [ commentsBefore value at; hard; doc ] ]) ]
        elif position.Tail && steps.Length > 1 && not (List.contains true isMatch)
             && not (List.exists Doc.hasHardLine (rootDoc :: stepDocs)) then
            Doc.group (cat [ rootDoc; nest (cat [ for doc in stepDocs -> cat [ Doc.line; doc ] ]) ])
        else
            let step doc afterMatch = if afterMatch then nest (cat [ hard; doc ]) else cat [ text " "; doc ]
            cat (rootDoc :: List.map2 step stepDocs afterMatch)

    /// `value except pattern -> replacement` when there is one arm (perhaps with alternatives), and otherwise `value
    /// except` with the arms on the lines after it, as a match has them.
    and exceptDoc (position: Position) (e: Expression) (value: Expression) (arms: MatchArm list) : Doc =
        let valueDoc = expr { position with Tail = false; Prec = 0 } value
        match singleArm arms with
        | Some(patterns, body) -> cat [ valueDoc; text " except "; patterns; armBody true body ]
        | None -> cat [ valueDoc; text " except"; nest (cat [ hard; armsInSequence value.Span.End e.Span.End arms ]) ]

    /// The patterns of arms that all share one body, joined as alternatives `a | b`, and that body.
    and singleArm (arms: MatchArm list) : (Doc * Expression) option =
        match List.distinctBy (fun (_, body: Expression) -> body.Id) arms with
        | [ _, body ] -> Some(arms |> List.map (fun (ps, _) -> commaJoin (List.map pattern ps)) |> Doc.join (text " | "), body)
        | _ -> None

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
            let anyBlock = isBlock thenBranch || (elseBranch |> Option.exists isBlock)
            // Two expressions go on the `if`'s line if they fit, or else `then` and `else` start lines of their own,
            // indented once, which parses the same.
            if not anyBlock then
                let thenPart = cat [ Doc.line; text "then "; expr { full with Tail = elseBranch.IsNone } thenBranch ]
                let elsePart =
                    match elseBranch with
                    | Some elseBranch -> cat [ Doc.line; text "else "; expr full elseBranch ]
                    | None -> Doc.empty
                Doc.group (cat [ text "if "; ifHead head; nest (cat [ thenPart; elsePart ]) ])
            else
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

    /// `for xs, ys | arms` and `for xs -> body`, parsed as a call to `__for` with a lambda of the elements. One arm
    /// (perhaps with alternatives) stays on the head's line, `for xs | x -> body`; several go on the lines after it.
    and forDoc (iterables: Expression list) (loop: Expression) : Doc =
        let head = cat [ text "for "; commaJoin (iterables |> List.map (expr { full with Tail = false })) ]
        match loop.Kind with
        | Lambda(parameters, body) ->
            match body.Kind with
            | Match(elements, arms) when elements |> List.map _.Kind = (parameters |> List.map (fst >> IdentifierExpr)) && body.Span = loop.Span ->
                match singleArm arms with
                | Some(patterns, armBodyExpr) -> cat [ head; text " | "; patterns; armBody true armBodyExpr ]
                | None -> cat [ head; nest (cat [ hard; armsInSequence loop.Span.Start loop.Span.End arms ]) ]
            | _ -> cat [ head; armBody true body ]
        | _ -> head

    /// A context and its type arguments, as in `C(T).field`.
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
        | UseStatement value -> cat [ text "use "; expr full value ]
        | ExprStatement value -> expr statementPosition value

    and defDoc (isExport: bool) (keyword: string) (name: string) (typeOpt: TypeExpr option) (value: Expression) : Doc =
        defDocNamed isExport keyword (text (Names.written name)) typeOpt value

    /// A def whose name (or destructuring pattern) is already laid out.
    and defDocNamed (isExport: bool) (keyword: string) (nameDoc: Doc) (typeOpt: TypeExpr option) (value: Expression) : Doc =
        let name = cat [ (if isExport then text "export " else Doc.empty); text $"{keyword} "; nameDoc ]
        // A signature that does not fit on the def's line goes on the next one, as does the `=`, and the value is
        // indented under them:
        //
        //   def priceLine
        //     : list(Product), OrderLine -> #some(Priced) | #unknownSku(string)
        //     = { products, line ->
        //       ...
        //     }
        let signatureGroup = nextGroupId ()
        let head =
            match typeOpt with
            | Some t ->
                NamedGroup(signatureGroup, cat [ name; nest (cat [ Doc.softLine; text ": "; signature t; Doc.line; text "=" ]) ])
            | None -> cat [ name; text " =" ]
        let underHead doc = IfGroupBroken(signatureGroup, nest doc, doc)
        // A def with a type annotation whose value is a lambda keeps the signature's line to itself: the body goes on
        // the next line, unless it is only a name or a literal (`def id: a -> a = { x -> x }`), or a statement could
        // not take it without parentheses (see `lambdaDoc`).
        let isTrivial (body: Expression) =
            match body.Kind with
            | IdentifierExpr _
            | LiteralExpr _
            | UnitExpr
            | TagExpr(_, None) -> true
            | _ -> false
        match value.Kind with
        | Block _ -> cat [ head; underHead (nest (cat [ hard; block value ])) ]
        | Lambda(parameters, body) when typeOpt.IsSome && not (isTrivial (snd (writtenLambda value parameters body))) ->
            cat [ head; underHead (cat [ text " "; lambdaDoc value parameters body true ]) ]
        | _ -> cat [ head; underHead (cat [ text " "; expr full value ]) ]

    and destructuringDef (group: Def list) (span: SourceSpan) : Doc =
        let _, _, _, value = List.head group
        let bindings = List.tail group
        let isExport = bindings |> List.exists (fun (isExport, _, _, _) -> isExport)
        match value.Kind with
        // A pattern that may fail to match (`def $Money(pence) = m`) is bound through a one-arm match on the
        // value; the def is printed from that arm's pattern.
        | Match([ scrutinee ], [ [ p ], _ ]) -> defDocNamed isExport "def" (pattern p) None scrutinee
        | _ ->
            let patternText = writtenPattern span |> Option.defaultWith (fun () -> rebuiltPattern bindings)
            defDocNamed isExport "def" (text patternText) None value

    and pattern (p: Pattern) : Doc =
        match p.Kind with
        | LiteralPattern literal -> literalDoc p.Span literal
        | IdentifierPattern n -> text n
        | WildcardPattern -> text "_"
        | UnitPattern -> text "()"
        | ElsePattern -> text "else"
        | TuplePattern ps -> cat [ text "("; commaJoin (List.map pattern ps); text ")" ]
        | RecordPattern(n, ps) -> cat [ text n; text "("; commaJoin (List.map pattern ps); text ")" ]
        | RecordMemberPattern(fields, hasRest) ->
            let restDoc = if hasRest then [ text "..." ] else []
            cat [ text "("; commaJoin ((fields |> List.map (fun (n, p) -> cat [ text n; text " = "; pattern p ])) @ restDoc); text ")" ]
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
        // The bare `> 5` is kept bare: it is the shorthand lambda `{ > 5 }` without its braces.
        | GuardPattern(Kind (Lambda([ (only, None) ], body))) when
            only = implicitArgument && valid p.Span && p.Span.Start < source.Length && source[p.Span.Start] <> '{' ->
            expr full body
        | GuardPattern predicate -> expr full predicate
        | AndPattern parts -> parts |> List.map pattern |> List.reduce (fun left right -> cat [ left; text " & "; right ])
        | TagPattern(n, None) -> text ("#" + n)
        | TagPattern(n, Some (Kind UnitPattern)) -> text $"#{n}()"
        | TagPattern(n, Some payload) ->
            let afterName = p.Span.Start + 1 + n.Length
            if valid p.Span && afterName < source.Length && source[afterName] <> '(' then
                match payload.Kind with
                // `#n (a & b)`: without its brackets, the and-pattern would take in the tag.
                | AndPattern _ -> cat [ text $"#{n} ("; pattern payload; text ")" ]
                | _ -> cat [ text $"#{n} "; pattern payload ]
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
        | TypeHole -> text "_"
        | TypeUnit -> text "()"
        | TypeTuple _ -> parenType t
        | TypeRecord fields -> cat [ text "("; commaJoin (List.map recordTypeField fields); text ")" ]
        | TypeApply(n, args) -> cat [ text n; text "("; typeArguments args; text ")" ]
        | TypeTag(n, None) -> text ("#" + n)
        | TypeTag(n, Some (Kind TypeUnit)) -> text $"#{n}()"
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
                | TypeField(n, _, isOptional, Some t, _) -> cat [ text (Names.written n); (if isOptional then text "?" else Doc.empty); text ": "; typeExpr t ]
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
                text (Names.written n)
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
            // An operator required by the member it reads: `` `+`: a, a -> a ``.
            | TypeRecord [ TypeField(n, false, false, Some memberType, None) ] -> cat [ text (Names.written n); text ": "; typeList memberType ]
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
        cat [ head; parametersDoc; text " = "; (if isPrivate then text "private " else Doc.empty); typeBody body ]

    /// The type a type or context definition stands for. A record of more than three fields, or with a comment among
    /// them, has one field per line, named fields separated by newlines and positional ones followed by commas (the
    /// parser takes nothing else after them):
    ///
    ///   type Product = (
    ///     sku: string
    ///     name: string
    ///     price: int
    ///     stock: int
    ///   )
    and typeBody (body: TypeExpr) : Doc =
        match body.Kind with
        | TypeRecord fields when fields.Length > 3 || (valid body.Span && hasCommentsIn body.Span.Start body.Span.End) ->
            let isPositional field =
                match field with
                | TypeField(n, false, false, Some _, None) -> n.Length > 0 && Seq.forall Char.IsDigit n
                | _ -> false
            // A field's own text is not kept; its type's span stands in for where it is.
            let spanOf field =
                match field with
                | TypeField(_, _, _, Some t, defaultValue) ->
                    let finish = match defaultValue with Some value when valid value.Span -> value.Span.End | _ -> t.Span.End
                    t.Span.Start, finish
                | TypeMember(_, Some t) -> t.Span.Start, t.Span.End
                | _ -> -1, -1
            let children =
                fields
                |> List.map (fun field ->
                    let start, finish = spanOf field
                    start, finish, fun () -> cat [ recordTypeField field; (if isPositional field then text "," else Doc.empty) ])
            cat [ text "("; nest (cat [ hard; sequence false body.Span.Start body.Span.End children ]); hard; text ")" ]
        | _ -> typeExpr body

    /// An import, its names sorted (see `ImportOrder`) within each run of them that no comment on a line of its own
    /// interrupts. A comment at the end of a name's line moves with the name; a comment on a line of its own stays
    /// where it is, ahead of the names after it.
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
            let items = Array.ofList items
            // Where a comment at the end of the line `items[k]` ends on would have to start before.
            let lineFinish k =
                let afterLine = lineEnd (max items[k].Span.Start (items[k].Span.End - 1)) + 1
                if k + 1 < items.Length then min items[k + 1].Span.Start afterLine else afterLine
            let runs =
                [ 0 .. items.Length - 1 ]
                |> List.fold
                    (fun runs k ->
                        match runs with
                        | run :: rest when not (hasCommentsIn (lineFinish (k - 1)) items[k].Span.Start) -> (k :: run) :: rest
                        | _ -> [ k ] :: runs)
                    []
                |> List.rev
                |> List.map List.rev
            let entries = ResizeArray<StackEntry>()
            let placeBetween a b =
                for i in unplacedIn a b do
                    placed[i] <- true
                    entries.Add(StackComment(fst comments[i], true))
            let mutable previous = start
            for run in runs do
                placeBetween previous items[List.head run].Span.Start
                for k in run |> List.sortWith (fun a b -> ImportOrder.compare items[a] items[b]) do
                    placeBetween items[k].Span.Start items[k].Span.End
                    match unplacedIn items[k].Span.End (lineFinish k) with
                    | i :: _ ->
                        placed[i] <- true
                        entries.Add(StackItem(cat [ item items[k]; Doc.lineSuffix (fst comments[i]) ], false))
                    | [] -> entries.Add(StackItem(item items[k], false))
                previous <- lineFinish (List.last run)
            placeBetween previous finish
            cat [ text "import ("; nest (cat [ hard; Stack(false, List.ofSeq entries) ]); hard; text ")" ]

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
        let blankLineBetween a b = blankRuns |> List.exists (fun span -> a <= span.Start && span.Start < b)
        destructuringRuns (items |> List.map (fun i -> i.Span, defOf i))
        |> List.map (fun (index, count, isDestructuring) ->
            let first = all[index]
            if isDestructuring then
                first.Span.Start, first.Span.End, first, fun () -> destructuringDef [ for k in index .. index + count - 1 -> (defOf all[k]).Value ] first.Span
            else
                first.Span.Start, first.Span.End, first, fun () -> item first)
        // Imports in a row, with no blank line between them, go in one bracketed import:
        // `import nanyx/math` and `import nanyx/text` become `import (` / `nanyx/math` / `nanyx/text` / `)`.
        |> List.fold
            (fun merged (childStart, childEnd, first: TopLevelItem, build) ->
                match first.Kind, merged with
                | Import imports, (runStart, runEnd, runFirst, _, Some run) :: rest when not (blankLineBetween runEnd childStart) ->
                    let run = run @ imports
                    (runStart, childEnd, runFirst, (fun () -> importDoc runStart childEnd run), Some run) :: rest
                | Import imports, _ -> (childStart, childEnd, first, build, Some imports) :: merged
                | _ -> (childStart, childEnd, first, build, None) :: merged)
            []
        |> List.rev
        |> List.map (fun (childStart, childEnd, first, build, _) -> childStart, childEnd, first, build)
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
        | Attribute(n, None) -> text $"@{n}"
        | Attribute(n, Some (Kind UnitExpr)) -> text $"@{n}()"
        | Attribute(n, Some argument) -> cat [ text ("@" + n); parenFields argument (i.Span.Start + 1 + n.Length) i.Span.End ]

    let code = Doc.render options.LineWidth (topLevel true headerEnd source.Length parsed.Items)
    if headerEnd = 0 then
        code
    else
        // A blank line between the header and the code is kept, and none is added.
        let blankLineAfter = blankRuns |> List.exists (fun span -> span.Start = headerEnd + 1)
        let separator = if code.Trim() = "" then "" elif blankLineAfter then "\n\n" else "\n"
        source.Substring(0, headerEnd) + separator + code.TrimStart('\n') + (if code.Trim() = "" then "\n" else "")

/// Formats the source of the file at `filePath`, or says why it does not parse.
let formatSource (options: Options) (filePath: string) (input: string) : Result<string, ParseError> =
    parseModuleWithTrivia filePath input |> Result.map (format options)
