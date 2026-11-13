namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// Instances of `ToString`, `Eq` and `Hash` the compiler derives from a type's structure, part by part (see "Printing values"
/// and "Equality and well-known contexts" in the docs' work in progress).
///
/// A tag union, a tuple, a record, a list, a map or a set has no `toString` attached, so a hole of such a
/// type, or a call that requires `ToString` of it, finds an instance written from its parts: `#some(a)` is written
/// `#some(` then its payload then `)`, and the payload is written through its own `ToString`, whichever that is.
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
/// A comparison of a union with a rest the function around chooses, `#some($Email) | r`, is derived the same way, with
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
        /// A map's key and value types. `Eq` and `ToString` are derived for a map, or a set; `Hash` is not.
        | MapShape of Ty * Ty
        | SetShape of Ty

    let private isTuple (fields: Map<string, Ty>) = not fields.IsEmpty && WellKnownContexts.isTuple fields

    let private tupleItems (fields: Map<string, Ty>) = List.init fields.Count (fun index -> fields.[string index])

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
        | TyNominal _ -> Some ty
        | TyAlias(name, args, underlying) ->
            match all args, closed rigid underlying with
            | Some args, Some underlying -> Some (TyAlias(name, args, underlying))
            | _ -> None
        | TyApply(name, args) -> all args |> Option.map (fun args -> TyApply(name, args))
        | TyFunc(arg, ret, contexts) -> all [ arg; ret ] |> Option.map (fun parts -> TyFunc(parts.[0], parts.[1], contexts))
        | TyRecord fields -> fieldsOf fields |> Option.map TyRecord
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
        | TyRecord fields when isTuple fields -> Some (TupleShape (tupleItems fields))
        | TyRecord fields when not fields.IsEmpty -> Some (RecordShape (Map.toList fields))
        | Lists.ListType element -> Some (ListShape element)
        | _ -> None

    /// What `ty` is built from, as far as comparing two values of it goes: a map and a set are structures too, which
    /// the compiler's own comparison cannot compare, as it would compare the order their entries were added in.
    /// A union with a rest left in it, as `closedFor` leaves one for `Eq`, is one too.
    let private comparedShapeOf (ty: Ty) : Shape option =
        match Unifier.unalias ty with
        | Maps.MapType(key, value) -> Some (MapShape(key, value))
        | Sets.SetType element -> Some (SetShape element)
        | TyUnion items when items |> List.exists (function TyVar _ -> true | _ -> false) ->
            items |> List.filter (function TyVar _ -> false | _ -> true) |> tagsOf |> Option.map OpenTagShape
        | _ -> shapeOf ty

    /// What `ty` is built from, as far as writing it as text goes: a map is written entry by entry, and a set element
    /// by element.
    let private writtenShapeOf (ty: Ty) : Shape option =
        match Unifier.unalias ty with
        | Maps.MapType(key, value) -> Some (MapShape(key, value))
        | Sets.SetType element -> Some (SetShape element)
        | _ -> shapeOf ty

    /// The shape an instance of `ctxName` is derived from.
    let private shapeFor (ctxName: Identifier) =
        if ctxName = EqualityOperators.context then comparedShapeOf
        elif ctxName = InterpolationHoles.context then writtenShapeOf
        else shapeOf

    let private partsOf (shape: Shape) : Ty list =
        match shape with
        | TagShape tags
        | OpenTagShape tags -> tags |> List.choose snd
        | TupleShape items -> items
        | RecordShape fields -> fields |> List.map snd
        | ListShape element
        | SetShape element -> [ element ]
        | MapShape(key, value) -> [ key; value ]

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

    /// Whether `ty` is a map or a set, which only a derived `==` compares.
    let private isCollection (ty: Ty) =
        match comparedShapeOf ty with
        | Some (MapShape _ | SetShape _) -> true
        | _ -> false

    /// Whether `ty` is a map or a set, or holds one at any depth, so that only a derived `==` compares it.
    let holdsCollection (ty: Ty) : bool =
        (ty |> tryPickPart comparedShapeOf (fun part -> if isCollection part then Some () else None)).IsSome

    /// Whether comparing two values of the structure `ty` compares a part, at any depth, that `hasOwn` says has an
    /// `==` the compiler's own comparison would not read, or that is a map or a set, or is one itself. `rigid` is as
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
                    | Some (TyRecord fields) when isTuple fields ->
                        Some (pattern (TuplePattern (positions fields.Count))), bracketed $"#{tagName}(" (positional (tupleItems fields))
                    | Some (TyRecord fields) when not fields.IsEmpty ->
                        Some whole, bracketed $"#{tagName}(" (named (name (partName 0)) (Map.toList fields))
                    | Some (TyPrimitive "unit") -> Some (pattern UnitPattern), text [ StringText $"#{tagName}()" ]
                    | Some payload -> Some whole, bracketed $"#{tagName}(" [ part payload (name (partName 0)) ]
                [ pattern (TagPattern(tagName, payloadPattern)) ], written
            expr (Match([ value ], tags |> List.map arm))
        | TupleShape items -> expr (Match([ value ], [ [ pattern (TuplePattern (positions items.Length)) ], bracketed "(" (positional items) ]))
        | RecordShape fields -> bracketed "(" (named value fields)
        // A collection is written by a prelude function, passed what writes each part.
        | ListShape _
        | SetShape _
        | MapShape _ ->
            let write (index: int) =
                let element = partName index
                expr (Lambda([ element, None ], text [ StringExpr (expr (FunctionCall(InterpolationHoles.holeFunction, None, [ name element ]))) ], false))
            let helper, writers =
                match shape with
                | MapShape _ -> InterpolationHoles.mapFunction, [ write 0; write 1 ]
                | SetShape _ -> InterpolationHoles.setFunction, [ write 0 ]
                | _ -> InterpolationHoles.listFunction, [ write 0 ]
            expr (FunctionCall(helper, None, value :: writers))
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
        let positions (partName: int -> Identifier) (count: int) = pattern (TuplePattern (List.init count (partName >> IdentifierPattern >> pattern)))
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
        | SetShape _ ->
            let compare = expr (Lambda([ partName 0, None; otherPartName 0, None ], same (name (partName 0)) (name (otherPartName 0)), false))
            let helper =
                match shape with
                | MapShape _ -> EqualityOperators.mapFunction
                | SetShape _ -> EqualityOperators.setFunction
                | _ -> EqualityOperators.listFunction
            expr (FunctionCall(helper, None, [ value; other; compare ]))

    /// The prelude functions that hash a value through its `Hash`, make two hashes one, and hash a list through its
    /// elements' hashes.
    let private hashOfFunction, hashCombine, listHash = "__hashOf", "__hashCombine", "__listHashBy"

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
        let positions (count: int) = pattern (TuplePattern (List.init count (partName >> IdentifierPattern >> pattern)))
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
        | OpenTagShape _
        | MapShape _
        | SetShape _ -> invalidOp "Hash is never derived for a map, a set or an open union (see shapeFor)"

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
            else
                typeExpr (TypeFunc(written, typeExpr (TypeName "string"))), expr (Lambda([ valueName, None ], toStringBody span shape, false))
        let annotation =
            match varNames with
            | [] -> signature
            | _ ->
                let contexts = varNames |> List.map (fun var -> typeExpr (TypeApply(ctxName, [ typeExpr (TypeVar var) ])))
                typeExpr (TypeWithContext(contexts, signature))
        TopLevelItem.at span (Def (ValueDef(false, name, Some annotation, body)))

    /// The definition that is the derived instance of `ctxName` for a value of the type `ty`, if one can be derived
    /// (see `derivable`), with the variables of `ty` it requires the context of, in the order it requires them. It
    /// is written the first time it is asked for, to be inferred with the module's other definitions, and is
    /// reported at `span`.
    let private derive (rigid: Set<int>) (span: SourceSpan) (ctxName: Identifier) (ty: Ty) (state: InferState) : (Identifier * TyVar list * InferState) option =
        derivable rigid ctxName ty
        |> Option.bind (fun (ty, shape, vars) ->
            let names = ContextInference.varNames ty
            ContextInference.typeExprOf names ty
            |> Option.map (fun written ->
                match state.DerivedInstances |> Map.tryFind (ctxName, written) with
                | Some name -> name, vars, state
                | None ->
                    // No two modules of a program write a definition of the same name.
                    let prefix =
                        if ctxName = EqualityOperators.context then EqualityOperators.builtInFunction
                        elif ctxName = WellKnownContexts.hashing.Context then WellKnownContexts.hashing.BuiltInFunction
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
            | TyRecord fields ->
                let names, tys = fields |> Map.toList |> List.unzip
                let tys, acc = tys |> List.mapFold part acc
                TyRecord (List.zip names tys |> Map.ofList), acc
            | Lists.ListType element ->
                let element, acc = part acc element
                Lists.ofType element, acc
            | Sets.SetType element ->
                let element, acc = part acc element
                Sets.ofType element, acc
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
        match functionPart ty with
        | Some held -> Some (Error (HoldsFunction held))
        // A union open to the caller's tags has an `==` derived only where it is compared (see `comparison`).
        | None when (closed rigid ty).IsNone && (closed Set.empty ty |> Option.bind (shapeFor ctxName)).IsSome -> Some (Error OpenToCaller)
        | None ->
            let ty, abstracted, state = abstractParts partInstance ty state
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
