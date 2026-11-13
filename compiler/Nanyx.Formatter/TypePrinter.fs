/// Printing the types in a module: annotations, type arguments, and what type definitions stand for.
module internal Nanyx.Formatter.TypePrinter

open System
open Parser.Program

/// Prints types. The expressions in them (a refinement's predicate, a `typeof`, a field's default) are printed by
/// `expression`, or by `expressionFollowed` where something follows on the line.
type TypePrinter(expression: Expression -> Doc, expressionFollowed: Expression -> Doc) =
    let text = Doc.text
    let cat = Doc.concat
    let commaJoin docs = Doc.join (text ", ") docs

    /// A def's annotation, or anywhere the parser reads a list of types: `,` binds tighter than `->`, so
    /// `int, int -> int` is a function of two parameters.
    let rec signature (t: TypeExpr) : Doc =
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
                | _ -> atomType item)
            |> Doc.join (text " & ")
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
        | TypeOpenRecord(fields, row) ->
            let others = text ("..." + Option.defaultValue "" row)
            cat [ text "("; commaJoin (List.map recordTypeField fields @ [ others ]); text ")" ]
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
        | TypeFunc _
        | TypeUnion _
        | TypeIntersection _
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
        | TypeRecord fields when fields |> List.forall (function TypeField(n, false, Some _, None) -> not (isPosition n) | _ -> false) ->
            fields
            |> List.map (function
                | TypeField(n, _, Some t, _) -> cat [ text (Names.written n); text ": "; typeExpr t ]
                | _ -> Doc.empty)
            |> commaJoin
        | TypeWithContext _ -> parenType payload
        | _ -> typeList payload

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
        | TypeMember(n, Some t) -> cat [ text $"type {n} = "; typeExpr t ]
        | TypeMember(n, None) -> text $"type {n}"

    and contextSet (items: TypeExpr list) : Doc =
        let item (t: TypeExpr) =
            match t.Kind with
            | TypeApply(n, args) -> cat [ text n; text "("; typeArguments args; text ")" ]
            // An operator required by the member it reads: `` `+`: a, a -> a ``. Its type runs to the end of the set,
            // so among other contexts it is bracketed: `` [(`+`: a, a -> a) & Eq(a)] ``.
            | TypeRecord [ TypeField(n, false, Some memberType, None) ] ->
                let requirement = cat [ text (Names.written n); text ": "; typeList memberType ]
                if items.Length > 1 then cat [ text "("; requirement; text ")" ] else requirement
            | _ -> typeExpr t
        cat [ text "["; Doc.join (text " & ") (List.map item items); text "]" ]

    /// A def's annotation, or anywhere the parser reads a list of types.
    member _.Signature t = signature t

    /// A type anywhere a single type is read.
    member _.TypeExpr t = typeExpr t

    /// A field of a record type.
    member _.RecordTypeField field = recordTypeField field
