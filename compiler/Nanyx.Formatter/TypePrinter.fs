/// Printing the types in a module: annotations, type arguments, and what type definitions stand for.
module internal Nanyx.Formatter.TypePrinter

open System
open Parser.Program

/// Prints types. The expressions in them (a refinement's predicate, a `typeof`, a field's default) are printed by
/// `expression`, or by `expressionFollowed` where something follows on the line. A field's attribute is printed by
/// `attribute`, as a top-level one is.
type TypePrinter(expression: Expression -> Doc, expressionFollowed: Expression -> Doc, attribute: Identifier -> Expression option -> SourceSpan -> Doc) =
    let text = Doc.text
    let cat = Doc.concat
    let commaJoin docs = Doc.join (text ", ") docs

    /// A def's annotation, or anywhere the parser reads a list of types: `,` binds tighter than `->`, so
    /// `int, int -> int` is a function of two parameters.
    let rec signature (t: TypeExpr) : Doc =
        match t.Kind with
        // Contexts in front of a list of types without `->` are its first item's, so a tuple they are written on is
        // bracketed.
        | TypeWithContext(contexts, ({ Kind = TypeTuple _ } as inner)) -> cat [ contextSet contexts; text " "; parenType inner ]
        | TypeWithContext(contexts, inner) -> cat [ contextSet contexts; text " "; typeList inner ]
        | _ -> typeList t

    and typeList (t: TypeExpr) : Doc =
        match t.Kind with
        // A tuple's first item takes the contexts in front of it without brackets: `[Console] Thunk(int), int`.
        | TypeTuple ({ Kind = TypeWithContext(contexts, inner) } :: rest) when (match inner.Kind with TypeFunc _ | TypeTuple _ -> false | _ -> true) ->
            commaJoin (cat [ contextSet contexts; text " "; listItem inner ] :: List.map listItem rest)
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
        | TypeUnion items -> items |> List.map unionCase |> Doc.join (text " | ")
        | _ -> intersectionType t

    /// One case of a union, bracketed where it would otherwise be read as more than one case or as a function.
    and unionCase (item: TypeExpr) : Doc =
        match item.Kind with
        | TypeUnion _
        | TypeFunc _
        | TypeWithContext _ -> parenType item
        | _ -> intersectionType item

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
                | _ -> atomType item)
            |> Doc.join (text " & ")
        | _ -> atomType t

    and parenType (t: TypeExpr) : Doc =
        match t.Kind with
        | TypeTuple _ -> cat [ text "("; typeList t; text ")" ]
        | _ -> cat [ text "("; signature t; text ")" ]

    and atomType (t: TypeExpr) : Doc =
        match t.Kind with
        | TypeName n
        | TypeVar n -> text n
        | TypeHole -> text "_"
        | TypeUnit -> text "()"
        | TypeTuple _ -> parenType t
        | TypeRecord fields -> cat [ text "("; commaJoin (recordTypeFields fields); text ")" ]
        | TypeOpenRecord(fields, row) ->
            let others = text ("..." + Option.defaultValue "" row)
            cat [ text "("; commaJoin (recordTypeFields fields @ [ others ]); text ")" ]
        // `mut int`, the type of a cell (see `CellPassingSyntax`): what it holds is an atom, so a bigger type is bracketed.
        | TypeApply(n, [ held ]) when n = CellPassingSyntax.typeName ->
            match held.Kind with
            | TypeFunc _ | TypeUnion _ | TypeIntersection _ | TypeWithContext _ | TypeTuple _ -> cat [ text "mut "; parenType held ]
            | _ -> cat [ text "mut "; typeExpr held ]
        | TypeApply(n, args) -> cat [ text n; text "("; typeArguments args; text ")" ]
        | TypeTag(n, None) -> text ("#" + n)
        | TypeTag(n, Some (Kind TypeUnit)) -> text $"#{n}()"
        | TypeTag(n, Some payload) -> cat [ text $"#{n}("; tagPayload payload; text ")" ]
        | TypeContext items -> contextSet items
        | TypeConstraint(n, baseType, predicate) ->
            cat [ text n; text " :: "; typeExpr baseType; text " where "; expression predicate ]
        | TypeWhere(baseType, predicate) -> cat [ typeExpr baseType; text " where "; expression predicate ]
        | TypeOf value -> cat [ text "typeof "; expression value ]
        | TypeKeyword -> text "type"
        | TypeNamedArgument(n, value) -> cat [ text n; text " = "; unionType value ]
        | TypeAssociated(context, n) -> cat [ atomType context; text ("." + n) ]
        | TypeArgument operand -> cat [ text "type "; typeExpr operand ]
        | TypeFunc _
        | TypeUnion _
        | TypeIntersection _
        | TypeWithContext _ -> parenType t

    /// Type arguments are a list of types, so one function argument needs no brackets: `map(a -> b)`, and nor does one
    /// record: `list(price: int)`.
    and typeArguments (args: TypeExpr list) : Doc =
        match args with
        | [ single ] ->
            match single.Kind with
            | TypeTuple _
            | TypeWithContext _ -> parenType single
            | _ -> bareRecord single |> Option.defaultWith (fun () -> typeExpr single)
        | _ -> commaJoin (List.map listItem args)

    /// The fields of `t` without its brackets, where it is a record of named fields, each with a type and nothing else,
    /// as a tag's payload or a type's only argument writes it: `#point(x: int, y: int)`, `list(price: int)`.
    and bareRecord (t: TypeExpr) : Doc option =
        let isPosition (n: string) = n.Length > 0 && Seq.forall Char.IsDigit n
        match t.Kind with
        | TypeRecord fields when fields |> List.forall (function TypeField(n, false, Some _, None) -> not (isPosition n) | _ -> false) ->
            fields
            |> List.map (function
                | TypeField(n, _, Some t, _) -> cat [ text (Names.written n); text ": "; typeExpr t ]
                | _ -> Doc.empty)
            |> commaJoin
            |> Some
        | _ -> None

    and tagPayload (payload: TypeExpr) : Doc =
        match payload.Kind with
        | TypeWithContext _ -> parenType payload
        | _ -> bareRecord payload |> Option.defaultWith (fun () -> typeList payload)

    and recordTypeField (field: TypeRecordField) : Doc =
        match field with
        | TypeField(n, false, Some t, None) when n.Length > 0 && Seq.forall Char.IsDigit n -> typeExpr t
        | TypeField(n, isMutable, typeOpt, defaultValue) ->
            cat [
                (if isMutable then text "mut " else Doc.empty)
                text (Names.written n)
                (match typeOpt with
                 | Some t -> cat [ text ": "; typeExpr t ]
                 | None -> Doc.empty)
                (match defaultValue with
                 | Some value -> cat [ text " = "; expressionFollowed value ]
                 | None -> Doc.empty)
            ]
        | TypeFieldAttribute(n, argument, span) -> attribute n argument span

    /// The fields of a record type on one line, each after its attributes: `(@customKeywordType(#bind) await: ...)`.
    and recordTypeFields (fields: TypeRecordField list) : Doc list =
        TypeRecordField.withAttributes fields
        |> List.map (fun (attributes, field) ->
            cat [
                for name, argument, span in attributes do
                    attribute name argument span
                    text " "
                recordTypeField field
            ])

    and contextSet (items: TypeExpr list) : Doc = cat [ text "["; commaJoin (contextItems items); text "]" ]

    and contextItems (items: TypeExpr list) : Doc list =
        let item (t: TypeExpr) =
            match t.Kind with
            | TypeApply(n, args) -> cat [ text n; text "("; typeArguments args; text ")" ]
            // An operator required by the member it reads, `` `+`: a, a -> a ``, which the compiler reports. Its type
            // runs to the end of the list, so among other contexts it is bracketed: `` [(`+`: a, a -> a), Eq(a)] ``.
            | TypeRecord [ TypeField(n, false, Some memberType, None) ] ->
                let requirement = cat [ text (Names.written n); text ": "; typeList memberType ]
                if items.Length > 1 then cat [ text "("; requirement; text ")" ] else requirement
            | _ -> typeExpr t
        List.map item items

    /// A def's annotation, as `signature` writes it on one line where it fits after the `: ` it follows, and otherwise
    /// broken, each line after the first indented to the column after the `: `:
    ///
    ///   : [Console, FileSystem]      contexts on a line of their own, one per line if they still don't fit
    ///     string,                    parameters one per line, each but the last followed by `,`
    ///     int
    ///     -> | #ok(Conn)             `->` and the result on a line of their own, and a union in the result one case
    ///        | #error(string)        per line, each after a `|`, as a type definition's are
    ///
    /// A part that fits on its line once the parts around it are broken stays on it. A type inside a parameter is not
    /// broken.
    and brokenSignature (t: TypeExpr) : Doc =
        let separatedByLines (docs: Doc list) = Doc.join (cat [ text ","; Doc.line ]) docs
        let union (t: TypeExpr) =
            match t.Kind with
            | TypeUnion(first :: rest) ->
                Doc.group (cat [ Doc.IfBreak(text "| ", Doc.empty); unionCase first; cat [ for case in rest -> cat [ Doc.line; text "| "; unionCase case ] ] ])
            | _ -> typeExpr t
        let arrow (parameters: Doc list) (result: TypeExpr) =
            Doc.group (cat [ separatedByLines parameters; Doc.line; text "-> "; Doc.nest "-> ".Length (union result) ])
        let typeList (t: TypeExpr) =
            match t.Kind with
            | TypeTuple _ -> typeList t
            | TypeFunc(parameter, result) ->
                match parameter.Kind with
                | TypeTuple items -> arrow (List.map listItem items) result
                | TypeFunc _
                | TypeWithContext _
                | TypeUnion _ -> arrow [ parenType parameter ] result
                | _ -> arrow [ unionType parameter ] result
            | TypeUnion _ -> union t
            | _ -> typeExpr t
        let broken =
            match t.Kind with
            | TypeWithContext(contexts, inner) ->
                let rest = match inner.Kind with TypeTuple _ -> parenType inner | _ -> typeList inner
                let contexts = Doc.group (cat [ text "["; Doc.nest 1 (separatedByLines (contextItems contexts)); text "]" ])
                Doc.group (cat [ contexts; Doc.line; rest ])
            | _ -> typeList t
        Doc.nest ": ".Length broken

    /// A def's annotation, or anywhere the parser reads a list of types.
    member _.Signature t = signature t

    /// A def's annotation after its `: `, broken where it doesn't fit (see `brokenSignature`).
    member _.BrokenSignature t = brokenSignature t

    /// One case of a union, as `UnionType` writes it between its `|`s.
    member _.UnionCase t = unionCase t

    /// A type anywhere a single type is read.
    member _.TypeExpr t = typeExpr t
    /// One type, bracketed unless it is an atom: what `type` is followed by in `type Point` or `type (a -> b)`.
    member _.AtomType t = atomType t

    /// A field of a record type.
    member _.RecordTypeField field = recordTypeField field
