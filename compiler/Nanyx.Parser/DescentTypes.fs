/// The recursive-descent parser's type expressions, read from the layout pass's tokens. A type that holds an expression
/// (a field's default, a `where` constraint) reads it through `Forward`.
module Parser.Program.DescentTypes

open Parser.Program.Lexer
open Parser.Program.Layout
open Parser.Program.DescentCursor

/// What types separated by commas mean, with the result they go to when an arrow follows them, spanning `span`:
/// `a, b -> c` is a function of two parameters, since `,` binds tighter than `->`. The parameters' tuple spans the
/// parameters.
let private typeListToType ((items: TypeExpr list, resultOpt: TypeExpr option), span: SourceSpan) : TypeExpr =
    let tuple () = TypeExpr.at (SourceSpan.between (List.head items).Span (List.last items).Span) (TypeTuple items)
    match items, resultOpt with
    | [ single ], None -> single
    | _, None -> TypeExpr.at span (TypeTuple items)
    | [ single ], Some result -> TypeExpr.at span (TypeFunc(single, result))
    | _, Some result -> TypeExpr.at span (TypeFunc(tuple (), result))

/// One type, or several made into the type `combine` builds from them, spanning from `start`.
let private singleOrCombined (c: Cursor) (start: int) (combine: TypeExpr list -> TypeExprKind) (items: TypeExpr list) =
    match items with
    | [ single ] -> single
    | multiple -> TypeExpr.at (c.SpanFrom start) (combine multiple)

/// An identifier, which may be a keyword: `#match` is a tag.
let private identifier (c: Cursor) = fst (identifierWithSpan c)

/// `.name`, written straight after what is before it.
let private dotName (c: Cursor) =
    if not c.Adjacent then raise NoMatch
    c.Expect Dot |> ignore
    if not c.Adjacent then raise NoMatch
    noDollar c
    identifier c

/// A name qualified with dots, written with no spaces: `m.Email`.
let private dottedName (c: Cursor) =
    let head = identifier c
    let tail = manyIf c (fun () -> c.Adjacent && c.Expects Dot) (fun () -> dotName c)
    String.concat "." (head :: tail)

/// A `?` straight after a type or a field's name, which is reported with `message`: a value that may be absent has its
/// absence in its type.
let private noOptionalMarker (c: Cursor) (message: string) =
    if c.Adjacent && c.Is(Operator, "?") then c.Report c.Start message

/// The message for a `?` after the field `name`.
let private optionalField (name: Identifier) =
    $"A field can't be marked optional with `?`: write one that may be absent as `{Names.written name}: #some(T) | #none`"

/// Whether the code token at the cursor, after any line breaks, is `kind` (with `text`).
let private across (c: Cursor) (kind: TokenKind) (text: string option) =
    c.Across(fun c -> c.Is(kind, ?text = text))

/// Whether the brackets that open at `offset` from the cursor may hold a named field, after the positional types that may
/// come first: whether a field's start, an attribute, `mut`, or a name and a `:`, `::` or `?` after it, or something that
/// can't start a type, is first in the brackets or after a `,` outside any brackets in them. Where none is, a reading as
/// a record would read the types in them and be thrown away, and so is not tried (#2218).
let private mayHoldField (c: Cursor) (offset: int) =
    // The offset of the first code token at or after `i`, and the token.
    let rec code (i: int) =
        match c.At i with
        | Code token -> i, token
        | _ -> code (i + 1)
    let startsField (first: Token) (afterFirst: int) =
        let _, next = code afterFirst
        match first.Kind with
        | At -> true
        | Keyword when c.Text first = "mut" -> true
        | Name | Keyword | BacktickedName | LeftParen | LeftBracket | Hash ->
            next.Kind = Colon || next.Kind = DoubleColon || (next.Kind = Operator && c.Text next = "?")
        | _ -> true
    let mutable i, current = code (offset + 1)
    let mutable depth = 0
    let mutable found = startsField current (i + 1)
    let mutable going = not found
    while going do
        match current.Kind with
        | EndOfFile -> going <- false
        | LeftParen | LeftBracket -> depth <- depth + 1
        | RightParen | RightBracket when depth = 0 -> going <- false
        | RightParen | RightBracket -> depth <- depth - 1
        | Comma when depth = 0 ->
            let after, afterToken = code (i + 1)
            if startsField afterToken (after + 1) then
                found <- true
                going <- false
        | _ -> ()
        if going then
            let next, nextToken = code (i + 1)
            i <- next
            current <- nextToken
    found

