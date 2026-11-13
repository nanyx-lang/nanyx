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

type Options =
    { /// The width lines are laid out to fit.
      LineWidth: int
      /// The spaces each level of indentation adds.
      IndentSize: int }

module Options =
    let defaults = { LineWidth = 100; IndentSize = 2 }

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
            match (branches ()).IfArm e with
            | Some _ -> ifDoc e
            | None -> (branches ()).MatchDoc e scrutinees arms
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

    /// Operator and pipe chains in the module, with their operands printed as the rest of the module's are.
    and operators () : OperatorPrinter = OperatorPrinter(options.IndentSize, source, sourceText, placement, parts (), calls ())

    and binary (position: Position) (op: string) (l: Expression) (r: Expression) : Doc = (operators ()).Binary position op l r

    and spreadSegments (e: Expression) : Choice<Expression list, Expression> list option = (operators ()).SpreadSegments e

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

    /// What the printers taken out of this one call back into it for.
    and parts () : ExpressionParts =
        { Expression = expr
          NeedsParentheses = needsParentheses
          IsWrittenBlock = isWrittenBlock
          Block = block
          StatementsInSequence = statementsInSequence
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
