/// The recursive-descent parser's type expressions: the grammar of `Types.fs`, read from the layout pass's tokens. It
/// gives up at a type that holds an expression (a field's default, a `where` constraint), which the expression parser
/// will read once it is written (see the Work in progress page "The parser").
module Parser.Program.DescentTypes

open Parser.Program.Lexer
open Parser.Program.Layout
open Parser.Program.DescentCursor

/// One type, or several made into the type `combine` builds from them, spanning from `start`.
let private singleOrCombined (c: Cursor) (start: int) (combine: TypeExpr list -> TypeExprKind) (items: TypeExpr list) =
    match items with
    | [ single ] -> single
    | multiple -> TypeExpr.at (c.SpanFrom start) (combine multiple)

/// An identifier as the FParsec parser reads one, which may be a keyword: `#match` is a tag.
let private identifier (c: Cursor) = fst (identifierWithSpan c)

/// `.name`, written straight after what is before it.
let private dotName (c: Cursor) =
    if not c.Adjacent then raise NoMatch
    c.Expect Dot |> ignore
    if not c.Adjacent then raise NoMatch
    identifier c

/// A name qualified with dots, written with no spaces: `m.Email`.
let private dottedName (c: Cursor) =
    let head = identifier c
    let tail = many c (fun () -> dotName c)
    String.concat "." (head :: tail)

/// A `?` straight after a type or a field's name, which is reported.
let private noOptionalMarker (c: Cursor) =
    if c.Adjacent && c.Is(Operator, "?") then c.GiveUp "an optional marker, which is a mistake"

/// Whether the code token at the cursor, after any line breaks FParsec's `ws` would cross, is `kind` (with `text`).
let private across (c: Cursor) (kind: TokenKind) (text: string option) =
    c.Across(fun c -> c.Is(kind, ?text = text))

let private typeIdentifier (c: Cursor) =
    if c.Is Dollar then c.GiveUp "a `$` before a type's name, which is a mistake"
    let start = c.Start
    let name = dottedName c
    let kind =
        if name = "_" then TypeHole
        elif Names.isTypeVariable name then TypeVar name
        else TypeName name
    TypeExpr.at (c.SpanFrom start) kind

let rec private typeOf (c: Cursor) =
    let start = c.Start
    c.Expect(Keyword, "typeof") |> ignore
    if not c.OnSameLine then raise NoMatch
    let path () =
        let head = c.Expect Name
        let headSpan = c.SpanOf head
        let fields = many c (fun () ->
            c.Expect Dot |> ignore
            let field = c.Expect Name
            c.Text field, c.SpanOf field)
        fields
        |> List.fold
            (fun ((expr: Expression), span) (field, fieldSpan) ->
                let span = SourceSpan.between span fieldSpan
                Expression.at span (MemberAccess(expr, field, Some fieldSpan)), span)
            (Expression.at headSpan (IdentifierExpr(c.Text head)), headSpan)
        |> fst
    let operand =
        if c.Is LeftParen then
            c.Take() |> ignore
            let inner = path ()
            c.Expect RightParen |> ignore
            inner
        else path ()
    TypeExpr.at (c.SpanFrom start) (TypeOf operand)

/// `type a`, a positional parameter that takes a type argument.
and private typeArgument (c: Cursor) =
    let start = c.Start
    c.Expect(Keyword, "type") |> ignore
    if not c.OnSameLine || not (c.Is Name) then raise NoMatch
    let operand = typeIdentifier c
    match operand.Kind with
    | TypeVar _ -> TypeExpr.at (c.SpanFrom start) (TypeArgument operand)
    | _ -> c.GiveUp "a positional type parameter that isn't a type variable"

/// `@name(argument)` before a field, on its line or the one above (see `TypeFieldAttribute`).
and private fieldAttribute (c: Cursor) =
    let start = c.Start
    c.Expect At |> ignore
    if not c.Adjacent then raise NoMatch
    let attributeName = identifier c
    let argument = if c.Adjacent && c.Is LeftParen then Some(Forward.attributeArgument.Value c) else None
    let span = c.SpanFrom start
    c.Across(fun c -> c.Token.IsSome) |> ignore
    TypeFieldAttribute(attributeName, argument, span)

