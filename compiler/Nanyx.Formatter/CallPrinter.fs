/// Printing what is between brackets: lists, maps, records, tuples, and the arguments of calls and pipes.
module internal Nanyx.Formatter.CallPrinter

open Parser.Program
open Nanyx.Formatter.SourceText
open Nanyx.Formatter.CommentPlacement

/// Prints bracketed items, with the comments among them put back by `placement`. The expressions in them are printed
/// by `expression`, told whether the expression is the last thing in the brackets (see `Position.Tail` in
/// `Formatter`).
type CallPrinter(indentSize: int, sourceText: SourceText, placement: CommentPlacement, expression: bool -> Expression -> Doc) =
    let text = Doc.text
    let cat = Doc.concat
    let hard = Doc.hardLine
    let nest doc = Doc.nest indentSize doc
    let commaJoin docs = Doc.join (text ", ") docs
    let hasCommentsIn start finish = placement.HasCommentsIn(start, finish)
    let rec listDoc (span: SourceSpan) (segments: Choice<Expression list, Expression> list) : Doc =
        let items =
            segments
            |> List.collect (function
                | Choice1Of2 elements -> elements |> List.map (fun element -> false, element)
                | Choice2Of2 spread -> [ true, spread ])
        let count = items.Length
        let itemDoc index (spread, item: Expression) =
            cat [ (if spread then text "..." else Doc.empty); expression (index = count - 1) item ]
        if sourceText.Valid span && hasCommentsIn span.Start span.End then
            let children =
                items
                |> List.mapi (fun index ((_, item: Expression) as entry) -> item.Span.Start, item.Span.End, fun () -> itemDoc index entry)
            cat [ text "["; nest (cat [ hard; placement.Sequence false span.Start span.End children ]); hard; text "]" ]
        else
            bracketed "[" "]" (List.mapi itemDoc items)

    /// A map literal, `[k => v, ...]`, laid out as a list's items are.
    and mapDoc (span: SourceSpan) (entries: (Expression * Expression) list) : Doc =
        let count = entries.Length
        let entryDoc index (key: Expression, value: Expression) =
            cat [ expression false key; text " => "; expression (index = count - 1) value ]
        if sourceText.Valid span && hasCommentsIn span.Start span.End then
            let children =
                entries
                |> List.mapi (fun index ((key: Expression, value: Expression) as entry) -> key.Span.Start, value.Span.End, fun () -> entryDoc index entry)
            cat [ text "["; nest (cat [ hard; placement.Sequence false span.Start span.End children ]); hard; text "]" ]
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
    and fieldsDoc (start: int) (finish: int) (fields: RecordField list) : Doc = recordFieldsDoc false start finish fields

    /// Fields in brackets, as `fieldsDoc` lays them out. With `spread`, they are the parts of a record literal with a
    /// spread (see `SpreadSyntax`), and each positional one is a record spread, `...r`.
    and recordFieldsDoc (spread: bool) (start: int) (finish: int) (fields: RecordField list) : Doc =
        let count = fields.Length
        let fieldDoc index field =
            let isLast = index = count - 1
            match field with
            | NamedField(n, value) -> cat [ text (Names.written n); text " = "; expression isLast value ]
            | PositionalField value when spread -> cat [ text "..."; expression isLast value ]
            | PositionalField value -> expression isLast value
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
            cat [ text "("; nest (cat [ hard; placement.Sequence false start finish children ]); hard; text ")" ]
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
        | Some span when sourceText.Valid span -> span.End
        | _ -> fallback

    /// Whether a lambda argument follows the brackets of a call whose name ends at `afterName` (`f { x -> x }`), rather
    /// than being in them.
    and isTrailingLambda (afterName: int) (argument: Expression) =
        match argument.Kind with
        | Lambda _ -> not (sourceText.Valid argument.Span && afterName <= argument.Span.Start && sourceText.NextChar afterName = '(')
        | _ -> false

    and callArguments (call: Expression) (afterName: int) (args: Expression list) : Doc =
        match args with
        | [] -> text "()"
        | _ -> pipeArguments call afterName args

    /// Whether `argument`, one of the values a call's brackets were parsed into, was written after them instead: a
    /// trailing lambda ends where its call does, and one in the brackets ends before the `)`.
    and endsCall (call: Expression) (argument: Expression) =
        match argument.Kind with
        | Lambda _ -> sourceText.Valid argument.Span && sourceText.Valid call.Span && argument.Span.End >= call.Span.End
        | _ -> false

    and pipeArguments (call: Expression) (afterName: int) (args: Expression list) : Doc =
        let withTrailing (fields: RecordField list) (lambda: Expression) =
            cat [ fieldsDoc afterName lambda.Span.Start fields; text " "; expression true lambda ]
        match args with
        | [] -> Doc.empty
        | [ argument ] when isTrailingLambda afterName argument -> cat [ text " "; expression true argument ]
        // `f(a, y = 1) { ... }`: the lambda was put among the named fields' positional ones. They go back in the order
        // they were written.
        | [ Kind (RecordExpr fields) ] when fields |> List.exists (function PositionalField e -> endsCall call e | NamedField _ -> false) ->
            let lambda = fields |> List.pick (function PositionalField e when endsCall call e -> Some e | _ -> None)
            let fieldStart = function PositionalField e -> e.Span.Start | NamedField(_, e) -> e.Span.Start
            let written = fields |> List.filter (function PositionalField e -> not (obj.ReferenceEquals(e, lambda)) | NamedField _ -> true)
            withTrailing (written |> List.sortBy fieldStart) lambda
        // A pipe has each value in its brackets as an argument of its own, so a tuple among them was written in brackets
        // of its own, as in `x \f((a, b))`. Named fields are one record of them all, before the lambda.
        | _ when (match call.Kind with Pipe _ -> true | _ -> false) ->
            let fieldsOf (written: Expression list) =
                match written with
                | [ Kind (RecordExpr fields) ] -> fields
                | _ -> written |> List.map PositionalField
            if endsCall call (List.last args) then withTrailing (fieldsOf (List.take (args.Length - 1) args)) (List.last args)
            else fieldsDoc afterName call.Span.End (fieldsOf args)
        | [ argument ] -> parenFields argument afterName call.Span.End
        // `f(a, b) { ... }`, whose brackets' values and lambda are the call's arguments. A tuple among them was written
        // in brackets of its own, as in `f((a, b)) { ... }`.
        | _ when endsCall call (List.last args) ->
            let lambda = List.last args
            withTrailing (args |> List.take (args.Length - 1) |> List.map PositionalField) lambda
        | _ -> cat [ text "("; commaJoin (List.map (expression true) args); text ")" ]

    member _.ListDoc (span: SourceSpan) (segments: Choice<Expression list, Expression> list) : Doc = listDoc span segments

    member _.MapDoc (span: SourceSpan) (entries: (Expression * Expression) list) : Doc = mapDoc span entries

    member _.FieldsDoc (start: int) (finish: int) (fields: RecordField list) : Doc = fieldsDoc start finish fields

    member _.RecordFieldsDoc (spread: bool) (start: int) (finish: int) (fields: RecordField list) : Doc = recordFieldsDoc spread start finish fields

    member _.ParenFields (argument: Expression) (start: int) (finish: int) : Doc = parenFields argument start finish

    member _.NameEnd (nameSpan: SourceSpan option) (fallback: int) = nameEnd nameSpan fallback

    member _.CallArguments (call: Expression) (afterName: int) (args: Expression list) : Doc = callArguments call afterName args

    member _.PipeArguments (call: Expression) (afterName: int) (args: Expression list) : Doc = pipeArguments call afterName args