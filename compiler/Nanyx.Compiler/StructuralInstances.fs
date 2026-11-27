namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// Instances of `ToString`, `Eq`, `Hash` and `Ord` the compiler derives from a type's structure, part by part (see "Printing values"
/// and "Common contexts" on the docs' Contexts page).
///
/// A tag union, a tuple, a record, a list, a map or a set has no `toString` attached, so a hole of such a type, or a
/// call that requires `ToString` of it, finds an instance written from its parts: `#some(a)` is written `#some(` then
/// its payload then `)`, and the payload is written through its own `ToString`, whichever that is. A measured value is
/// one too: its number of default storage units, written in the largest of its dimension's units it is a whole number
/// of (see the docs' Units of measure page).
///
/// The instance is an ordinary definition the compiler writes, one for each type, as the user would have written it:
///
///     def __toString_1: [ToString(a)] (#some(a) | #none) -> string = { __v ->
///       match __v
///         | #some(__p0) -> "#some({__p0})"
///         | #none -> "#none"
///     }
///
/// It is added to the module once the module's own items are inferred, and inferred as any definition is, so its
/// holes find their instances as any hole does: the compiler's own for an `int`, an attached `toString` for a type
/// that has one, another derived definition for a part that is a structure itself. A type that names itself finds
/// its own definition. A part with no `ToString`, a function, is an error there, reported at the hole that needed
/// the definition.
///
/// A definition for a type with no variables is the `toString` of a synthesized instance. One for a type with
/// variables requires `ToString` of each, so a hole calls it by name instead of the hole function, with the
/// instances in scope where the hole is (see `InferState.DerivedCalls`), and any other call is passed it as a closure
/// over those instances (`InstanceDerived`). A part with an instance loaded by `use` where the call is counts as a
/// variable, so it is written through that instance (see `abstractParts`).
///
/// Every type that holds no function has the compiler's own `Eq`, which compares two values by their structure and
/// reads no `==` a part's type has of its own. So where a part has one, an `==` attached to its type or the `Eq` a
/// function requires of a type variable, the structure is compared by a derived definition instead, which compares
/// each part with `==`, as the user would have written it:
///
///     def __equals_2: [Eq(a)] ((#some(a) | #none), (#some(a) | #none)) -> bool = { __v, __w ->
///       match __v, __w
///         | #some(__p0), #some(__q0) -> __p0 == __q0
///         | #none, #none -> true
///         | _, _ -> false
///     }
///
/// A comparison of a union with a rest the function around chooses, `#some(Email) | r`, is derived the same way, with
/// the rest kept in its type: two values of a tag the rest holds, or of different tags, are compared with the
/// compiler's own `__equals`, and the definition requires no `Eq(r)` (see `WellKnownContexts.comparedRests`).
///
/// `Hash` is derived where `Eq` is, so that values a derived `==` finds equal have the same hash: a structure with a
/// part that has a `hash` of its own is hashed by a definition that hashes each part with `__hashOf`, which finds the
/// part's own `Hash`, and folds the hashes with `__hashCombine`.
module internal StructuralInstances =
    /// Whether a call that requires the context `ctxName` of a structure with no instance of its own finds a derived
    /// one: `ToString`. `Eq` has the compiler's own, and a derived one only as `comparesParts` says.
    let derives (ctxName: Identifier) = ctxName = InterpolationHoles.context

    /// The member of the context `ctxName` a derived definition is.
    let private memberOf (ctxName: Identifier) =
        if ctxName = EqualityOperators.context then EqualityOperators.equals
        elif ctxName = WellKnownContexts.hashing.Context then WellKnownContexts.hashing.Member
        elif ctxName = WellKnownContexts.ordering.Context then WellKnownContexts.ordering.Member
        else InterpolationHoles.memberName

    /// What a type is built from, as far as writing a value of it goes.
    type private Shape =
        /// A union of tags, or one tag: each tag's name with its payload's type.
        | TagShape of (string * Ty option) list
        /// A union of the tags named here and a rest the caller of the function around chooses. Only `Eq` is derived
        /// for one: two values of a tag it does not name are compared with the compiler's own.
        | OpenTagShape of (string * Ty option) list
        | TupleShape of Ty list
        /// A record's named fields, in the order they are written out in: by name.
        | RecordShape of (string * Ty) list
        | ListShape of Ty
        /// A map's key and value types. `Eq`, `Hash` and `ToString` are derived for a map, or a set, whose keys or
        /// values have their own: otherwise its store is compared and hashed by the compiler's own, which a store, one
        /// trie for one set of keys, makes right (see `Maps.storeOf`).
        | MapShape of Ty * Ty
        | SetShape of Ty
        /// An array's element type. `Eq` and `ToString` are derived for an array, as for a set; `Hash` is not.
        | ArrayShape of Ty
        /// A measured value's dimension and storage, and the units the module sees that it is written in, which `derive`
        /// fills in: a dimension's, the largest first (see `Units.seenOf`), and a derived dimension's parts' default
        /// storage and display units. Only `ToString` is derived for one: it is compared and hashed as its storage.
        | MeasureShape of Dimension * storage: Ty * units: UnitInfo list

    let private isTuple (fields: Map<string, Ty>) = not fields.IsEmpty && WellKnownContexts.isTuple fields


    /// `ty` without the variables that leave its tag unions open to more tags, at any depth: by the time an instance
    /// is derived, such a union holds only the tags it names. One of them in `rigid`, the variables of the function
    /// around that stand for a type its caller chooses, which may hold any tag, is kept with `keepRigid`, as a derived
    /// `==` compares the tags it does not name with the compiler's own; without it, the type is `None`.
    let rec private closedKeeping (keepRigid: bool) (rigid: Set<int>) (ty: Ty) : Ty option =
        let closed = closedKeeping keepRigid
        let all (tys: Ty list) =
            let closedTys = tys |> List.map (closed rigid)
            if closedTys |> List.forall Option.isSome then Some (closedTys |> List.map Option.get) else None
        let fieldsOf (fields: Map<string, Ty>) =
            let names, tys = fields |> Map.toList |> List.unzip
            all tys |> Option.map (fun tys -> List.zip names tys |> Map.ofList)
        match ty with
        | TyVar _
        | TyPrimitive _
        | TyParam _
        | TyNominal _
        | TyContexts _ -> Some ty
        | TyAlias(name, args, underlying) ->
            match all args, closed rigid underlying with
            | Some args, Some underlying -> Some (TyAlias(name, args, underlying))
            | _ -> None
        | TyApply(name, args) -> all args |> Option.map (fun args -> TyApply(name, args))
        | TyScope scope -> all [ scope ] |> Option.map (List.exactlyOne >> TyScope)
        | TyMeasure(dimension, storage) -> closed rigid storage |> Option.map (fun storage -> TyMeasure(dimension, storage))
        | TySelf args -> all args |> Option.map TySelf
        | TyMember(index, args) -> all args |> Option.map (fun args -> TyMember(index, args))
        // Closed: a group's definitions have no variables.
        | TyGroup _ -> Some ty
        | TyRecursive(body, args) ->
            match all (body :: args) with
            | Some (body :: args) -> Some (TyRecursive(body, args))
            | _ -> None
        | TyFunc(arg, ret, contexts) -> all [ arg; ret ] |> Option.map (fun parts -> TyFunc(parts.[0], parts.[1], contexts))
        | TyRecord(fields, order) -> fieldsOf fields |> Option.map (fun fields -> TyRecord(fields, order))
        | TyOpenRecord(fields, row) -> fieldsOf fields |> Option.map (fun fields -> TyOpenRecord(fields, row))
        | TyTag(name, payload) ->
            match payload with
            | Some payload -> closed rigid payload |> Option.map (fun payload -> TyTag(name, Some payload))
            | None -> Some ty
        | TyUnion items ->
            let rests, named = items |> List.partition (function TyVar _ -> true | _ -> false)
            let rigidRests = rests |> List.filter (function TyVar v -> rigid.Contains v.Id | _ -> false)
            if not rigidRests.IsEmpty && not keepRigid then None
            else all named |> Option.map (fun named -> TyUnion (named @ rigidRests))

    /// `ty` closed as an instance of `ToString` is derived for it (see `closedKeeping`).
    let private closed = closedKeeping false

    /// `ty` closed as an instance of the context `ctxName` is derived for it: a rest in `rigid` is kept for `Eq`.
    let private closedFor (ctxName: Identifier) = closedKeeping (ctxName = EqualityOperators.context)

    /// The tags of the items of a union, if tags are all it holds, each once.
    let rec private tagsOf (items: Ty list) : (string * Ty option) list option =
        let tags =
            items
            |> List.map (fun item ->
                match Unifier.unalias item with
                | TyTag(name, payload) -> Some [ name, payload ]
                | TyUnion nested -> tagsOf nested
                | _ -> None)
        if tags |> List.forall Option.isSome then
            let tags = tags |> List.collect Option.get |> List.distinct
            if (tags |> List.distinctBy fst).Length = tags.Length then Some tags else None
        else None

    let private shapeOf (ty: Ty) : Shape option =
        match Unifier.unalias ty with
        | TyTag(name, payload) -> Some (TagShape [ name, payload ])
        | TyUnion items when not items.IsEmpty -> tagsOf items |> Option.map TagShape
        | TyRecord(fields, _) when isTuple fields -> Some (TupleShape (WellKnownContexts.tupleItems fields))
        | TyRecord(fields, _) when not fields.IsEmpty -> Some (RecordShape (Map.toList fields))
        | Lists.ListType element -> Some (ListShape element)
        | _ -> None

    /// What `ty` is built from, as far as comparing or hashing two values of it goes: a map, a set and an array are
    /// structures too, of their keys and values, or elements.
    /// A union with a rest left in it, as `closedFor` leaves one for `Eq`, is one too.
    let private comparedShapeOf (ty: Ty) : Shape option =
        match Unifier.unalias ty with
        | Maps.MapType(key, value) -> Some (MapShape(key, value))
        | Sets.SetType element -> Some (SetShape element)
        | Arrays.ArrayType element -> Some (ArrayShape element)
        | TyUnion items when items |> List.exists (function TyVar _ -> true | _ -> false) ->
            items |> List.filter (function TyVar _ -> false | _ -> true) |> tagsOf |> Option.map OpenTagShape
        | _ -> shapeOf ty

    /// What `ty` is built from, as far as writing it as text goes: a map is written entry by entry, and a set element
    /// by element, and so is an array.
    let private writtenShapeOf (ty: Ty) : Shape option =
        match Unifier.unalias ty with
        | Maps.MapType(key, value) -> Some (MapShape(key, value))
        | Sets.SetType element -> Some (SetShape element)
        | Arrays.ArrayType element -> Some (ArrayShape element)
        | TyMeasure(dimension, storage) -> Some (MeasureShape(dimension, storage, []))
        | _ -> shapeOf ty

    /// What `ty` is built from, as far as ordering two values of it goes: only a tuple and a list are, since a
    /// record's fields and a union's tags have no order of their own.
    let private orderedShapeOf (ty: Ty) : Shape option =
        match shapeOf ty with
        | Some (TupleShape _ | ListShape _ as shape) -> Some shape
        | _ -> None

    /// The shape an instance of `ctxName` is derived from: `Hash` as `Eq`, so that values a derived `==` finds equal have
    /// the same hash.
    let private shapeFor (ctxName: Identifier) =
        if ctxName = EqualityOperators.context || ctxName = WellKnownContexts.hashing.Context then comparedShapeOf
        elif ctxName = InterpolationHoles.context then writtenShapeOf
        elif ctxName = WellKnownContexts.ordering.Context then orderedShapeOf
        else shapeOf

    let private partsOf (shape: Shape) : Ty list =
        match shape with
        | TagShape tags
        | OpenTagShape tags -> tags |> List.choose snd
        | TupleShape items -> items
        | RecordShape fields -> fields |> List.map snd
        | ListShape element
        | SetShape element
        | ArrayShape element -> [ element ]
        | MapShape(key, value) -> [ key; value ]
        | MeasureShape(_, storage, _) -> [ storage ]

    /// The first part of the structure `ty`, at any depth, that `found` picks, as `shape` takes structures apart. A
    /// type met again is not looked inside again, as it holds what it did the first time: a recursive alias holds
    /// itself (see `RecursiveAliases`).
    let private tryPickPart (shape: Ty -> Shape option) (found: Ty -> 'a option) (ty: Ty) : 'a option =
        let rec pick (seen: Set<Ty>) (ty: Ty) =
            if seen.Contains ty then None
            else
                match found ty with
                | Some result -> Some result
                | None -> shape ty |> Option.bind (partsOf >> List.tryPick (pick (seen.Add ty)))
        pick Set.empty ty

    /// The part of the structure `ty`, at any depth, that is a function, if one is.
    let private functionPart (ty: Ty) : Ty option =
        ty |> tryPickPart writtenShapeOf (fun part -> match Unifier.unalias part with TyFunc _ -> Some part | _ -> None)

    /// Whether `ty` is an array, which only a derived `==` compares: the compiler's own reads an array only inside a
    /// map's store. A map and a set are compared by the compiler's own unless their keys or values have their own.
    let private isCollection (ty: Ty) =
        match comparedShapeOf ty with
        | Some (ArrayShape _) -> true
        | _ -> false

    /// Whether `ty` is an array, or holds one at any depth, so that only a derived `==` compares it.
    let holdsCollection (ty: Ty) : bool =
        (ty |> tryPickPart comparedShapeOf (fun part -> if isCollection part then Some () else None)).IsSome

    /// Whether comparing two values of the structure `ty` compares a part, at any depth, that `hasOwn` says has an
    /// `==` the compiler's own comparison would not read, or that is an array, or is one itself. `rigid` is as
    /// `closedKeeping` has it: a union with a rest in it compares the parts of the tags it names.
    let comparesParts (rigid: Set<int>) (hasOwn: Ty -> bool) (ty: Ty) : bool =
        let needsDerived (part: Ty) = hasOwn part || isCollection part
        let inParts (ty: Ty) =
            isCollection ty
            || comparedShapeOf ty |> Option.exists (partsOf >> List.exists (tryPickPart comparedShapeOf (fun part -> if needsDerived part then Some () else None) >> Option.isSome))
        closedFor EqualityOperators.context rigid ty |> Option.exists inParts

    /// The type an instance of `ctxName` is derived for and the variables it requires the context of, for a value of
    /// the type `ty`: `ty` closed (see `closedFor`), if it is a structure, with its variables in the order they appear,
    /// but for the rests of its unions, whose `Eq` is checked at each call to the function around, not passed (see
    /// `WellKnownContexts.comparedRests`). `None` for a type with a variable an annotation could not name, one inside
    /// a nominal type.
    let private derivable (rigid: Set<int>) (ctxName: Identifier) (ty: Ty) : (Ty * Shape * TyVar list) option =
        closedFor ctxName rigid ty
        |> Option.bind (fun ty ->
            let vars = ContextInference.varsInOrder ty
            let rests = ContextInference.unionRests ty |> List.map _.Id |> Set.ofList
            match shapeFor ctxName ty with
            | Some shape when TypeEnv.freeTyVars ty = (vars |> List.map _.Id |> Set.ofList) ->
                Some (ty, shape, vars |> List.filter (fun var -> not (rests.Contains var.Id)))
            | _ -> None)

    /// The variables an instance of `ToString` derived for the type `ty` requires the context of, if one can be
    /// derived (see `derivable`).
    let variables (rigid: Set<int>) (ty: Ty) : TyVar list option =
        derivable rigid InterpolationHoles.context ty |> Option.map (fun (_, _, vars) -> vars)

    let private valueName = "__v"

    let private partName (index: int) = $"__p{index}"


    /// The text a derived `toString` writes for `value`, a measured value of `dimension` stored as `storage`, by a
    /// prelude function, where the storage is an `int` or a `float`, or a type that converts to one: its number of
    /// default storage units, in the largest of `units` it is a whole number of for an integer, and in the largest its
    /// magnitude is at least 1 of for a float. Zero is written in the dimension's default display unit, `0s`. Where
    /// the default storage unit is made up, as no declared unit is one, an integer no unit fits is written as an exact
    /// decimal of the smallest unit it is one of, `2.6mm`. An
    /// integer no unit fits and a value of any other storage, one of a type variable's, are written in the default
    /// storage unit, through the storage's own `ToString`, or as `dbg` writes them, `Time(500)`, if the module sees no
    /// default storage unit. A value of a derived dimension is written in its parts' default display units, multiplied
    /// and divided, `10.4m/s`, and never in its own units: an integer that is no whole number of them in its parts'
    /// default storage units, `1500mm*mm`, and any value as `dbg` writes it if the module sees no default display unit
    /// of a part.
    let private measuredText (span: SourceSpan) (dimension: Dimension) (storage: Ty) (units: UnitInfo list) (value: Expression) : Expression =
        let expr = Expression.at span
        let call (callee: Identifier) (args: Expression list) = expr (FunctionCall(callee, None, args))
        let text (s: string) = expr (LiteralExpr (StringLit s))
        let integer (n: bigint) = expr (LiteralExpr (IntLit n))
        let stored = call Units.storageIntrinsic [ value ]
        let partUnit (pick: UnitInfo -> bool) (id: NominalId) = units |> List.tryFind (fun info -> pick info && info.Dimension = Dimension.ofBase id)
        let storageOf = partUnit UnitInfo.isStorage
        let displayOf = partUnit _.IsDisplay
        let before, after =
            match units |> List.tryFind (fun info -> UnitInfo.isStorage info && info.Dimension = dimension) with
            | _ when Dimension.isDerived dimension && dimension |> List.forall (fst >> storageOf >> Option.isSome) ->
                "", Dimension.writtenWith (fun id -> (storageOf id).Value.Name) "*" "/" dimension
            | Some storageUnit when not (Dimension.isDerived dimension) -> "", storageUnit.Name
            | _ -> $"{Dimension.applied dimension}(", ")"
        // The value as an `int` or as a `float`, through the prelude's conversion to one where its storage has one that
        // loses nothing.
        let asInt, asFloat =
            match Unifier.unalias storage with
            | TyPrimitive "int" -> Some stored, None
            | SizedIntegerType integer when SizedInteger.bits integer < 64 -> Some (call $"{SizedInteger.name integer}.toInt" [ stored ]), None
            | TyPrimitive "float" -> None, Some stored
            | TyPrimitive "F32" -> None, Some (call "F32.toFloat" [ stored ])
            | _ -> None, None
        let inUnits (helper: Identifier) (number: Expression) (names: string list) (scales: Expression list) (display: string) =
            call helper [
                number
                expr (ListExpr (names |> List.map text))
                expr (ListExpr scales)
                text display
                text before
                text after ]
        let asStored () = expr (InterpolatedString [ StringText before; StringExpr (call InterpolationHoles.holeFunction [ stored ]); StringText after ])
        if Dimension.isDerived dimension then
            match dimension |> List.choose (fun (id, power) -> displayOf id |> Option.map (fun info -> info.Scale, power)) with
            | displayed when displayed.Length = dimension.Length ->
                let name = Dimension.writtenWith (fun id -> (displayOf id).Value.Name) "*" "/" dimension
                let scale = displayed |> List.fold (fun scale (part, power) -> Rational.multiply scale (Rational.power part power)) Rational.one
                match asInt, asFloat with
                | Some number, _ ->
                    call InterpolationHoles.measuredRatioFunction [
                        number; text name; integer scale.Numerator; integer scale.Denominator; text before; text after ]
                // Multiplied by how many display units a storage unit is, which rounds less than dividing by the scale.
                | _, Some number ->
                    let inDisplayUnits = expr (BinaryOp("*", None, number, expr (LiteralExpr (FloatLit (Rational.toFloat (Rational.divide Rational.one scale))))))
                    inUnits InterpolationHoles.measuredFloatFunction inDisplayUnits [ name ] [ expr (LiteralExpr (FloatLit 1.0)) ] name
                | None, None -> asStored ()
            | _ -> asStored ()
        else
            let display = units |> List.tryFind (fun info -> info.IsDisplay && info.Dimension = dimension) |> Option.map _.Name |> Option.defaultValue ""
            let units = units |> List.filter (fun info -> info.Dimension = dimension)
            match asInt, asFloat with
            | Some number, _ ->
                let whole = units |> List.filter (fun info -> Rational.isWhole info.Scale)
                // Where the default storage unit is made up, a value no unit fits is written as a decimal of one.
                let decimals =
                    if units |> List.exists UnitInfo.isStorage then []
                    else whole |> List.sortBy (fun info -> info.Scale.Numerator)
                call InterpolationHoles.measuredIntFunction [
                    number
                    expr (ListExpr (whole |> List.map (_.Name >> text)))
                    expr (ListExpr (whole |> List.map (fun info -> integer info.Scale.Numerator)))
                    text display
                    expr (ListExpr (decimals |> List.map (_.Name >> text)))
                    expr (ListExpr (decimals |> List.map (fun info -> integer info.Scale.Numerator)))
                    text before
                    text after ]
            | _, Some number ->
                let scales = units |> List.map (fun info -> expr (LiteralExpr (FloatLit (Rational.toFloat info.Scale))))
                inUnits InterpolationHoles.measuredFloatFunction number (units |> List.map _.Name) scales display
            | None, None -> asStored ()

    /// The text a derived `toString` writes for a value of the type `shape` is, read from `value`.
    let private toStringBody (span: SourceSpan) (shape: Shape) : Expression =
        let expr = Expression.at span
        let name (identifier: Identifier) = expr (IdentifierExpr identifier)
        let field (target: Expression) (fieldName: Identifier) = expr (MemberAccess(target, fieldName, None))
        // A part is written through its own `ToString`, as a hole writes it. The unit value has none: it is `()`.
        let part (ty: Ty) (value: Expression) =
            match Unifier.unalias ty with
            | TyPrimitive "unit" -> StringText "()"
            | _ -> StringExpr (expr (FunctionCall(InterpolationHoles.holeFunction, None, [ value ])))
        let separated (parts: StringPart list list) =
            parts |> List.mapi (fun index part -> if index = 0 then part else StringText ", " :: part) |> List.concat
        let positional (tys: Ty list) = tys |> List.mapi (fun index ty -> [ part ty (name (partName index)) ]) |> separated
        let named (target: Expression) (fields: (string * Ty) list) =
            fields |> List.map (fun (fieldName, ty) -> [ StringText $"{fieldName} = "; part ty (field target fieldName) ]) |> separated
        let text (parts: StringPart list) = expr (InterpolatedString parts)
        let bracketed (before: string) (parts: StringPart list) = text (StringText before :: parts @ [ StringText ")" ])
        let pattern = Pattern.at span
        let positions (count: int) = List.init count (partName >> IdentifierPattern >> pattern)
        let value = name valueName
        match shape with
        | TagShape tags ->
            let arm (tagName: string, payload: Ty option) : MatchArm =
                let whole = pattern (IdentifierPattern (partName 0))
                let payloadPattern, written =
                    match payload with
                    | None -> None, text [ StringText $"#{tagName}" ]
                    // A payload written out as a tuple or a record is written field by field, as the tag is made.
                    | Some (TyRecord(fields, _)) when isTuple fields ->
                        Some (pattern (TuplePattern(positions fields.Count, false))), bracketed $"#{tagName}(" (positional (WellKnownContexts.tupleItems fields))
                    | Some (TyRecord(fields, _)) when not fields.IsEmpty ->
                        Some whole, bracketed $"#{tagName}(" (named (name (partName 0)) (Map.toList fields))
                    | Some (TyPrimitive "unit") -> Some (pattern UnitPattern), text [ StringText $"#{tagName}()" ]
                    | Some payload -> Some whole, bracketed $"#{tagName}(" [ part payload (name (partName 0)) ]
                [ pattern (TagPattern(tagName, payloadPattern)) ], written
            expr (Match([ value ], tags |> List.map arm))
        | TupleShape items -> expr (Match([ value ], [ [ pattern (TuplePattern(positions items.Length, false)) ], bracketed "(" (positional items) ]))
        | RecordShape fields -> bracketed "(" (named value fields)
        // A collection is written by a prelude function, passed what writes each part.
        | ListShape _
        | SetShape _
        | ArrayShape _
        | MapShape _ ->
            let write (index: int) =
                let element = partName index
                expr (Lambda([ element, None ], text [ StringExpr (expr (FunctionCall(InterpolationHoles.holeFunction, None, [ name element ]))) ], false))
            let helper, writers =
                match shape with
                | MapShape _ -> InterpolationHoles.mapFunction, [ write 0; write 1 ]
                | SetShape _ -> InterpolationHoles.setFunction, [ write 0 ]
                | ArrayShape _ -> InterpolationHoles.arrayFunction, [ write 0 ]
                | _ -> InterpolationHoles.listFunction, [ write 0 ]
            expr (FunctionCall(helper, None, value :: writers))
        | MeasureShape(dimension, storage, units) -> measuredText span dimension storage units value
        | OpenTagShape _ -> invalidOp "ToString is never derived for an open union (see shapeFor)"

    let private otherName = "__w"

    let private otherPartName (index: int) = $"__q{index}"

    /// Whether `value` and `other`, two values of the type `shape` is, are equal, as a derived `==` works it out:
    /// each part compared with `==`, which finds the part's own.
    let private equalsBody (span: SourceSpan) (shape: Shape) : Expression =
        let expr = Expression.at span
        let name (identifier: Identifier) = expr (IdentifierExpr identifier)
        let field (target: Expression) (fieldName: Identifier) = expr (MemberAccess(target, fieldName, None))
        let pattern = Pattern.at span
        let answer (value: bool) = expr (LiteralExpr (BoolLit value))
        let same (left: Expression) (right: Expression) = expr (BinaryOp(EqualityOperators.equals, None, left, right))
        let all (comparisons: Expression list) =
            match comparisons with
            | [] -> answer true
            | first :: rest -> rest |> List.fold (fun both next -> expr (BinaryOp("and", None, both, next))) first
        let positions (partName: int -> Identifier) (count: int) = pattern (TuplePattern(List.init count (partName >> IdentifierPattern >> pattern), false))
        let value, other = name valueName, name otherName
        let arm (tagName: string, payload: Ty option) : MatchArm =
            let tag (payloadPattern: PatternKind option) = pattern (TagPattern(tagName, payloadPattern |> Option.map pattern))
            match payload with
            | None -> [ tag None; tag None ], answer true
            | Some (TyPrimitive "unit") -> [ tag (Some UnitPattern); tag (Some UnitPattern) ], answer true
            | Some _ ->
                [ tag (Some (IdentifierPattern (partName 0))); tag (Some (IdentifierPattern (otherPartName 0))) ],
                same (name (partName 0)) (name (otherPartName 0))
        let otherwise (answer: Expression) : MatchArm = [ pattern WildcardPattern; pattern WildcardPattern ], answer
        match shape with
        | TagShape tags ->
            // Two values of different tags are not equal.
            let different = if tags.Length > 1 then [ otherwise (answer false) ] else []
            expr (Match([ value; other ], (tags |> List.map arm) @ different))
        // Two values of a tag of the rest, or of different tags, are as the compiler's own comparison finds them.
        | OpenTagShape tags ->
            let builtIn = expr (FunctionCall(EqualityOperators.builtInFunction, None, [ value; other ]))
            expr (Match([ value; other ], (tags |> List.map arm) @ [ otherwise builtIn ]))
        | TupleShape items ->
            let parts = List.init items.Length (fun index -> same (name (partName index)) (name (otherPartName index)))
            expr (Match([ value; other ], [ [ positions partName items.Length; positions otherPartName items.Length ], all parts ]))
        | RecordShape fields -> fields |> List.map (fun (fieldName, _) -> same (field value fieldName) (field other fieldName)) |> all
        | ListShape _
        | MapShape _
        | SetShape _
        | ArrayShape _ ->
            let compare = expr (Lambda([ partName 0, None; otherPartName 0, None ], same (name (partName 0)) (name (otherPartName 0)), false))
            let helper =
                match shape with
                | MapShape _ -> EqualityOperators.mapFunction
                | SetShape _ -> EqualityOperators.setFunction
                | ArrayShape _ -> EqualityOperators.arrayFunction
                | _ -> EqualityOperators.listFunction
            expr (FunctionCall(helper, None, [ value; other; compare ]))
        | MeasureShape _ -> invalidOp "Eq is never derived for a measured value, which is compared as its storage (see comparedShapeOf)"

    /// The prelude functions that hash a value through its `Hash`, make two hashes one, and hash a list, a map and a set
    /// through their parts' hashes.
    let private hashOfFunction, hashCombine, listHash = "__hashOf", "__hashCombine", "__listHashBy"
    let private mapHash, setHash = "__mapHashBy", "__setHashBy"

    /// The hash of `value`, a value of the type `shape` is, as a derived `hash` works it out: each part hashed with
    /// `__hashOf`, which finds the part's own `Hash`, and the hashes folded with `__hashCombine`, after the tag's name for a
    /// tag, so that values a derived `==` finds equal have the same hash.
    let private hashBody (span: SourceSpan) (shape: Shape) : Expression =
        let expr = Expression.at span
        let name (identifier: Identifier) = expr (IdentifierExpr identifier)
        let field (target: Expression) (fieldName: Identifier) = expr (MemberAccess(target, fieldName, None))
        let pattern = Pattern.at span
        let hashOf (part: Expression) = expr (FunctionCall(hashOfFunction, None, [ part ]))
        let builtIn (value: Expression) = expr (FunctionCall(WellKnownContexts.hashing.BuiltInFunction, None, [ value ]))
        let folded (seed: Expression) (parts: Expression list) =
            parts |> List.fold (fun hash part -> expr (FunctionCall(hashCombine, None, [ hash; hashOf part ]))) seed
        let seed = builtIn (expr (LiteralExpr (IntLit 0I)))
        let positions (count: int) = pattern (TuplePattern(List.init count (partName >> IdentifierPattern >> pattern), false))
        let value = name valueName
        match shape with
        | TagShape tags ->
            let arm (tagName: string, payload: Ty option) : MatchArm =
                let tag (payloadPattern: PatternKind option) = pattern (TagPattern(tagName, payloadPattern |> Option.map pattern))
                let named = builtIn (expr (LiteralExpr (StringLit $"#{tagName}")))
                match payload with
                | None -> [ tag None ], named
                | Some (TyPrimitive "unit") -> [ tag (Some UnitPattern) ], named
                | Some _ -> [ tag (Some (IdentifierPattern (partName 0))) ], folded named [ name (partName 0) ]
            expr (Match([ value ], tags |> List.map arm))
        | TupleShape items ->
            expr (Match([ value ], [ [ positions items.Length ], folded seed (List.init items.Length (partName >> name)) ]))
        | RecordShape fields -> folded seed (fields |> List.map (fun (fieldName, _) -> field value fieldName))
        | ListShape _ ->
            let hashPart = expr (Lambda([ partName 0, None ], hashOf (name (partName 0)), false))
            expr (FunctionCall(listHash, None, [ seed; value; hashPart ]))
        // A map's or a set's entries in no order: equal maps have the same hash however their keys were added.
        | MapShape _ ->
            let hashPart () = expr (Lambda([ partName 0, None ], hashOf (name (partName 0)), false))
            expr (FunctionCall(mapHash, None, [ value; hashPart (); hashPart () ]))
        | SetShape _ ->
            let hashPart = expr (Lambda([ partName 0, None ], hashOf (name (partName 0)), false))
            expr (FunctionCall(setHash, None, [ value; hashPart ]))
        | OpenTagShape _
        | ArrayShape _
        | MeasureShape _ -> invalidOp "Hash is never derived for an array, an open union or a measured value (see WellKnownContexts.hashing)"

    /// The prelude functions that order two values through their `Ord`, and two lists through their elements'.
    let private compareOf, listCompare = "__compareOf", "__listCompare"

    /// The tags an order is given as.
    let private orderTags = [ "less"; "equal"; "greater" ]

    /// How `value` and `other`, two values of the type `shape` is, are ordered, as a derived `compare` works it out: a
    /// tuple position by position, each part with `__compareOf`, which finds the part's own, going on to the next while they
    /// are `#equal`; a list element by element with `__listCompare`, which does the same (#2748).
    let private compareBody (span: SourceSpan) (shape: Shape) : Expression =
        let expr = Expression.at span
        let name (identifier: Identifier) = expr (IdentifierExpr identifier)
        let pattern = Pattern.at span
        let compare (left: Expression) (right: Expression) = expr (FunctionCall(compareOf, None, [ left; right ]))
        let positions (partName: int -> Identifier) (count: int) = pattern (TuplePattern(List.init count (partName >> IdentifierPattern >> pattern), false))
        let value, other = name valueName, name otherName
        match shape with
        | TupleShape items ->
            let orderAt (index: int) = compare (name (partName index)) (name (otherPartName index))
            let ordered =
                List.init (items.Length - 1) id
                |> List.rev
                |> List.fold
                    (fun later index ->
                        let decided = $"__order{index}"
                        expr (Match([ orderAt index ], [ [ pattern (TagPattern("equal", None)) ], later; [ pattern (IdentifierPattern decided) ], name decided ])))
                    (orderAt (items.Length - 1))
            expr (Match([ value; other ], [ [ positions partName items.Length; positions otherPartName items.Length ], ordered ]))
        | ListShape _ -> expr (FunctionCall(listCompare, None, [ value; other ]))
        | _ -> invalidOp "Ord is derived only for a tuple or a list (see orderedShapeOf)"

    /// The definition the compiler writes for the instance of `ctxName` of the type written `written`, which
    /// requires `ctxName` of each of `varNames`: its name is `name`, and it is reported at `span`.
    let private definition (span: SourceSpan) (ctxName: Identifier) (name: Identifier) (written: TypeExpr) (varNames: string list) (shape: Shape) : TopLevelItem =
        let typeExpr = TypeExpr.at span
        let expr = Expression.at span
        let signature, body =
            if ctxName = EqualityOperators.context then
                typeExpr (TypeFunc(typeExpr (TypeTuple [ written; written ]), typeExpr (TypeName "bool"))),
                expr (Lambda([ valueName, None; otherName, None ], equalsBody span shape, false))
            elif ctxName = WellKnownContexts.hashing.Context then
                typeExpr (TypeFunc(written, typeExpr (TypeName "int"))), expr (Lambda([ valueName, None ], hashBody span shape, false))
            elif ctxName = WellKnownContexts.ordering.Context then
                let order = typeExpr (TypeUnion(orderTags |> List.map (fun tag -> typeExpr (TypeTag(tag, None)))))
                typeExpr (TypeFunc(typeExpr (TypeTuple [ written; written ]), order)),
                expr (Lambda([ valueName, None; otherName, None ], compareBody span shape, false))
            else
                typeExpr (TypeFunc(written, typeExpr (TypeName "string"))), expr (Lambda([ valueName, None ], toStringBody span shape, false))
        let annotation =
            match varNames with
            | [] -> signature
            | _ ->
                let contexts = varNames |> List.map (fun var -> typeExpr (TypeApply(ctxName, [ typeExpr (TypeVar var) ])))
                typeExpr (TypeWithContext(contexts, signature))
        TopLevelItem.at span (Def (ValueDef(false, name, Some annotation, body)))

    /// The name `state`'s module sees a type by, of those `fits` says are it: `name` if the module sees it by that
    /// name, else the first other name it sees it by, as an import's alias qualifies it (`lib.Pair`). A name only an
    /// aliased import brings in is not seen (see `tryFindTypeDef`).
    let private seenName (state: InferState) (fits: TypeDefInfo -> bool) (name: string) : string option =
        let seen key info = fits info && not (state.HiddenTypeNames.Contains key)
        match state.TypeDefs |> Map.tryFind name with
        | Some info when seen name info -> Some name
        | _ -> state.TypeDefs |> Map.tryPick (fun key info -> if seen key info then Some key else None)

    /// Whether `info` defines the nominal type `nominal`.
    let private definesNominal (nominal: NominalId) (info: TypeDefInfo) = TypeDefInfo.nominalId info = nominal

    /// Whether `info` defines the alias defined as `name`.
    let private definesAlias (name: string) (info: TypeDefInfo) = not info.IsNominal && info.Name = name

    /// `ty` as the annotation of a definition derived in `state`'s module writes it, each type it names by a name the
    /// module sees it by (see `seenName`), with its variables named by `names`.
    let private writtenIn (state: InferState) (names: Map<int, string>) (ty: Ty) : TypeExpr option =
        let nameOf fits name = seenName state fits name |> Option.defaultValue name
        ContextInference.typeExprWith (fun nominal -> nameOf (definesNominal nominal) nominal.Name) (fun alias -> nameOf (definesAlias alias) alias) names ty

    /// `state` with a made-up name the module sees for each type `ty` names that it sees by no name, as one a selective
    /// import left out, so the definition derived for `ty` can write it (see `writtenIn`).
    let private namingUnseen (names: Map<int, string>) (ty: Ty) (state: InferState) : InferState =
        let mutable unseen: (string * TypeDefInfo) list = []
        let note fits name =
            if (seenName state fits name).IsNone then
                state.TypeDefs |> Map.tryPick (fun _ info -> if fits info then Some info else None) |> Option.iter (fun info -> unseen <- (name, info) :: unseen)
            name
        ContextInference.typeExprWith (fun nominal -> note (definesNominal nominal) nominal.Name) (fun alias -> note (definesAlias alias) alias) names ty |> ignore
        { state with TypeDefs = unseen |> List.distinctBy fst |> List.fold (fun typeDefs (name, info) -> typeDefs |> Map.add $"__{name}_{NodeId.next ()}" info) state.TypeDefs }
    /// The definition that is the derived instance of `ctxName` for a value of the type `ty`, if one can be derived
    /// (see `derivable`), with the variables of `ty` it requires the context of, in the order it requires them. It
    /// is written the first time it is asked for, to be inferred with the module's other definitions, and is
    /// reported at `span`.
    let private derive (rigid: Set<int>) (span: SourceSpan) (ctxName: Identifier) (ty: Ty) (state: InferState) : (Identifier * TyVar list * InferState) option =
        derivable rigid ctxName ty
        |> Option.bind (fun (ty, shape, vars) ->
            let shape =
                match shape with
                | MeasureShape([ dimension, 1 ], storage, _) -> MeasureShape([ dimension, 1 ], storage, Units.seenOf state [ dimension, 1 ])
                // A derived dimension is written in the default display units of its parts, or else in their default
                // storage units.
                | MeasureShape(dimension, storage, _) ->
                    let parts = dimension |> List.collect (fun (id, _) -> Units.seenOf state (Dimension.ofBase id))
                    MeasureShape(dimension, storage, parts |> List.filter (fun info -> UnitInfo.isStorage info || info.IsDisplay))
                | _ -> shape
            let names = ContextInference.varNames ty
            let state = namingUnseen names ty state
            writtenIn state names ty
            |> Option.map (fun written ->
                match state.DerivedInstances |> Map.tryFind (ctxName, written) with
                | Some name -> name, vars, state
                | None ->
                    // No two modules of a program write a definition of the same name.
                    let prefix =
                        if ctxName = EqualityOperators.context then EqualityOperators.builtInFunction
                        elif ctxName = WellKnownContexts.hashing.Context then WellKnownContexts.hashing.BuiltInFunction
                        elif ctxName = WellKnownContexts.ordering.Context then WellKnownContexts.ordering.BuiltInFunction
                        else "__toString"
                    let name = $"{prefix}_{NodeId.next ()}"
                    let item = definition span ctxName name written (vars |> List.map (fun var -> names.[var.Id])) shape
                    name, vars,
                    { state with
                        DerivedInstances = state.DerivedInstances |> Map.add (ctxName, written) name
                        DerivedDefs = state.DerivedDefs @ [ item ] }))

    /// `ty` with each part of it, at any depth, that is not a type variable and that `partInstance` finds an instance
    /// in scope of (one a `use` in a block loads) made a variable of its own, and the instance each such variable
    /// stands for. A definition derived for the type that comes out requires the context of each, so the part is
    /// written, or compared, through the instance in scope where the call is, not through the one the top of the
    /// module would find. A nominal type, an alias and a function are not looked inside.
    let private abstractParts (partInstance: Ty -> (TypeExpr * ContextProvider) option) (ty: Ty) (state: InferState) : Ty * Map<int, TypeExpr * ContextProvider> * InferState =
        let rec part (found: Map<Ty, TyVar * (TypeExpr * ContextProvider)>, state: InferState) (ty: Ty) =
            match ty, found |> Map.tryFind ty with
            | TyVar _, _ -> ty, (found, state)
            | _, Some (var, _) -> TyVar var, (found, state)
            | _ ->
                match partInstance ty with
                | Some inScope ->
                    match freshVar state with
                    | TyVar var, next -> TyVar var, (found |> Map.add ty (var, inScope), next)
                    | _ -> inside (found, state) ty
                | None -> inside (found, state) ty
        and inside (acc: Map<Ty, TyVar * (TypeExpr * ContextProvider)> * InferState) (ty: Ty) =
            match ty with
            | TyTag(name, Some payload) ->
                let payload, acc = part acc payload
                TyTag(name, Some payload), acc
            | TyUnion items ->
                let items, acc = items |> List.mapFold part acc
                TyUnion items, acc
            | TyRecord(fields, order) ->
                let names, tys = fields |> Map.toList |> List.unzip
                let tys, acc = tys |> List.mapFold part acc
                TyRecord(List.zip names tys |> Map.ofList, order), acc
            | Lists.ListType element ->
                let element, acc = part acc element
                Lists.ofType element, acc
            | Sets.SetType element ->
                let element, acc = part acc element
                Sets.ofType element, acc
            | Arrays.ArrayType element ->
                let element, acc = part acc element
                Arrays.ofType element, acc
            | Maps.MapType(key, value) ->
                let key, acc = part acc key
                let value, acc = part acc value
                Maps.ofTypes key value, acc
            | _ -> ty, acc
        let abstracted, (found, next) = inside (Map.empty, state) ty
        if found.IsEmpty then ty, Map.empty, state
        else abstracted, found |> Map.values |> Seq.map (fun (var, inScope) -> var.Id, inScope) |> Map.ofSeq, next

    /// `instanceOf`, and the instance in scope each variable `abstractParts` made stands for.
    let private withAbstracted (abstracted: Map<int, TypeExpr * ContextProvider>) (instanceOf: TyVar -> ContextInstance option) (var: TyVar) =
        match abstracted |> Map.tryFind var.Id with
        | Some inScope -> Some (InstanceInScope inScope)
        | None -> instanceOf var

    /// Why a call does not find the instance derived for a type.
    type Failure =
        /// Nothing in scope is the context of this variable, which the derived instance requires.
        | NoInstanceOf of TyVar
        /// A part of the type is this function, and no function has an instance.
        | HoldsFunction of Ty
        /// The type holds a union open to tags the caller of the function around the call chooses, which may be any.
        | OpenToCaller

    /// Where the call `call`, at `span`, that requires `ctxName` of the type `ty` finds it, if an instance can be
    /// derived for the type. For a type with no variables it is the derived definition, as the context's member. For
    /// one with variables, the derived definition is passed the instance `instanceOf` finds for each variable: a hole
    /// (`isHole`) calls it in place of the hole function, and any other call is passed it as the member of an
    /// `InstanceDerived`. A part with an instance in scope that `partInstance` finds is written through it (see
    /// `abstractParts`). `rigid` holds the variables of the function around the call.
    let instance
        (rigid: Set<int>)
        (partInstance: Ty -> (TypeExpr * ContextProvider) option)
        (instanceOf: TyVar -> ContextInstance option)
        (isHole: bool)
        (call: NodeId)
        (span: SourceSpan)
        (ctxName: Identifier)
        (ty: Ty)
        (state: InferState)
        : Result<ContextInstance * InferState, Failure> option =
        // A function with an instance in scope is a part like any other, written or compared through it.
        let abstractedTy, abstracted, abstractedState = abstractParts partInstance ty state
        match functionPart abstractedTy with
        | Some held -> Some (Error (HoldsFunction held))
        // A union open to the caller's tags has only an `==` derived, which compares the tags it names part by part and
        // leaves the others to the compiler's own, as a comparison of it does (see `comparison`), and a call that requires
        // `Eq` of it is passed that (#2751).
        | None when (closedFor ctxName rigid ty).IsNone && (closed Set.empty ty |> Option.bind (shapeFor ctxName)).IsSome -> Some (Error OpenToCaller)
        | None ->
            let ty, state = abstractedTy, abstractedState
            let instanceOf = withAbstracted abstracted instanceOf
            derive rigid span ctxName ty state
            |> Option.map (fun (name, vars, next) ->
                match vars, vars |> List.tryFind (instanceOf >> Option.isNone) with
                | [], _ -> Ok (InstanceSynthesized [ memberOf ctxName, name ], next)
                | _, Some missing -> Error (NoInstanceOf missing)
                | _, None ->
                    let instances = vars |> List.map instanceOf
                    if isHole then Ok (InstanceBuiltIn, { next with DerivedCalls = next.DerivedCalls |> Map.add call (name, instances) })
                    else
                        let at = NodeId.next ()
                        Ok (InstanceDerived(memberOf ctxName, name, at), { next with ContextInstances = next.ContextInstances |> Map.add at instances }))

    /// The `==` derived for the type `ty`, which the comparison `call`, at `span`, of two values of it calls, and
    /// `state` with the comparison finding the instance `instanceOf` finds for each variable of the type. `None` if
    /// none can be derived, or a variable has no instance: the comparison is then the compiler's own. `rigid` and
    /// `partInstance` are as `instance` has them.
    let comparison
        (rigid: Set<int>)
        (partInstance: Ty -> (TypeExpr * ContextProvider) option)
        (instanceOf: TyVar -> ContextInstance option)
        (call: NodeId)
        (span: SourceSpan)
        (ty: Ty)
        (state: InferState)
        : (Identifier * InferState) option =
        let ty, abstracted, state = abstractParts partInstance ty state
        let instanceOf = withAbstracted abstracted instanceOf
        derive rigid span EqualityOperators.context ty state
        |> Option.bind (fun (name, vars, next) ->
            if vars |> List.forall (instanceOf >> Option.isSome) then
                let instances = vars |> List.map instanceOf
                Some (name, { next with DerivedCalls = next.DerivedCalls |> Map.add call (name, instances) })
            else None)

    /// The definitions written since they were last taken, to be inferred, and the state without them.
    let takeDefinitions (state: InferState) : TopLevelItem list * InferState =
        state.DerivedDefs, { state with DerivedDefs = [] }

    /// `state` with each hole and comparison that calls a derived definition finding the instances that definition
    /// requires.
    let callInstances (state: InferState) : InferState =
        { state with
            ContextInstances =
                state.DerivedCalls
                |> Map.fold (fun instances call (_, found) -> if found.IsEmpty then instances else instances |> Map.add call found) state.ContextInstances }

    /// The definition each call is written as a call to for the backend: a pipe's attached definition, and the
    /// derived definition a hole calls in place of the hole function. A comparison is not a call until it is
    /// written as one (see `InferState.OperatorCalls`), so its name here changes nothing.
    let callTargets (state: InferState) : Map<NodeId, Identifier> =
        state.DerivedCalls |> Map.fold (fun targets call (name, _) -> targets |> Map.add call name) state.PipeTargets