/// `name: T` in a record type, with its default after `=` if it has one, and the attributes before it, with the names
/// it declares noted for the record (`declared`).
and private typeField (c: Cursor) (declared: ResizeArray<Identifier * SourceSpan>) : TypeRecordField list =
    let attributes = many c (fun () -> fieldAttribute c)
    let fieldStart = c.Start
    let isMut =
        if c.IsKeyword "mut" then
            c.Take() |> ignore
            true
        else false
    let fieldName, nameSpan = name c
    declared.Add((fieldName, nameSpan))
    noOptionalMarker c
    if isMut then c.NoteDocumentable fieldStart nameSpan.Start
    if not c.OnSameLine || not (c.Is Colon || c.Is DoubleColon) then raise NoMatch
    c.Take() |> ignore
    // `type` alone is the type of a context's associated type (see `TypeKeyword`).
    let value =
        if c.IsKeyword "type" then
            let token = c.Take()
            TypeExpr.at (c.SpanOf token) TypeKeyword
        else typeExpr c
    let defaultValue =
        if across c Equals None then
            c.Take() |> ignore
            c.Across(fun c -> c.Token.IsSome) |> ignore
            Some(Forward.expression.Value c)
        else None
    c.Declare NamedFieldValue value.Id [ fieldName, nameSpan ]
    attributes @ [ TypeField(fieldName, isMut, Some value, defaultValue) ]

/// The fields of a record type, each after a `,` or on a line of its own.
and private namedTypeFields (c: Cursor) (declared: ResizeArray<Identifier * SourceSpan>) =
    let separator () =
        if not (c.Skip Comma) && c.Adjacent then raise NoMatch
    sepBy1 c (fun () -> typeField c declared) separator |> List.concat

/// A record type in brackets: positional fields, then named ones, then `...` or `...r` if it is open.
and private typeRecord (c: Cursor) =
    let start = c.Start
    c.Expect LeftParen |> ignore
    c.NoteDocumentable start c.Start
    let positional =
        many c (fun () ->
            let item = typeExpr c
            c.Expect Comma |> ignore
            item)
        |> List.mapi (fun index typeValue -> TypeField(string index, false, Some typeValue, None))
    let declared = ResizeArray()
    let named = namedTypeFields c declared
    let others =
        c.Attempt(fun () ->
            if not (c.Skip Comma) && c.Adjacent then raise NoMatch
            c.Expect Ellipsis |> ignore
            if c.Adjacent && c.Is Name then Some(identifier c) else None)
    c.Expect RightParen |> ignore
    let kind =
        match others with
        | Some row -> TypeOpenRecord(positional @ named, row)
        | None -> TypeRecord(positional @ named)
    let record = TypeExpr.at (c.SpanFrom start) kind
    c.Declare RecordTypeFields record.Id (List.ofSeq declared)
    record

/// Brackets around a type, a tuple of types, or nothing: `()` is unit.
and private typeTupleOrParen (c: Cursor) =
    let start = c.Start
    c.Expect LeftParen |> ignore
    let inner = c.Attempt(fun () -> typeSignature c)
    c.Expect RightParen |> ignore
    let span = c.SpanFrom start
    match inner with
    | None -> TypeExpr.at span TypeUnit
    | Some({ TypeExpr.Kind = TypeTuple _ } as tuple) -> { tuple with Span = span }
    | Some single -> single