/// Whether a `->` is next, across line breaks, as a reading of a function type's result asks first.
let private arrowNext (c: Cursor) = c.AcrossFits(fun c -> c.Is Arrow)

/// Whether a `,` is next on the line, as the separator of a list of types asks: where it isn't on the line, the separator
/// fails noting nothing, and where something else is, it notes that a `,` was expected.
let private commaNext (c: Cursor) = c.OnSameLine && c.Expects Comma

/// Whether a context's member, `name: T`, can be read past its name at the cursor: where a name is there and no `:`
/// after it on its line, `contextMember` would fail having taken the name, and this notes what it would have noted. A
/// name in backticks is left to `contextMember`, which may report it.
let private memberNext (c: Cursor) =
    if c.Is BacktickedName then true
    elif not (c.Is Name || c.Is Keyword) then false
    elif not (c.OnSameLineAt 1) then
        c.NoteAt 1
        false
    elif c.IsAt(1, Colon) then true
    else
        c.NoteAt(1, "`:`")
        false

/// Whether `typeApply` can read past the name at the cursor: whether a name qualified with dots, as `dottedName` reads
/// it, is there with `(` straight after it. Where it isn't, `typeApply` would fail, having taken the name, and this notes
/// what it would have noted (see `Cursor.AttemptIf`).
let private appliedNext (c: Cursor) =
    let isName (offset: int) = c.IsAt(offset, Name) || c.IsAt(offset, Keyword)
    if not (isName 0) then false
    else
        // The offset of the dotted name's last part: `dotName` takes a `.` and a name, each straight after the one before.
        let mutable last = 0
        while c.AdjacentAt(last + 1) && c.IsAt(last + 1, Dot) && c.AdjacentAt(last + 2) && isName (last + 2) do
            last <- last + 2
        if c.AdjacentAt(last + 1) && c.IsAt(last + 1, Dot) then true
        elif c.AdjacentAt(last + 1) && c.IsAt(last + 1, LeftParen) then true
        else
            if c.AdjacentAt(last + 1) then c.NoteAt(last + 1, "`.`") else c.NoteAt(last + 1)
            false

