/// Printing a parsed module back as Nanyx source, with its comments.
module Nanyx.Formatter.Formatter

open System
open System.Globalization
open System.Text.RegularExpressions
open Parser.Program
open Nanyx.Formatter.SourceText
open Nanyx.Formatter.Position
open Nanyx.Formatter.CommentPlacement
open Nanyx.Formatter.TypePrinter
open Nanyx.Formatter.PatternPrinter
open Nanyx.Formatter.ImportPrinter
open Nanyx.Formatter.CallPrinter
open Nanyx.Formatter.LambdaPrinter

type Options =
    { /// The width lines are laid out to fit.
      LineWidth: int
      /// The spaces each level of indentation adds.
      IndentSize: int }

module Options =
    let defaults = { LineWidth = 100; IndentSize = 2 }

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

    let sourceText = SourceText(source)

    // The `#!` line and directives at the top are kept as they are, with the comments among them.
    let headerEnd = Directives.headerEnd source

    let placement = CommentPlacement(parsed, options.IndentSize, sourceText, headerEnd)
    let blankRuns = placement.BlankRuns
    let unplacedIn start finish = placement.UnplacedIn(start, finish)
    let hasCommentsIn start finish = placement.HasCommentsIn(start, finish)
    let sequence = placement.Sequence
    let sequenceOf = placement.SequenceOf
    let imports = ImportPrinter(options.IndentSize, sourceText, placement)

    /// A new id for a `NamedGroup`.
    let nextGroupId =
        let last = ref 0
        fun () ->
            last.Value <- last.Value + 1
            last.Value

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
            | span, Some(_, temp, None, _) when DestructuringSyntax.isTemp temp ->
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
        | BinaryOp(op, _, _, _) -> precedence op < position.Prec
        | RangeSyntax.Call _ -> rangePrecedence < position.Prec || position.Inline
        | FunctionCall(name, _, [ _ ]) when name = NotSyntax.builtIn -> position.Prec >= prefixPrecedence
        | FunctionCall(name, _, [ _ ]) when name = NegateSyntax.builtIn -> position.Prec >= prefixPrecedence
        // A pipe binds tighter than every binary operator, but not than a prefix one: `-(x \abs)` keeps its brackets, as `-x \abs` is `(-x) \abs`.
        | Pipe _ -> position.Prec >= prefixPrecedence
        // `(r with (x = 1)).x`: nothing follows a `with` but an operator, though its updates are bracketed.
        | WithSyntax.Call(_, updates) when sourceText.StartsWith updates.Span "(" -> position.Prec >= prefixPrecedence
        | _ when isOpenEnded e -> not position.Tail || position.Prec >= prefixPrecedence
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
        | WithSyntax.Call _ -> true
        | ForSyntax.Call _
        | WhileSyntax.Call _
        | AssignSyntax.Call _ -> true
        | _ -> false

    and exprBare (position: Position) (e: Expression) : Doc =
        match e.Kind with
        | LiteralExpr literal -> literalDoc e.Span literal
        | UnitExpr -> text "()"
        | IdentifierExpr n when n = implicitArgument -> Doc.empty
        | IdentifierExpr n when n = notImplemented -> text n
        | IdentifierExpr n -> text (Names.written n)
        | BinaryOp(op, _, l, r) ->
            match spreadSegments e with
            | Some segments -> listDoc e.Span segments
            | None -> binary position op l r
        | FunctionCall(name, _, [ operand ]) when name = NotSyntax.builtIn -> cat [ text "not "; expr operandPosition operand ]
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
            cat [ expr { position with Tail = false; Prec = rangePrecedence + 1 } a; text ".."; expr { position with Prec = rangePrecedence + 1 } b ]
        | RangeSyntax.Call(a, None) -> cat [ expr { position with Tail = false; Prec = rangePrecedence + 1 } a; text ".." ]
        // Updates written in parentheses, `record with (x = 1, y = 2)`, stay there, laid out as a record's fields are.
        | WithSyntax.Call(record, updates) when sourceText.StartsWith updates.Span "(" ->
            cat [ expr operandPosition record; text " with "; exprBare full updates ]
        | WithSyntax.Call(record, updates) when (match updates.Kind with RecordExpr fields -> fields |> List.forall (function NamedField _ -> true | _ -> false) | _ -> false) ->
            let fields = match updates.Kind with RecordExpr fields -> fields | _ -> []
            let count = fields.Length
            let update index field =
                match field with
                | NamedField(n, value) -> cat [ text (Names.written n); text " = "; expr { full with Tail = (index = count - 1) } value ]
                | PositionalField value -> expr full value
            cat [ expr operandPosition record; text " with "; commaJoin (List.mapi update fields) ]
        | ForSyntax.Call(iterables, loop, parameters, _) when parameters = List.init iterables.Length (fun index -> (ForSyntax.element (index + 1), None)) ->
            forDoc iterables loop
        | WhileSyntax.Call(condition, body) -> cat [ text "while "; expr { full with Tail = false } condition; armBody true body ]
        | MapSyntax.Call entries -> mapDoc e.Span entries
        | FunctionCall(n, _, [ Kind (RecordExpr fields) ]) when n = SpreadSyntax.builtIn -> recordFieldsDoc true e.Span.Start e.Span.End fields
        | AssignSyntax.Call(target, value) -> cat [ expr operandPosition target; text " := "; expr { full with Tail = position.Tail } value ]
        | CellPassingSyntax.Argument cell -> cat [ text "mut "; expr full cell ]
        | FunctionCall(n, nameSpan, args) ->
            let written = Names.written n
            cat [ text written; callArguments e (nameEnd nameSpan (e.Span.Start + written.Length)) args ]
        | Lambda(parameters, body, _) -> lambdaDoc e parameters body false
        | Pipe _ -> pipeChain position e
        | CallResultSyntax.Call(callee, ({ Kind = FunctionCall(_, _, args) } as call)) ->
            cat [ exprBare full callee; callArguments call callee.Span.End args ]
        // A block where a def's value, an arm's body or a branch would not take one is a `do` block.
        | Block _ -> cat [ text "do"; nest (cat [ hard; block e ]) ]
        | Match([ scrutinee ], arms) when sourceText.IsPipedMatch e scrutinee -> pipeChain position e
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
            let head = cat [ text "use "; useValue { full with Tail = false } value; text " in" ]
            if isWrittenBlock body then cat [ head; nest (cat [ hard; block body ]) ]
            else cat [ head; text " "; expr full body ]
        | WorkflowBindExpr(n, value) ->
            let keyword = if n = WorkflowSyntax.tryKeyword && not (sourceText.StartsWith e.Span (n + "!")) then n + " " else n + "! "
            cat [ text keyword; expr full value ]
        | WorkflowReturnExpr value -> cat [ text (WorkflowSyntax.returnKeyword + " "); expr full value ]
        | InterpolatedString parts ->
            // Each part of text starts after the opening quote or the hole before it.
            let part start p =
                match p with
                | StringText s -> sourceText.WrittenStringText start s, None
                | StringExpr inner -> cat [ text "{"; expr full inner; text "}" ], sourceText.AfterHole inner
            let opening = if sourceText.StartsWith e.Span "\"" then Some (e.Span.Start + 1) else None
            cat [ text "\""; cat (List.mapFold part opening parts |> fst); text "\"" ]

    and literalDoc (span: SourceSpan) (literal: Literal) : Doc =
        if sourceText.Valid span && span.End > span.Start then
            Doc.textOrVerbatim (source.Substring(span.Start, span.End - span.Start))
        else
            match literal with
            | StringLit s -> Doc.textOrVerbatim ("\"" + StringEscapes.escape s + "\"")
            | IntLit i -> text (string i)
            | FloatLit f -> text (f.ToString("R", CultureInfo.InvariantCulture))
            | BoolLit b -> text (if b then "true" else "false")


    /// A chain of operators of one precedence, `a ++ b ++ c`, as its operands and the operators between them. In a
    /// position that may cross lines, as many operands go on a line as fit, each line ending in an operator (the
    /// parser takes a line break after an operator, not before one), and the lines after the first indented once.
    and binary (position: Position) (op: string) (l: Expression) (r: Expression) : Doc =
        let p = precedence op
        let grouping = associativity op
        let isRight = grouping = RightAssociative
        // A node of the chain: same precedence and grouping, which groups at all, needing no brackets where it is, and
        // not a list with spreads.
        let continues (x: Expression) =
            match x.Kind with
            | BinaryOp(op', _, _, _) ->
                grouping <> NonAssociative
                && precedence op' = p
                && associativity op' = grouping
                && (spreadSegments x).IsNone
            | _ -> false
        let rec flattenLeft (x: Expression) =
            match x.Kind with
            | BinaryOp(op', _, a, b) when continues x ->
                let operands, operators = flattenLeft a
                operands @ [ b ], operators @ [ op' ]
            | _ -> [ x ], []
        let rec flattenRight (x: Expression) =
            match x.Kind with
            | BinaryOp(op', _, a, b) when continues x ->
                let operands, operators = flattenRight b
                a :: operands, op' :: operators
            | _ -> [ x ], []
        let operands, operators =
            if isRight then
                let rest, operators = flattenRight r
                l :: rest, op :: operators
            else
                let first, operators = flattenLeft l
                first @ [ r ], operators @ [ op ]
        let count = operands.Length
        let operandDoc index operand =
            let isLast = index = count - 1
            // An operand of the same precedence that groups another way is bracketed: `(f >> g) << h`.
            let groupsAnotherWay =
                match operand.Kind with
                | BinaryOp(op', _, _, _) -> precedence op' = p && associativity op' <> grouping
                | _ -> false
            let prec =
                match grouping with
                | _ when groupsAnotherWay -> p + 1
                | RightAssociative -> if isLast then p else p + 1
                | LeftAssociative -> if index = 0 then p else p + 1
                | NonAssociative -> p + 1
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
        | BinaryOp("++", _, l, r) when sourceText.Valid e.Span && source[e.Span.Start] = '[' && (l.Span = e.Span || (sourceText.Valid l.Span && l.Span.Start > e.Span.Start)) ->
            let segment (x: Expression) =
                match x.Kind with
                | ListExpr items when x.Span = e.Span -> Choice1Of2 items
                | _ -> Choice2Of2 x
            let rec flatten (x: Expression) =
                match x.Kind with
                | BinaryOp("++", _, a, b) when x.Span = e.Span -> segment a :: flatten b
                | _ -> [ segment x ]
            Some(segment l :: flatten r)
        | _ -> None

    /// What is between brackets in the module, with the expressions in it printed as `expr` prints them.
    and calls () = CallPrinter(options.IndentSize, sourceText, placement, fun isLast e -> expr { full with Tail = isLast } e)

    and listDoc (span: SourceSpan) (segments: Choice<Expression list, Expression> list) : Doc = (calls ()).ListDoc span segments

    and mapDoc (span: SourceSpan) (entries: (Expression * Expression) list) : Doc = (calls ()).MapDoc span entries

    and fieldsDoc (start: int) (finish: int) (fields: RecordField list) : Doc = (calls ()).FieldsDoc start finish fields

    and recordFieldsDoc (spread: bool) (start: int) (finish: int) (fields: RecordField list) : Doc =
        (calls ()).RecordFieldsDoc spread start finish fields

    and parenFields (argument: Expression) (start: int) (finish: int) : Doc = (calls ()).ParenFields argument start finish

    and nameEnd (nameSpan: SourceSpan option) (fallback: int) = (calls ()).NameEnd nameSpan fallback

    and callArguments (call: Expression) (afterName: int) (args: Expression list) : Doc = (calls ()).CallArguments call afterName args

    and pipeArguments (call: Expression) (afterName: int) (args: Expression list) : Doc = (calls ()).PipeArguments call afterName args

    /// Lambdas in the module, with their parts printed as the rest of the module's are.
    and lambdas () =
        LambdaPrinter(
            options.IndentSize,
            sourceText,
            placement,
            { Expression = expr
              NeedsParentheses = needsParentheses
              IsWrittenBlock = isWrittenBlock
              StatementsInSequence = statementsInSequence
              ArmsInSequence = armsInSequence
              Pattern = pattern
              TypeExpr = typeExpr }
        )

    /// A lambda. With `bodyOnOwnLine`, a body that is an expression goes on a line of its own after the `->`, as the
    /// one statement of a block (see `defDoc`).
    and lambdaDoc (e: Expression) (parameters: (Identifier * TypeExpr option) list) (body: Expression) (bodyOnOwnLine: bool) : Doc =
        (lambdas ()).LambdaDoc e parameters body bodyOnOwnLine

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
                | (first :: _) :: _ when sourceText.Valid first.Span -> first.Span.Start
                | _ -> body.Span.Start
            armStart, body.Span.End, fun () ->
                let patterns = alternatives |> List.map (fun ps -> commaJoin (List.map pattern ps)) |> Doc.join (text " | ")
                cat [ text "| "; patterns; armBody (index = count - 1) body ])
        |> sequence false start finish

    /// `-> body`: an indented block on the lines after it, or an expression on the same line.
    and armBody (isLast: bool) (body: Expression) : Doc =
        if isWrittenBlock body then cat [ text " ->"; nest (cat [ hard; block body ]) ]
        else cat [ text " -> "; expr { statementPosition with Tail = isLast } body ]

    /// Whether `e` is a block of statements as written, not one the parser made up for a call on a call's result (see
    /// `CallResultSyntax`), which is written as that call.
    and isWrittenBlock (e: Expression) =
        match e.Kind with
        | CallResultSyntax.Call _ -> false
        | Block _ -> true
        | _ -> false

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
                let at = match nameSpan with Some span when sourceText.Valid span -> span.Start | _ -> -1
                let doc = cat [ text "\\"; text n; pipeArguments e (nameEnd nameSpan value.Span.End) args ]
                collect value ((value, at, false, doc) :: steps)
            | Match([ value ], arms) ->
                match sourceText.PipedMatchAt e value with
                | Some at ->
                    let doc = cat [ text "\\match"; nest (cat [ hard; armsInSequence value.Span.End e.Span.End arms ]) ]
                    collect value ((value, at, true, doc) :: steps)
                | None -> e, steps
            | _ -> e, steps
        let root, steps = collect e []
        // A pipe binds tighter than every binary operator, so one piped is bracketed: `(a + b) \f`. A range without an end
        // has nothing after its `..` for a pipe to take, so it is not: `1.. \take(3)`.
        let rootDoc =
            match root.Kind with
            | BinaryOp _
            | RangeSyntax.Call(_, Some _) -> parens (expr full root)
            | _ -> expr { position with Tail = false; Prec = 0 } root
        let isMatch = steps |> List.map (fun (_, _, isMatch, _) -> isMatch)
        let afterMatch = false :: List.truncate (steps.Length - 1) isMatch
        // Whether the step at `at` starts a line of its own after the value it pipes.
        let startsLine (value: Expression) at = at >= 0 && sourceText.Valid value.Span && value.Span.End > 0 && sourceText.LineOf at > sourceText.LineOf (value.Span.End - 1)
        let writtenOnLines =
            List.zip steps afterMatch |> List.exists (fun ((value, at, _, _), afterMatch) -> not afterMatch && startsLine value at)
        let stepDocs = steps |> List.map (fun (_, _, _, doc) -> doc)
        // The comments between a step and the value it pipes: one at the end of the value's last line stays there, and
        // any other goes on a line of its own before the step.
        let commentsBefore (value: Expression) at =
            if at < 0 || not (sourceText.Valid value.Span) then
                Doc.empty
            else
                cat [ for i in unplacedIn value.Span.End at ->
                          let comment, span = placement.Place i
                          if sourceText.LineOf span.Start = sourceText.LineOf (max 0 (value.Span.End - 1)) then Doc.trailingComment comment
                          else cat [ hard; Doc.comment comment ] ]
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
            && (sourceText.StartsWith e.Span "if" || sourceText.StartsWith e.Span "|")
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
        if sourceText.StartsWith e.Span "if" && sourceText.NextChar (e.Span.Start + 2) = '|' then
            let rec chain arms (head, body, rest: Expression option) =
                match rest with
                | Some r when sourceText.StartsWith r.Span "|" && (ifArm r).IsSome -> chain ((head, body) :: arms) (ifArm r).Value
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
        | Lambda(parameters, body, _) ->
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
        | DefStatement(false, n, t, Kind (RecSyntax.Call inner)) -> defDoc false "rec" n t inner
        | DefStatement(false, n, t, Kind (MutSyntax.Call inner)) -> defDoc false "mut" n t inner
        | DefStatement(isExport, n, t, value) -> defDoc isExport "def" n t value
        | TypeDefStatement(n, modifiers, parameters, body) -> typeDefDoc n modifiers parameters body
        | ImportStatement items -> importDoc s.Span.Start s.Span.End items
        | UseStatement value -> cat [ text "use "; useValue full value ]
        | ExprStatement value -> expr statementPosition value

    /// What a `use` loads. An instance written out is printed as written, `Add(Point)(zero = ...)` (see
    /// `UseInstanceSyntax`).
    and useValue (position: Position) (value: Expression) : Doc =
        match value.Kind with
        | UseInstanceSyntax.Instance(context, members) -> cat [ typeExpr context; expr position members ]
        | _ -> expr position value

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
        | Block _ when isWrittenBlock value -> cat [ head; underHead (nest (cat [ hard; block value ])) ]
        | Lambda(parameters, body, _) when typeOpt.IsSome && not (isTrivial (snd ((lambdas ()).WrittenLambda value parameters body))) ->
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
            let patternText = sourceText.WrittenPattern span |> Option.defaultWith (fun () -> rebuiltPattern bindings)
            defDocNamed isExport "def" (text patternText) None value

    and pattern (p: Pattern) : Doc = PatternPrinter(source, sourceText, literalDoc, expr full).Pattern p

    /// The types in the module, with the expressions in them printed as `expr` prints them.
    and types () = TypePrinter(expr full, expr { full with Tail = false })

    and signature (t: TypeExpr) : Doc = (types ()).Signature t

    and typeExpr (t: TypeExpr) : Doc = (types ()).TypeExpr t

    and recordTypeField (field: TypeRecordField) : Doc = (types ()).RecordTypeField field

    and typeDefDoc (name: Identifier) (modifiers: TypeDefModifier list) (parameters: (Identifier * TypeExpr option) list) (body: TypeExpr) : Doc =
        let isPrivate = List.contains Private modifiers
        // `context Name = ...` and `context type Name = ...` both give `[Context]`; the modifiers' order is kept.
        // `scoped` only comes before `context`, and is written first.
        let scoped = if List.exists TypeDefModifier.isScoped modifiers then text "scoped " else Doc.empty
        let head =
            match modifiers |> List.filter (fun m -> m <> Private && not (TypeDefModifier.isScoped m)) with
            | [ Context ] -> cat [ scoped; text $"context {name}" ]
            | [ Context; Export ] -> cat [ scoped; text $"export context {name}" ]
            | others ->
                let modifier m =
                    match m with
                    | Export -> text "export "
                    | Context -> text "context "
                    | Private
                    | Scoped _ -> Doc.empty
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
        | TypeRecord fields when fields.Length > 3 || (sourceText.Valid body.Span && hasCommentsIn body.Span.Start body.Span.End) ->
            let isPositional field =
                match field with
                | TypeField(n, false, Some _, None) -> n.Length > 0 && Seq.forall Char.IsDigit n
                | _ -> false
            // A field's own text is not kept; its type's span stands in for where it is.
            let spanOf field =
                match field with
                | TypeField(_, _, Some t, defaultValue) ->
                    let finish = match defaultValue with Some value when sourceText.Valid value.Span -> value.Span.End | _ -> t.Span.End
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

    and importDoc (start: int) (finish: int) (items: ImportItem list) : Doc = imports.ImportDoc start finish items

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
        // Imports in a row, with no blank line between them, go in one bracketed import (see `ImportOrder.mergeRuns`).
        |> ImportOrder.mergeRuns
            (fun (_, _, first: TopLevelItem, _) -> match first.Kind with Import imports -> Some imports | _ -> None)
            (fun (_, previousEnd, _, _) (childStart, _, _, _) -> not (blankLineBetween previousEnd childStart))
            (fun run imports ->
                let runStart, _, runFirst, _ = List.head run
                let _, runEnd, _, _ = List.last run
                runStart, runEnd, runFirst, fun () -> importDoc runStart runEnd imports)
        // An attribute line belongs to the item after it, so nothing comes between them. `@partial` is written on the
        // line of the module declaration it marks.
        |> List.foldBack
            (fun (childStart, childEnd, first: TopLevelItem, build) children ->
                match children with
                | (_, nextEnd, next: TopLevelItem, build') :: rest when isAttribute first ->
                    let between = if TopLevelItem.isPartialDeclaration first next then text " " else hard
                    (childStart, nextEnd, next, fun () -> cat [ build (); between; build' () ]) :: rest
                | _ -> (childStart, childEnd, first, build) :: children)
            <| []
        |> List.map (fun (childStart, childEnd, first, build) -> childStart, childEnd, isImport first, build)
        |> sequenceOf blankLines start finish

    and item (i: TopLevelItem) : Doc =
        match i.Kind with
        | ModuleDecl n -> text $"module {n}"
        | ModuleBlock(n, items) -> cat [ text $"module {n} ="; nest (cat [ hard; topLevel true i.Span.Start i.Span.End items ]) ]
        | Def(ValueDef(isExport, n, t, value)) -> defDoc isExport "def" n t value
        | Def(TypeDef(n, modifiers, parameters, body)) -> typeDefDoc n modifiers parameters body
        | Import items -> importDoc i.Span.Start i.Span.End items
        | ReExport item -> cat [ text "export "; imports.ImportSource item.Source; text " "; imports.ImportSelection (Option.defaultValue [] item.Selection) ]
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