/// A tag, with a payload of named fields or of types, `#pair(int, string)`, or none.
and private typeTag (c: Cursor) =
    let start = c.Start
    c.Expect Hash |> ignore
    if not c.Adjacent then raise NoMatch
    let tagName = identifier c
    let payload =
        if c.Adjacent && c.Is LeftParen then
            let open' = c.Take()
            if c.Is RightParen then
                c.Take() |> ignore
                Some(TypeExpr.at (c.SpanFrom open'.Start) TypeUnit)
            else
                let fields () =
                    let fieldsStart = c.Start
                    let field () =
                        let fieldName = identifier c
                        noOptionalMarker c
                        if not c.OnSameLine || not (c.Is Colon || c.Is DoubleColon) then raise NoMatch
                        c.Take() |> ignore
                        TypeField(fieldName, false, Some(typeExpr c), None)
                    let items = sepBy1 c field (fun () -> c.Expect Comma |> ignore)
                    TypeExpr.at (c.SpanFrom fieldsStart) (TypeRecord items)
                let inner =
                    match c.Attempt(fun () -> let r = fields () in (if not (c.Is RightParen) then raise NoMatch); r) with
                    | Some record -> record
                    | None -> typeListToType (typeList c)
                c.Expect RightParen |> ignore
                Some inner
        else None
    TypeExpr.at (c.SpanFrom start) (TypeTag(tagName, payload))

/// `name(args)`, a type applied to arguments: types, named fields as one record, or named arguments after positional
/// ones.
and private typeApply (c: Cursor) =
    let start = c.Start
    let applied = dottedName c
    if not c.Adjacent || not (c.Is LeftParen) then raise NoMatch
    c.Take() |> ignore
    let recordArgument () =
        let fieldsStart = c.Start
        let declared = ResizeArray()
        let fields = namedTypeFields c declared
        if not (c.Is RightParen) then raise NoMatch
        let record = TypeExpr.at (c.SpanFrom fieldsStart) (TypeRecord fields)
        c.Declare RecordTypeFields record.Id (List.ofSeq declared)
        [ record ]
    let withNamedArguments () =
        let argument () =
            match c.Attempt(fun () ->
                let argStart = c.Start
                let argName = identifier c
                if not c.OnSameLine then raise NoMatch
                c.Expect Equals |> ignore
                let value = typeUnion c
                TypeExpr.at (c.SpanFrom argStart) (TypeNamedArgument(argName, value))) with
            | Some named -> named
            | None -> typeUnion c
        let args = sepBy1 c argument (fun () -> (if not c.OnSameLine then raise NoMatch); c.Expect Comma |> ignore)
        if not (c.Is RightParen) then raise NoMatch
        if args |> List.exists (fun arg -> match arg.Kind with TypeNamedArgument _ -> true | _ -> false) then args
        else raise NoMatch
    let typeArguments () =
        match typeList c with
        | (args, None), _ -> args
        | list -> [ typeListToType list ]
    let args =
        match c.Attempt recordArgument with
        | Some args -> args
        | None ->
            match c.Attempt withNamedArguments with
            | Some args -> args
            | None -> typeArguments ()
    c.Expect RightParen |> ignore
    TypeExpr.at (c.SpanFrom start) (TypeApply(applied, args))

/// `typeApply`, and `.member` after it: an associated type.
and private typeApplyOrAssociated (c: Cursor) =
    let start = c.Start
    let applied = typeApply c
    match c.Attempt(fun () -> dotName c) with
    | Some memberName -> TypeExpr.at (c.SpanFrom start) (TypeAssociated(applied, memberName))
    | None -> applied

/// `name: T` in a context list: the old way of requiring an operator, read so that it can be reported.
and private contextMember (c: Cursor) =
    let start = c.Start
    let memberName, _ = name c
    if not c.OnSameLine then raise NoMatch
    c.Expect Colon |> ignore
    let memberType = typeSignature c
    TypeExpr.at (c.SpanFrom start) (TypeRecord [ TypeField(memberName, false, Some memberType, None) ])

/// The contexts a function needs, in `[` and `]`.
and private contextSet (c: Cursor) =
    let start = c.Start
    c.Expect LeftBracket |> ignore
    let item () =
        let bracketed () =
            let open' = c.Expect LeftParen
            let inner = contextMember c
            c.Expect RightParen |> ignore
            { inner with Span = c.SpanFrom open'.Start }
        c.Attempt bracketed
        |> Option.orElseWith (fun () -> c.Attempt(fun () -> contextMember c))
        |> Option.orElseWith (fun () -> c.Attempt(fun () -> typeApply c))
        |> Option.defaultWith (fun () -> typeIdentifier c)
    let separator () =
        if not c.OnSameLine then raise NoMatch
        if not (c.Skip Comma || c.Skip Ampersand || c.Skip(Operator, "+")) then raise NoMatch
    let items = if c.Is RightBracket then [] else sepBy1 c item separator
    c.Expect RightBracket |> ignore
    TypeExpr.at (c.SpanFrom start) (TypeContext items)

and private typeAtom (c: Cursor) : TypeExpr =
    let first (alternatives: (unit -> TypeExpr) list) =
        alternatives
        |> List.tryPick (fun read -> c.Attempt read)
        |> Option.defaultWith (fun () -> raise NoMatch)
    if c.Is Name && (match c.At 1 with Code next -> next.Kind = DoubleColon | _ -> false) then
        c.GiveUp "a constrained type, whose `where` is an expression"
    first
        [ fun () -> typeOf c
          fun () -> typeArgument c
          fun () -> typeTag c
          fun () -> typeApplyOrAssociated c
          fun () -> contextSet c
          fun () -> typeRecord c
          fun () -> typeTupleOrParen c
          fun () -> typeIdentifier c ]

/// A type, `mut T` (a cell's type), and no `?` after it.
and internal typePostfix (c: Cursor) =
    let start = c.Start
    let atom =
        if c.IsKeyword CellPassingSyntax.typeName then
            c.Take() |> ignore
            let held = typeAtom c
            TypeExpr.at (c.SpanFrom start) (TypeApply(CellPassingSyntax.typeName, [ held ]))
        else typeAtom c
    noOptionalMarker c
    atom

and private typeIntersection (c: Cursor) =
    let start = c.Start
    sepBy1 c (fun () -> typePostfix c) (fun () -> if not (across c Ampersand None) then raise NoMatch else c.Take() |> ignore)
    |> singleOrCombined c start TypeIntersection

/// A `|`, before a union's first type or between two. A doc comment before it documents the type after it.
and private bar (c: Cursor) =
    let token = c.Expect Bar
    c.NoteDocumentable token.Start c.Start

and internal typeUnion (c: Cursor) =
    // A `|` before the first type is no part of the union's span, which starts at that type.
    if c.Is Bar then bar c
    let start = c.Start
    sepBy1 c (fun () -> typeIntersection c) (fun () -> if across c Bar None then bar c else raise NoMatch)
    |> singleOrCombined c start TypeUnion

and private typeArrow (c: Cursor) =
    let start = c.Start
    let left = typeUnion c
    match c.Attempt(fun () -> (if not (across c Arrow None) then raise NoMatch); c.Take() |> ignore; typeExpr c) with
    | Some right -> TypeExpr.at (c.SpanFrom start) (TypeFunc(left, right))
    | None -> left

/// A type expression: `Types.fs`'s `typeExpr`.
and typeExpr (c: Cursor) : TypeExpr =
    let withContext () =
        let start = c.Start
        let contexts = contextSet c
        match c.Attempt(fun () -> typeArrow c) with
        | Some t ->
            let items = match contexts.Kind with TypeContext items -> items | _ -> []
            TypeExpr.at (c.SpanFrom start) (TypeWithContext(items, t))
        | None -> contexts
    match c.Attempt withContext with
    | Some t -> t
    | None -> typeArrow c

/// Types separated by `,`, and the result after `->` if there is one, spanning them all.
and private typeList (c: Cursor) : (TypeExpr list * TypeExpr option) * SourceSpan =
    let start = c.Start
    let items = sepBy1 c (fun () -> typeUnion c) (fun () -> (if not c.OnSameLine then raise NoMatch); c.Expect Comma |> ignore)
    let result = c.Attempt(fun () -> (if not (across c Arrow None) then raise NoMatch); c.Take() |> ignore; typeExpr c)
    (items, result), c.SpanFrom start

/// A def's annotation: a type list, after the contexts it needs if it has any (`Types.fs`'s `typeSignature`).
and typeSignature (c: Cursor) : TypeExpr =
    let withContext () =
        let start = c.Start
        let contexts = contextSet c
        let list = typeList c
        let items = match contexts.Kind with TypeContext items -> items | _ -> []
        match list with
        | (first :: rest, None), span when not rest.IsEmpty ->
            let first = TypeExpr.at (SourceSpan.between contexts.Span first.Span) (TypeWithContext(items, first))
            typeListToType ((first :: rest, None), span)
        | _ -> TypeExpr.at (c.SpanFrom start) (TypeWithContext(items, typeListToType list))
    match c.Attempt withContext with
    | Some t -> t
    | None -> typeListToType (typeList c)

/// `source`, the whole of it, read as a def's annotation is, if the new parser reads it: `None` when it gives up or
/// something follows the type, for the FParsec parser to read it instead.
let readTypeSignature (source: string) : TypeExpr option =
    try
        let c = cursorOf "" source
        let read = typeSignature c
        while (match c.At 0 with Code _ -> false | _ -> true) do
            c.Index <- c.Index + 1
        if c.Is EndOfFile then Some read else None
    with
    | GiveUp _
    | NoMatch -> None