let private typeIdentifier (c: Cursor) =
    noDollar c
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
        let fields = manyIf c (fun () -> c.Expects Dot) (fun () ->
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

/// `type a`, the old form of a positional parameter that takes a type argument, now written `Type(a)`: it still
/// compiles, with a warning (see the compiler's `TypeArguments`).
and private typeArgument (c: Cursor) =
    let start = c.Start
    c.Expect(Keyword, "type") |> ignore
    if not c.OnSameLine || not (c.Is Name) then raise NoMatch
    let operand = typeIdentifier c
    match operand.Kind with
    | TypeVar _ -> TypeExpr.at (c.SpanFrom start) (TypeArgument operand)
    | _ -> c.Report operand.Span.Start "A parameter that takes a type is written with its type, as in `Type(a)`"

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
    let attributes = manyIf c (fun () -> c.StartsWith At) (fun () -> fieldAttribute c)
    let fieldStart = c.Start
    let isMut =
        if c.IsKeyword "mut" then
            c.Take() |> ignore
            true
        else false
    let fieldName, nameSpan = name c
    declared.Add((fieldName, nameSpan))
    noOptionalMarker c (optionalField fieldName)
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
                let field () =
                    let fieldName = identifier c
                    noOptionalMarker c (optionalField fieldName)
                    if not c.OnSameLine || not (c.Is Colon || c.Is DoubleColon) then raise NoMatch
                    c.Take() |> ignore
                    TypeField(fieldName, false, Some(typeExpr c), None)
                let fields () =
                    let fieldsStart = c.Start
                    let items = sepBy1If c field (fun () -> c.Expects Comma) (fun () -> c.Expect Comma |> ignore)
                    TypeExpr.at (c.SpanFrom fieldsStart) (TypeRecord items)
                // Positional types, then named fields, as a record type writes them: `#rect(string, width: float)`.
                let mixed () =
                    if not (mayHoldField c -1) then raise NoMatch
                    let fieldsStart = c.Start
                    let positional =
                        many c (fun () ->
                            let item = typeExpr c
                            c.Expect Comma |> ignore
                            item)
                        |> List.mapi (fun index typeValue -> TypeField(string index, false, Some typeValue, None))
                    if positional.IsEmpty then raise NoMatch
                    let named = sepBy1If c field (fun () -> c.Expects Comma) (fun () -> c.Expect Comma |> ignore)
                    TypeExpr.at (c.SpanFrom fieldsStart) (TypeRecord(positional @ named))
                let closed (payload: unit -> TypeExpr) () =
                    let r = payload ()
                    if not (c.Is RightParen) then raise NoMatch
                    r
                let inner =
                    match c.Attempt(closed fields) with
                    | Some record -> record
                    | None ->
                        match c.Attempt(closed mixed) with
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
    // The arguments are read once, each as `name = T` or as a type, and are named arguments if any of them is named, or
    // else types, which an arrow after them makes one function type (#2218).
    let arguments () =
        // A named argument takes a name and expects `=` after it on its line.
        let namedNext () =
            if not (c.Is Name || c.Is Keyword) then false
            elif not (c.OnSameLineAt 1) then
                c.NoteAt 1
                false
            elif c.IsAt(1, Equals) then true
            else
                c.NoteAt(1, "`=`")
                false
        let argument () =
            match c.AttemptIf(namedNext (), fun () ->
                let argStart = c.Start
                let argName = identifier c
                if not c.OnSameLine then raise NoMatch
                c.Expect Equals |> ignore
                let value = typeUnion c
                TypeExpr.at (c.SpanFrom argStart) (TypeNamedArgument(argName, value))) with
            | Some named -> named
            | None -> typeUnion c
        let listStart = c.Start
        let args = sepBy1If c argument (fun () -> commaNext c) (fun () -> (if not c.OnSameLine then raise NoMatch); c.Expect Comma |> ignore)
        if args |> List.exists (fun arg -> match arg.Kind with TypeNamedArgument _ -> true | _ -> false) then
            if not (c.Is RightParen) then raise NoMatch
            args
        else
            match c.AttemptIf(arrowNext c, fun () -> (if not (across c Arrow None) then raise NoMatch); c.Take() |> ignore; typeExpr c) with
            | Some result -> [ typeListToType ((args, Some result), c.SpanFrom listStart) ]
            | None -> args
    // A record argument's first field is what `typeField` reads: its attributes, `mut`, and a name with `:` or `::`
    // after it on its line.
    let recordNext () =
        c.Token.IsSome
        && (c.StartsWith At
            || c.IsKeyword "mut"
            || c.Is BacktickedName
            || ((c.Is Name || c.Is Keyword)
                && ((c.AdjacentAt 1 && c.IsAt(1, Operator, "?"))
                    || (c.OnSameLineAt 1 && (c.IsAt(1, Colon) || c.IsAt(1, DoubleColon)))
                    || (c.NoteAt 1; false))))
    let args =
        match c.AttemptIf(recordNext (), recordArgument) with
        | Some args -> args
        | None -> arguments ()
    c.Expect RightParen |> ignore
    TypeExpr.at (c.SpanFrom start) (TypeApply(applied, args))

/// `typeApply`, and `.member` after it: an associated type.
and private typeApplyOrAssociated (c: Cursor) =
    let start = c.Start
    let applied = typeApply c
    match c.AttemptIf(c.Adjacent && c.Expects Dot, fun () -> dotName c) with
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
        c.AttemptIf(c.Expects LeftParen, bracketed)
        |> Option.orElseWith (fun () -> c.AttemptIf(memberNext c, fun () -> contextMember c))
        |> Option.orElseWith (fun () -> c.AttemptIf(appliedNext c, fun () -> typeApply c))
        |> Option.defaultWith (fun () -> typeIdentifier c)
    let separator () =
        if not c.OnSameLine then raise NoMatch
        if not (c.Skip Comma || c.Skip Ampersand || c.Skip(Operator, "+")) then raise NoMatch
    let separates () = c.OnSameLine && (c.Is Comma || c.Is Ampersand || c.Is(Operator, "+"))
    let items = if c.Is RightBracket then [] else sepBy1If c item separates separator
    c.Expect RightBracket |> ignore
    TypeExpr.at (c.SpanFrom start) (TypeContext items)

and private typeAtom (c: Cursor) : TypeExpr =
    // Each alternative is tried only where the token at the cursor lets it match (see `Cursor.AttemptIf`).
    let first (alternatives: (unit -> TypeExpr option) list) =
        alternatives
        |> List.tryPick (fun attempt -> attempt ())
        |> Option.defaultWith (fun () -> raise NoMatch)
    if c.Is Name && (match c.At 1 with Code next -> next.Kind = DoubleColon | _ -> false) then
        c.GiveUp "a constrained type, whose `where` is an expression"
    first
        [ fun () -> c.AttemptIf(c.StartsWith(Keyword, "typeof"), fun () -> typeOf c)
          fun () -> c.AttemptIf(c.StartsWith(Keyword, "type"), fun () -> typeArgument c)
          fun () -> c.AttemptIf(c.StartsWith Hash, fun () -> typeTag c)
          fun () -> c.AttemptIf(appliedNext c, fun () -> typeApplyOrAssociated c)
          fun () -> c.AttemptIf(c.StartsWith LeftBracket, fun () -> contextSet c)
          fun () ->
              c.AttemptIf((if c.Is LeftParen then mayHoldField c 0 else c.StartsWith LeftParen), fun () -> typeRecord c)
          fun () -> c.AttemptIf(c.StartsWith LeftParen, fun () -> typeTupleOrParen c)
          fun () -> c.Attempt(fun () -> typeIdentifier c)
          fun () -> c.Attempt(fun () -> c.Fail "a type") ]

/// A type, `mut T` (a cell's type), and no `?` after it.
and internal typePostfix (c: Cursor) =
    let start = c.Start
    let atom =
        if c.IsKeyword CellPassingSyntax.typeName then
            c.Take() |> ignore
            let held = typeAtom c
            TypeExpr.at (c.SpanFrom start) (TypeApply(CellPassingSyntax.typeName, [ held ]))
        else typeAtom c
    noOptionalMarker c "A type can't be marked optional with `?`: write a value that may be absent as `#some(T) | #none`"
    atom

/// A type, or a derived dimension: types joined by `*` and `/` on one line, read from the left, so `Length / Time / Time`
/// is `Length` divided by `Time` twice (see `UnitSyntax`).
and private typeDimension (c: Cursor) =
    let start = c.Start
    let next () =
        let isTimes = c.Is(Operator, UnitSyntax.times)
        if not (isTimes || c.Is(Operator, UnitSyntax.over)) || not c.OnSameLine then raise NoMatch
        c.Take() |> ignore
        isTimes, typePostfix c
    let mutable result = typePostfix c
    let mutable going = true
    while going do
        let fits = (c.Is(Operator, UnitSyntax.times) || c.Is(Operator, UnitSyntax.over)) && c.OnSameLine
        match c.AttemptIf(fits, next) with
        | Some(isTimes, right) ->
            result <- TypeExpr.at (c.SpanFrom start) (if isTimes then TypeProduct(result, right) else TypeQuotient(result, right))
        | None -> going <- false
    result

and private typeIntersection (c: Cursor) =
    let start = c.Start
    sepBy1If c (fun () -> typeDimension c) (fun () -> c.AcrossFits(fun c -> c.Is Ampersand)) (fun () ->
        if not (across c Ampersand None) then raise NoMatch else c.Take() |> ignore)
    |> singleOrCombined c start TypeIntersection

/// A `|`, before a union's first type or between two. A doc comment before it documents the type after it.
and private bar (c: Cursor) =
    let token = c.Expect Bar
    c.NoteDocumentable token.Start c.Start

and internal typeUnion (c: Cursor) =
    // A `|` before the first type is no part of the union's span, which starts at that type.
    if c.Is Bar then bar c
    let start = c.Start
    // A case on a line of its own, indented past the line before, starts a block of arms (layout rule 3), as a def's
    // long result does: `-> | #some(Entry)` and its other cases under the first `|`. The block ends after the last
    // case, and its `Dedent` is the union's.
    let next () =
        let before = c.Index
        if not (across c Bar None) then raise NoMatch
        let opensBlock = c.Index > before && (match c.At(before - c.Index) with Indent _ -> true | _ -> false)
        bar c
        opensBlock, typeIntersection c
    let first = typeIntersection c
    let rest = ResizeArray()
    let mutable inBlock = false
    let mutable going = true
    while going do
        match c.AttemptIf(c.AcrossFits(fun c -> c.Is Bar), next) with
        | Some(opensBlock, case) ->
            inBlock <- inBlock || opensBlock
            rest.Add case
        | None -> going <- false
    if inBlock && (match c.At 0 with Dedent _ -> true | _ -> false) then c.Index <- c.Index + 1
    first :: List.ofSeq rest |> singleOrCombined c start TypeUnion

and private typeArrow (c: Cursor) =
    let start = c.Start
    let left = typeUnion c
    match c.AttemptIf(arrowNext c, fun () -> (if not (across c Arrow None) then raise NoMatch); c.Take() |> ignore; typeExpr c) with
    | Some right -> TypeExpr.at (c.SpanFrom start) (TypeFunc(left, right))
    | None -> left

/// A type expression.
and typeExpr (c: Cursor) : TypeExpr =
    let withContext () =
        let start = c.Start
        let contexts = contextSet c
        match c.Attempt(fun () -> typeArrow c) with
        | Some t ->
            let items = match contexts.Kind with TypeContext items -> items | _ -> []
            TypeExpr.at (c.SpanFrom start) (TypeWithContext(items, t))
        | None -> contexts
    match c.AttemptIf(c.StartsWith LeftBracket, withContext) with
    | Some t -> t
    | None -> typeArrow c

/// Types separated by `,`, and the result after `->` if there is one, spanning them all.
and private typeList (c: Cursor) : (TypeExpr list * TypeExpr option) * SourceSpan =
    let start = c.Start
    let items = sepBy1If c (fun () -> typeUnion c) (fun () -> commaNext c) (fun () -> (if not c.OnSameLine then raise NoMatch); c.Expect Comma |> ignore)
    let result = c.AttemptIf(arrowNext c, fun () -> (if not (across c Arrow None) then raise NoMatch); c.Take() |> ignore; typeExpr c)
    (items, result), c.SpanFrom start

/// A def's annotation: a type list, after the contexts it needs if it has any.
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
    match c.AttemptIf(c.StartsWith LeftBracket, withContext) with
    | Some t -> t
    | None -> typeListToType (typeList c)

/// `source` read as the type in a def's annotation is written: `bool -> string`, `list(a), (a -> bool) -> list(a)`, or
/// the mistake in it.
let readTypeSignature (source: string) : Result<TypeExpr, ParseError> =
    let mutable cursor = None
    try
        let c = cursorOf "" source
        cursor <- Some c
        let read = typeSignature c
        while (match c.At 0 with Code _ -> false | _ -> true) do
            c.Index <- c.Index + 1
        if c.Is EndOfFile then Ok read else c.Fail "the end of the type"
    with
    | Reported error -> Error error
    | GiveUp _
    | NoMatch -> Error(cursor.Value.Unexpected())
