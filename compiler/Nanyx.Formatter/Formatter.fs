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
open Nanyx.Formatter.ExpressionParts
open Nanyx.Formatter.LambdaPrinter
open Nanyx.Formatter.BranchPrinter
open Nanyx.Formatter.OperatorPrinter
open Nanyx.Formatter.Destructuring

type Options =
    { /// The width lines are laid out to fit.
      LineWidth: int
      /// The spaces each level of indentation adds.
      IndentSize: int }

module Options =
    let defaults = { LineWidth = 100; IndentSize = 2 }

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
        // A test binds as `is` does, and one with an `and` chain after it as `and` does.
        | IsSyntax.Test(_, _, None) -> isOperator.Precedence < position.Prec
        | IsSyntax.Test(_, _, Some _) -> precedence "and" < position.Prec
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
                | LiteralExpr(IntLit _ | FloatLit _ | UnitLit _) -> true
                | FunctionCall(inner, _, [ _ ]) -> inner = NegateSyntax.builtIn
                | _ -> false
            cat [ text "-"; (if joinsTheMinus then parens (exprBare full operand) else expr operandPosition operand) ]
        // `Array[1, 2]` and `MyMap["a" => 1]`, the brackets straight after the type's name.
        | CollectionSyntax.Construct(typeName, _, items) ->
            let bracketsDoc =
                match CollectionSyntax.listSegments items with
                | [ Choice1Of2 entries ] when not entries.IsEmpty && List.forall CollectionSyntax.isEntry entries ->
                    entries
                    |> List.map (function
                        | Kind (TupleExpr [ key; value ]) -> key, value
                        | entry -> entry, entry)
                    |> mapDoc items.Span
                | segments -> listDoc items.Span segments
            cat [ text typeName; bracketsDoc ]
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
                | NamedField(n, value) -> cat [ text (Names.written n); text " = "; expr (itemPosition (index = count - 1) value) value ]
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
        // Only a lambda's body is one, which the lambda prints (see `LambdaPrinter`).
        | ContextNames(_, body) -> exprBare position body
        | Pipe _ -> pipeChain position e
        | CallResultSyntax.Call(callee, ({ Kind = FunctionCall(_, _, args) } as call)) ->
            cat [ exprBare full callee; callArguments call callee.Span.End args ]
        // A block where a def's value, an arm's body or a branch would not take one is a `do` block.
        | Block _ -> cat [ text "do"; nest (cat [ hard; block e ]) ]
        | IsSyntax.Test(value, p, rest) -> isTestDoc position value p rest
        | Match([ scrutinee ], arms) when sourceText.IsPipedMatch e scrutinee -> pipeChain position e
        | Match(scrutinees, arms) ->
            match (branches ()).IfArm e with
            | Some _ -> ifDoc e
            | None -> (branches ()).MatchDoc e scrutinees arms
        | Except(value, arms) -> exceptDoc position e value arms
        | IfExpr _ -> ifDoc e
        | TupleExpr items -> fieldsDoc e.Span.Start e.Span.End (List.map PositionalField items)
        | RecordExpr fields -> fieldsDoc e.Span.Start e.Span.End fields
        | ListExpr items when sourceText.StartsWith e.Span "[|" -> tableDoc e.Span items
        | ListExpr items -> listDoc e.Span [ Choice1Of2 items ]
        | TagExpr(n, None) -> text ("#" + n)
        | TagExpr(n, Some (Kind UnitExpr)) -> text $"#{n}()"
        | TagExpr(n, Some payload) -> cat [ text ("#" + n); parenFields payload (e.Span.Start + 1 + n.Length) e.Span.End ]
        | MemberAccess(target, field, _) ->
            // A tuple's index, `.0`, is written as it is, not in backticks.
            let isIndex = field <> "" && String.forall Char.IsDigit field
            cat [ expr operandPosition target; text "."; text (if isIndex then field else Names.written field) ]
        | ContextMemberAccess(context, field) -> cat [ contextHead context; text "."; text (Names.written field) ]
        | TypeValue written -> cat [ text "type "; atomType written ]
        | ContextMemberCall(context, field, args) ->
            let count = args.Length
            let argument index arg = expr (itemPosition (index = count - 1) arg) arg
            cat [ contextHead context; text "."; text (Names.written field); text "("; commaJoin (List.mapi argument args); text ")" ]
        | UseIn(value, body) ->
            let head = cat [ text "use "; useValue { full with Tail = false } value; text " in" ]
            if isWrittenBlock body then cat [ head; nest (cat [ hard; block body ]) ]
            else cat [ head; text " "; expr full body ]
        | WorkflowBindExpr(n, value) ->
            let bare = n = WorkflowSyntax.yieldKeyword || (n = WorkflowSyntax.tryKeyword && not (sourceText.StartsWith e.Span (n + "!")))
            let keyword = if bare then n + " " else n + "! "
            cat [ text keyword; expr full value ]
        | WorkflowReturnExpr value -> cat [ text (WorkflowSyntax.returnKeyword + " "); expr full value ]
        | InterpolatedString parts ->
            // Each part of text starts after the opening quote or the hole before it.
            let part start p =
                match p with
                | StringText s -> sourceText.WrittenStringText start s, None
                // A spec is kept as it is written.
                | StringExpr({ Kind = FormatSyntax.Call(value, spec, _) } as inner) ->
                    cat [ text "{"; expr full value; text (":" + spec); text "}" ], sourceText.AfterHole inner
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
            | UnitLit(number, unitName) -> text (UnitSyntax.written number unitName)

    /// Operator and pipe chains in the module, with their operands printed as the rest of the module's are.
    and operators () : OperatorPrinter = OperatorPrinter(options.IndentSize, source, sourceText, placement, parts (), calls ())

    and binary (position: Position) (op: string) (l: Expression) (r: Expression) : Doc = (operators ()).Binary position op l r

    /// `value is pattern`, and the rest of the `and` chain after it, which the test's names are in scope in, if any.
    /// The chain breaks as a chain of `and`s does (see `OperatorPrinter.Binary`).
    and isTestDoc (position: Position) (value: Expression) (p: Pattern) (rest: Expression option) : Doc =
        let test = cat [ expr { position with Tail = false; Prec = isOperator.Precedence + 1 } value; text " is "; pattern p ]
        match rest with
        | None -> test
        | Some rest ->
            let restDoc = expr { position with Prec = precedence "and" } rest
            if position.Inline then cat [ test; text " and "; restDoc ]
            else nest (Doc.Fill [ cat [ test; text " and" ]; Doc.line; restDoc ])

    and spreadSegments (e: Expression) : Choice<Expression list, Expression> list option = (operators ()).SpreadSegments e

    /// What is between brackets in the module, with the expressions in it printed as `expr` prints them.
    and calls () = CallPrinter(options.IndentSize, sourceText, placement, fun isLast e -> expr { full with Tail = isLast } e)

    and listDoc (span: SourceSpan) (segments: Choice<Expression list, Expression> list) : Doc = (calls ()).ListDoc span segments

    and tableDoc (span: SourceSpan) (records: Expression list) : Doc = (calls ()).TableDoc span records

    and mapDoc (span: SourceSpan) (entries: (Expression * Expression) list) : Doc = (calls ()).MapDoc span entries

    and fieldsDoc (start: int) (finish: int) (fields: RecordField list) : Doc = (calls ()).FieldsDoc start finish fields

    and recordFieldsDoc (spread: bool) (start: int) (finish: int) (fields: RecordField list) : Doc =
        (calls ()).RecordFieldsDoc spread start finish fields

    and parenFields (argument: Expression) (start: int) (finish: int) : Doc = (calls ()).ParenFields argument start finish

    and nameEnd (nameSpan: SourceSpan option) (fallback: int) = (calls ()).NameEnd nameSpan fallback

    and callArguments (call: Expression) (afterName: int) (args: Expression list) : Doc = (calls ()).CallArguments call afterName args

    and pipeArguments (call: Expression) (afterName: int) (args: Expression list) : Doc = (calls ()).PipeArguments call afterName args

    /// What the printers taken out of this one call back into it for.
    and parts () : ExpressionParts =
        { Expression = expr
          NeedsParentheses = needsParentheses
          IsWrittenBlock = isWrittenBlock
          Block = block
          StatementsInSequence = statementsInSequence
          SemicolonLine = semicolonLine
          ArmsInSequence = armsInSequence
          Pattern = pattern
          TypeExpr = typeExpr }

    /// Lambdas in the module, with their parts printed as the rest of the module's are.
    and lambdas () = LambdaPrinter(options.IndentSize, sourceText, placement, parts ())

    /// A lambda. With `bodyOnOwnLine`, a body that is an expression goes on a line of its own after the `->`, as the
    /// one statement of a block (see `defDoc`).
    and lambdaDoc (e: Expression) (parameters: (Identifier * TypeExpr option) list) (body: Expression) (bodyOnOwnLine: bool) : Doc =
        (lambdas ()).LambdaDoc e parameters body bodyOnOwnLine

    /// Branching constructs in the module, with their parts printed as the rest of the module's are.
    and branches () : BranchPrinter = BranchPrinter(options.IndentSize, sourceText, placement, parts ())

    and armsInSequence (start: int) (finish: int) (arms: MatchArm list) : Doc = (branches ()).ArmsInSequence start finish arms

    and armBody (isLast: bool) (body: Expression) : Doc = (branches ()).ArmBody isLast body

    /// Whether `e` is a block of statements as written, not one the parser made up for a call on a call's result (see
    /// `CallResultSyntax`), which is written as that call.
    and isWrittenBlock (e: Expression) =
        match e.Kind with
        | CallResultSyntax.Call _ -> false
        | Block _ -> true
        | _ -> false

    and pipeChain (position: Position) (e: Expression) : Doc = (operators ()).PipeChain position e

    and exceptDoc (position: Position) (e: Expression) (value: Expression) (arms: MatchArm list) : Doc =
        (branches ()).ExceptDoc position e value arms

    and ifDoc (e: Expression) : Doc = (branches ()).IfDoc e

    and forDoc (iterables: Expression list) (loop: Expression) : Doc = (branches ()).ForDoc iterables loop

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
        statementLines statements
        |> List.map (function
            | [ child ] -> child
            | (first, _, _) :: _ as line ->
                let _, last, _ = List.last line
                first, last, fun () -> Doc.group (semicolonJoined line)
            | [] -> invalidOp "A line of statements has at least one")
        |> sequence blankLines start finish

    /// The statements of a block as the children of a sequence, each statement or destructuring def given as its start
    /// and end offsets and a function building it, in the lines they were written on: those written on one line,
    /// separated by `;`, make one line, and every other statement a line of its own.
    and statementLines (statements: Statement list) : (int * int * (unit -> Doc)) list list =
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
        |> List.fold
            (fun lines ((start, _, _) as child) ->
                match lines with
                | ((_, previousEnd, _) :: _ as line) :: rest when sourceText.SemicolonBetween previousEnd start -> (child :: line) :: rest
                | _ -> [ child ] :: lines)
            []
        |> List.map List.rev
        |> List.rev

    /// Statements separated by `;` and a space, or put on lines of their own when the group around them is broken.
    and semicolonJoined (line: (int * int * (unit -> Doc)) list) : Doc =
        Doc.join (cat [ IfBreak(Doc.empty, text ";"); Doc.line ]) (line |> List.map (fun (_, _, build) -> build ()))

    and semicolonLine (e: Expression) : (unit -> Doc) option =
        match e.Kind with
        | Block statements when isWrittenBlock e ->
            match statementLines statements with
            | [ _ :: _ :: _ as line ] -> Some(fun () -> semicolonJoined line)
            | _ -> None
        | _ -> None

    and statement (s: Statement) : Doc =
        match s.Kind with
        | DefStatement(false, n, t, Kind (RecSyntax.Call inner)) -> defDoc false "rec" n t inner
        | DefStatement(false, n, t, Kind (MutSyntax.Call inner)) -> defDoc false "mut" n t inner
        | DefStatement(isExport, n, Some t, value) when (PatternSyntax.shapeOf n).IsSome -> patternDoc isExport t value
        | DefStatement(isExport, n, t, value) -> defDoc isExport "def" n t value
        | TypeDefStatement(n, modifiers, parameters, body) -> typeDefDoc n modifiers parameters body
        | ImportStatement items -> importDoc s.Span.Start s.Span.End items
        | UseStatement value -> cat [ text "use "; useValue full value ]
        | ExprStatement value -> expr statementPosition value
        | AttributeStatement(n, argument) -> attributeDoc n argument s.Span

    /// What a `use` loads. An instance written out is printed as written, `Add(Point)(zero = ...)` (see
    /// `UseInstanceSyntax`).
    and useValue (position: Position) (value: Expression) : Doc =
        match value.Kind with
        | UseInstanceSyntax.Instance(context, members) -> cat [ typeExpr context; expr position members ]
        | _ -> expr position value

    and defDoc (isExport: bool) (keyword: string) (name: string) (typeOpt: TypeExpr option) (value: Expression) : Doc =
        defDocNamed isExport keyword (text (Names.written name)) typeOpt value

    /// A def with no value, a foreign function's declaration (see `ExternSyntax`): its name and its type, which goes on
    /// the next line when it does not fit on the def's.
    and declarationDoc (isExport: bool) (name: string) (typeOpt: TypeExpr option) : Doc =
        let name = cat [ (if isExport then text "export " else Doc.empty); text "def "; text (Names.written name) ]
        match typeOpt with
        | Some t -> Doc.group (cat [ name; nest (cat [ Doc.softLine; text ": "; brokenSignature t ]) ])
        | None -> name

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
        //
        // A comment between the signature and the `=` keeps the `=` on a line of its own after it:
        //
        //   def scale: int, int -> int  -- times k, plus x
        //     = { x, k -> x * k + x }
        let signatureGroup = nextGroupId ()
        let commentsBeforeEquals (t: TypeExpr) =
            if sourceText.Valid t.Span && sourceText.Valid value.Span then
                placement.UnplacedIn(t.Span.End, value.Span.Start)
                |> List.map (fun i ->
                    let comment, span = placement.Place i
                    if sourceText.LineOf span.Start = sourceText.LineOf (t.Span.End - 1) then Doc.trailingComment comment
                    else cat [ hard; Doc.comment comment ])
            else
                []
        let head =
            match typeOpt with
            | Some t ->
                match commentsBeforeEquals t with
                | [] -> NamedGroup(signatureGroup, cat [ name; nest (cat [ Doc.softLine; text ": "; brokenSignature t; Doc.line; text "=" ]) ])
                | comments ->
                    let signatureLine = cat [ name; Doc.group (nest (cat [ Doc.softLine; text ": "; brokenSignature t ])) ]
                    NamedGroup(signatureGroup, cat [ signatureLine; nest (cat [ cat comments; hard; text "=" ]) ])
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
        | _ -> cat [ head; underHead (cat [ text " "; defValueDoc value ]) ]

    /// A custom pattern's declaration (see `PatternSyntax`): `pattern`, its type, and its body after the `=`.
    and patternDoc (isExport: bool) (signatureType: TypeExpr) (value: Expression) : Doc =
        let head = cat [ (if isExport then text "export " else Doc.empty); text $"{PatternSyntax.keyword} "; signature signatureType; text " =" ]
        match value.Kind with
        | Block _ when isWrittenBlock value -> cat [ head; nest (cat [ hard; block value ]) ]
        | _ -> cat [ head; text " "; expr full value ]

    and destructuringDef (group: Def list) (span: SourceSpan) : Doc =
        let _, _, _, value = List.head group
        let bindings = List.tail group
        let isExport = bindings |> List.exists (fun (isExport, _, _, _) -> isExport)
        match value.Kind with
        // A pattern that may fail to match (`def Money(pence) = m`) is bound through a one-arm match on the
        // value; the def is printed from that arm's pattern.
        | Match([ scrutinee ], [ [ p ], _ ]) -> defDocNamed isExport "def" (pattern p) None scrutinee
        | _ ->
            let patternText = sourceText.WrittenPattern span |> Option.defaultWith (fun () -> rebuiltPattern bindings)
            defDocNamed isExport "def" (text patternText) None value

    /// A def's value that is a tuple, which needs no brackets after the `=`, `def point = 3, 4`: none where it fits on
    /// the line and none of its items is an `if`, a `match` or a block, which would take in the items after it, else
    /// in brackets, as a tuple is broken over lines anywhere else (#2653).
    and defValueDoc (value: Expression) : Doc =
        match value.Kind with
        | TupleExpr items when items |> List.forall (fun item -> match item.Kind with IfExpr _ | Match _ | Except _ | Block _ -> false | _ -> true) ->
            let unbracketed = Doc.join (text ", ") (items |> List.map (expr { full with Tail = false }))
            Doc.group (IfBreak(expr full value, unbracketed))
        | _ -> expr full value

    and pattern (p: Pattern) : Doc = PatternPrinter(source, sourceText, literalDoc, expr full).Pattern p

    /// The types in the module, with the expressions in them printed as `expr` prints them.
    and types () = TypePrinter(expr full, expr { full with Tail = false }, attributeDoc)

    /// An attribute written at `span`, on a line of its own or before a field: `@entry`, `@customKeywordType(#bind)`.
    and attributeDoc (name: Identifier) (argument: Expression option) (span: SourceSpan) : Doc =
        match argument with
        | None -> text $"@{name}"
        | Some (Kind UnitExpr) -> text $"@{name}()"
        | Some (LinterRuleSyntax.Rules rules) ->
            let written = rules |> List.map (fun (linter, rule, _) -> $"{linter}{LinterRuleSyntax.separator}{rule}") |> String.concat ", "
            text $"@{name}({written})"
        | Some argument -> cat [ text ("@" + name); parenFields argument (span.Start + 1 + name.Length) span.End ]

    and signature (t: TypeExpr) : Doc = (types ()).Signature t
    and brokenSignature (t: TypeExpr) : Doc = (types ()).BrokenSignature t

    and typeExpr (t: TypeExpr) : Doc = (types ()).TypeExpr t

    and atomType (t: TypeExpr) : Doc = (types ()).AtomType t

    and recordTypeField (field: TypeRecordField) : Doc = (types ()).RecordTypeField field

    and typeDefDoc (name: Identifier) (modifiers: TypeDefModifier list) (parameters: (Identifier * TypeExpr option) list) (body: TypeExpr) : Doc =
        let kind =
            if List.contains Private modifiers then [ text "private" ]
            elif List.contains Protected modifiers then [ text "protected" ]
            else []
        // `context Name = ...` and `context type Name = ...` both give `[Context]`; the modifiers' order is kept.
        // `scoped` only comes before `context`, and after `export`: `export scoped context`.
        let scoped = if List.exists TypeDefModifier.isScoped modifiers then text "scoped " else Doc.empty
        // `[Tx] protected (...)`: the contexts whose scope a value belongs to come before the kind.
        let inScopeOf =
            match TypeDefModifier.scopeContexts modifiers with
            | [] -> []
            | contexts -> [ text $"""[{contexts |> List.map fst |> String.concat ", "}]""" ]
        let isHeadModifier m =
            match m with
            | Private
            | Protected
            | Scoped _
            | InScopeOf _
            | KindAt _
            | AssociatedTypes _ -> false
            | Export
            | Context -> true
        let head =
            match modifiers |> List.filter isHeadModifier with
            | [ Context ] -> cat [ scoped; text $"context {name}" ]
            | [ Context; Export ] -> cat [ text "export "; scoped; text $"context {name}" ]
            | others ->
                let modifier m =
                    match m with
                    | Export -> text "export "
                    | Context -> text "context "
                    | Private
                    | Protected
                    | Scoped _
                    | InScopeOf _
                    | KindAt _
                    | AssociatedTypes _ -> Doc.empty
                cat [ cat (List.map modifier others); text $"type {name}" ]
        let parameter (p, typeOpt) =
            match typeOpt with
            | Some t -> cat [ text p; text " :: "; typeExpr t ]
            | None -> text p
        let parametersDoc =
            match parameters with
            | [] -> Doc.empty
            | _ -> cat [ text "("; commaJoin (List.map parameter parameters); text ")" ]
        cat [ head; parametersDoc; text " ="; typeBody (inScopeOf @ kind) body ]

    /// The type a type or context definition stands for, after its `=` and the modifiers that follow it.
    ///
    /// A union written over more than one line, or too long for one, has one case per line, each starting with `|`:
    ///
    ///   type Shape =
    ///     | #circle(radius: float)
    ///     | #rectangle(width: float, height: float)
    ///
    /// A union written on one line that fits stays on it. The comments among a union's cases stay where they were written
    /// (see `unionPerLine`). A record of more than three fields, or with a comment among
    /// them, or too long for one line, has one field per line, named fields separated by newlines and positional ones
    /// followed by commas (the parser takes nothing else after them):
    ///
    ///   type Product = (
    ///     sku: string
    ///     name: string
    ///     price: int
    ///     stock: int
    ///   )
    ///
    /// A record with an attribute among its fields is laid out so too, each attribute on a line of its own above its
    /// field.
    ///
    /// Any other type too long for the line goes on the next one, indented.
    and typeBody (modifiers: Doc list) (body: TypeExpr) : Doc =
        let hasAttribute fields = fields |> List.exists (function TypeFieldAttribute _ -> true | _ -> false)
        let afterEquals = cat [ for m in modifiers -> cat [ text " "; m ] ]
        match body.Kind with
        | TypeRecord fields when fields.Length > 3 || hasAttribute fields || (sourceText.Valid body.Span && hasCommentsIn body.Span.Start body.Span.End) ->
            cat [ afterEquals; text " "; recordPerLine body fields ]
        // Laying the record out one field per line places the comments up to the end of its line, so it is only offered
        // where the line has none.
        | TypeRecord fields when sourceText.Valid body.Span && not (hasCommentsIn body.Span.Start (sourceText.LineEnd(max body.Span.Start (body.Span.End - 1)) + 1)) ->
            cat [ afterEquals; text " "; Doc.group (Doc.IfBreak(recordPerLine body fields, typeExpr body)) ]
        | TypeUnion cases ->
            let writtenOnLines = sourceText.Valid body.Span && sourceText.LineOf body.Span.Start <> sourceText.LineOf(max body.Span.Start (body.Span.End - 1))
            let headEnd = if writtenOnLines then unionHeadEnd body.Span.Start else body.Span.Start
            if writtenOnLines && hasCommentsIn headEnd (sourceText.LineEnd(body.Span.End - 1) + 1) then
                cat [ afterEquals; unionPerLine headEnd body cases ]
            else
            let separator = if writtenOnLines then hard else Doc.line
            let caseLines =
                cases
                |> List.mapi (fun index case ->
                    let bar = if index = 0 then Doc.IfBreak(text "| ", Doc.empty) else text "| "
                    cat [ separator; bar; (types ()).UnionCase case ])
            cat [ afterEquals; Doc.group (nest (cat caseLines)) ]
        | _ ->
            let written = typeExpr body
            if Doc.hasHardLine written then cat [ afterEquals; text " "; written ]
            else Doc.group (cat [ afterEquals; nest (cat [ Doc.line; written ]) ])

    /// Where the text before the union starting at `bodyStart` ends: past the `=` or modifier before it, and not past
    /// the first case's `|` or the comments before it.
    and unionHeadEnd (bodyStart: int) : int =
        let commentEndingAt offset =
            placement.UnplacedIn(0, bodyStart)
            |> List.tryPick (fun i ->
                let _, span = placement.Comment i
                if span.Start <= offset && offset < span.End then Some span.Start else None)
        let rec back offset =
            if offset <= 0 then 0
            else
                match commentEndingAt (offset - 1) with
                | Some start -> back start
                | None ->
                    let c = sourceText.CharAt(offset - 1)
                    if c = '|' || Char.IsWhiteSpace c then back (offset - 1) else offset
        back bodyStart

    /// A union with a comment among its cases, one case per line: the comment after the `=` stays at the end of its
    /// line, a comment after a case at the end of that case's line, and any other on a line of its own before the case
    /// it comes before.
    and unionPerLine (headEnd: int) (body: TypeExpr) (cases: TypeExpr list) : Doc =
        let headComments = placement.PlaceAfter headEnd body.Span.Start
        let children = [ for case in cases -> case.Span.Start, case.Span.End, fun () -> cat [ text "| "; (types ()).UnionCase case ] ]
        nest (cat [ cat headComments; hard; sequence false body.Span.Start body.Span.End children ])

    /// A record type with one field per line (see `typeBody`).
    and recordPerLine (body: TypeExpr) (fields: TypeRecordField list) : Doc =
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
            | _ -> -1, -1
        let children =
            TypeRecordField.withAttributes fields
            |> List.map (fun (attributes, field) ->
                let start, finish = spanOf field
                let start = match attributes with (_, _, span) :: _ when sourceText.Valid span -> span.Start | _ -> start
                let attributeLines = [ for name, argument, span in attributes -> cat [ attributeDoc name argument span; hard ] ]
                start, finish, fun () -> cat (attributeLines @ [ recordTypeField field; (if isPositional field then text "," else Doc.empty) ]))
        cat [ text "("; nest (cat [ hard; sequence false body.Span.Start body.Span.End children ]); hard; text ")" ]

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
        // line of the module declaration it marks, and `@baseUnit` and `@storageUnit` on the line of the unit.
        |> List.foldBack
            (fun (childStart, childEnd, first: TopLevelItem, build) children ->
                match children with
                | (_, nextEnd, next: TopLevelItem, build') :: rest when isAttribute first ->
                    let between =
                        if TopLevelItem.isPartialDeclaration first next || UnitSyntax.isUnitAttributeDeclaration first next then text " "
                        else hard
                    (childStart, nextEnd, next, fun () -> cat [ build (); between; build' () ]) :: rest
                | _ -> (childStart, childEnd, first, build) :: children)
            <| []
        |> List.map (fun (childStart, childEnd, first, build) -> childStart, childEnd, isImport first, build)
        |> sequenceOf blankLines start finish

    and item (i: TopLevelItem) : Doc =
        match i.Kind with
        | ModuleDecl(isExport, n) -> text $"""{if isExport then "export " else ""}module {n}"""
        | ModuleBlock(n, items) -> cat [ text $"module {n} ="; nest (cat [ hard; topLevel true i.Span.Start i.Span.End items ]) ]
        | Def(PatternSyntax.Declaration _ & ValueDef(isExport, _, Some t, value)) -> patternDoc isExport t value
        | Def(ValueDef(isExport, n, t, Kind ExternSyntax.Bodiless)) -> declarationDoc isExport n t
        | Def(ValueDef(false, n, t, Kind (MutSyntax.Call inner))) -> defDoc false "mut" n t inner
        | Def(ValueDef(isExport, n, t, value)) -> defDoc isExport "def" n t value
        | Def(TypeDef(n, modifiers, parameters, body)) -> typeDefDoc n modifiers parameters body
        | Import items -> importDoc i.Span.Start i.Span.End items
        | ReExport item ->
            match item.Selection with
            | Some selection -> cat [ text "export "; imports.ImportSource item.Source; text " "; imports.ImportSelection selection ]
            | None -> cat [ text "export "; imports.ImportSource item.Source ]
        | Expr value -> expr statementPosition value
        | Attribute(n, argument) -> attributeDoc n argument i.Span
        | DimensionDecl(isExport, n, storage, derived) ->
            let export = if isExport then "export " else ""
            cat [ text $"{export}{UnitSyntax.dimensionKeyword} {Names.written n}"
                  (match storage with Some t -> cat [ text "("; typeExpr t; text ")" ] | None -> Doc.empty)
                  (match derived with Some t -> cat [ text " = "; typeExpr t ] | None -> Doc.empty) ]
        | UnitDecl(isExport, n, dimension, multiple) ->
            let export = if isExport then "export " else ""
            cat [ text $"{export}{UnitSyntax.unitKeyword} {Names.written n}: "
                  typeExpr dimension
                  (match multiple with Some value -> cat [ text " = "; expr full value ] | None -> Doc.empty) ]

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
