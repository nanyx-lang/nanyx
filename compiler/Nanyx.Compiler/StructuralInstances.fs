namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// Instances of `ToString` and `Eq` the compiler derives from a type's structure, part by part (see "Printing values"
/// and "Equality and well-known contexts" in the docs' work in progress).
///
/// A tag union, a tuple, a record or a list has no `toString` attached, so a hole of such a
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
/// instances in scope where the hole is (see `InferState.DerivedCalls`).
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
module internal StructuralInstances =
    /// Whether a call that requires the context `ctxName` of a structure with no instance of its own finds a derived
    /// one: `ToString`. `Eq` has the compiler's own, and a derived one only as `comparesParts` says.
    let derives (ctxName: Identifier) = ctxName = InterpolationHoles.context

    /// The member of the context `ctxName` a derived definition is.
    let private memberOf (ctxName: Identifier) =
        if ctxName = EqualityOperators.context then EqualityOperators.equals else InterpolationHoles.memberName

    /// What a type is built from, as far as writing a value of it goes.
    type private Shape =
        /// A union of tags, or one tag: each tag's name with its payload's type.
        | TagShape of (string * Ty option) list
        | TupleShape of Ty list
        /// A record's named fields, in the order they are written out in: by name.
        | RecordShape of (string * Ty) list
        | ListShape of Ty

    let private isTuple (fields: Map<string, Ty>) = not fields.IsEmpty && WellKnownContexts.isTuple fields

    let private tupleItems (fields: Map<string, Ty>) = List.init fields.Count (fun index -> fields.[string index])

    /// `ty` without the variables that leave its tag unions open to more tags, at any depth: by the time an instance
    /// is derived, such a union holds only the tags it names. `None` if one of them is in `rigid`, the variables of
    /// the function around that stand for a type its caller chooses, which may hold any tag.
    let rec private closed (rigid: Set<int>) (ty: Ty) : Ty option =
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
            if rests |> List.exists (function TyVar v -> rigid.Contains v.Id | _ -> false) then None
            else all named |> Option.map TyUnion

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
        | TyApply("list", [ element ]) -> Some (ListShape element)
        | _ -> None

    let private partsOf (shape: Shape) : Ty list =
        match shape with
        | TagShape tags -> tags |> List.choose snd
        | TupleShape items -> items
        | RecordShape fields -> fields |> List.map snd
        | ListShape element -> [ element ]

    /// The part of the structure `ty`, at any depth, that is a function, if one is.
    let rec private functionPart (ty: Ty) : Ty option =
        match Unifier.unalias ty with
        | TyFunc _ -> Some ty
        | _ -> shapeOf ty |> Option.bind (partsOf >> List.tryPick functionPart)

    /// Whether comparing two values of the structure `ty` compares a part, at any depth, that `hasOwn` says has an
    /// `==` the compiler's own comparison would not read. `rigid` is as `closed` has it.
    let comparesParts (rigid: Set<int>) (hasOwn: Ty -> bool) (ty: Ty) : bool =
        let rec inParts (ty: Ty) =
            shapeOf ty |> Option.exists (partsOf >> List.exists (fun part -> hasOwn part || inParts part))
        closed rigid ty |> Option.exists inParts

    /// The type an instance is derived for and the variables it requires the context of, for a value of the type
    /// `ty`: `ty` closed (see `closed`), if it is a structure, with its variables in the order they appear. `None`
    /// for a type with a variable an annotation could not name, one inside a nominal type.
    let private derivable (rigid: Set<int>) (ty: Ty) : (Ty * Shape * TyVar list) option =
        closed rigid ty
        |> Option.bind (fun ty ->
            let vars = ContextInference.varsInOrder ty
            match shapeOf ty with
            | Some shape when TypeEnv.freeTyVars ty = (vars |> List.map _.Id |> Set.ofList) -> Some (ty, shape, vars)
            | _ -> None)

    /// The variables an instance derived for the type `ty` requires the context of, if one can be derived (see
    /// `derivable`).
    let variables (rigid: Set<int>) (ty: Ty) : TyVar list option =
        derivable rigid ty |> Option.map (fun (_, _, vars) -> vars)

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
        | ListShape _ ->
            let element = partName 0
            let write = expr (Lambda([ element, None ], text [ StringExpr (expr (FunctionCall(InterpolationHoles.holeFunction, None, [ name element ]))) ]))
            expr (FunctionCall(InterpolationHoles.listFunction, None, [ value; write ]))

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
        let same (left: Expression) (right: Expression) = expr (BinaryOp(EqualityOperators.equals, left, right))
        let all (comparisons: Expression list) =
            match comparisons with
            | [] -> answer true
            | first :: rest -> rest |> List.fold (fun both next -> expr (BinaryOp("and", both, next))) first
        let positions (partName: int -> Identifier) (count: int) = pattern (TuplePattern (List.init count (partName >> IdentifierPattern >> pattern)))
        let value, other = name valueName, name otherName
        match shape with
        | TagShape tags ->
            let arm (tagName: string, payload: Ty option) : MatchArm =
                let tag (payloadPattern: PatternKind option) = pattern (TagPattern(tagName, payloadPattern |> Option.map pattern))
                match payload with
                | None -> [ tag None; tag None ], answer true
                | Some (TyPrimitive "unit") -> [ tag (Some UnitPattern); tag (Some UnitPattern) ], answer true
                | Some _ ->
                    [ tag (Some (IdentifierPattern (partName 0))); tag (Some (IdentifierPattern (otherPartName 0))) ],
                    same (name (partName 0)) (name (otherPartName 0))
            // Two values of different tags are not equal.
            let different: MatchArm list =
                if tags.Length > 1 then [ [ pattern WildcardPattern; pattern WildcardPattern ], answer false ] else []
            expr (Match([ value; other ], (tags |> List.map arm) @ different))
        | TupleShape items ->
            let parts = List.init items.Length (fun index -> same (name (partName index)) (name (otherPartName index)))
            expr (Match([ value; other ], [ [ positions partName items.Length; positions otherPartName items.Length ], all parts ]))
        | RecordShape fields -> fields |> List.map (fun (fieldName, _) -> same (field value fieldName) (field other fieldName)) |> all
        | ListShape _ ->
            let compare = expr (Lambda([ partName 0, None; otherPartName 0, None ], same (name (partName 0)) (name (otherPartName 0))))
            expr (FunctionCall(EqualityOperators.listFunction, None, [ value; other; compare ]))

    /// The definition the compiler writes for the instance of `ctxName` of the type written `written`, which
    /// requires `ctxName` of each of `varNames`: its name is `name`, and it is reported at `span`.
    let private definition (span: SourceSpan) (ctxName: Identifier) (name: Identifier) (written: TypeExpr) (varNames: string list) (shape: Shape) : TopLevelItem =
        let typeExpr = TypeExpr.at span
        let expr = Expression.at span
        let signature, body =
            if ctxName = EqualityOperators.context then
                typeExpr (TypeFunc(typeExpr (TypeTuple [ written; written ]), typeExpr (TypeName "bool"))),
                expr (Lambda([ valueName, None; otherName, None ], equalsBody span shape))
            else
                typeExpr (TypeFunc(written, typeExpr (TypeName "string"))), expr (Lambda([ valueName, None ], toStringBody span shape))
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
        derivable rigid ty
        |> Option.bind (fun (ty, shape, vars) ->
            let names = ContextInference.varNames ty
            ContextInference.typeExprOf names ty
            |> Option.map (fun written ->
                match state.DerivedInstances |> Map.tryFind (ctxName, written) with
                | Some name -> name, vars, state
                | None ->
                    // No two modules of a program write a definition of the same name.
                    let prefix = if ctxName = EqualityOperators.context then EqualityOperators.builtInFunction else "__toString"
                    let name = $"{prefix}_{NodeId.next ()}"
                    let item = definition span ctxName name written (vars |> List.map (fun var -> names.[var.Id])) shape
                    name, vars,
                    { state with
                        DerivedInstances = state.DerivedInstances |> Map.add (ctxName, written) name
                        DerivedDefs = state.DerivedDefs @ [ item ] }))

    /// Why a call does not find the instance derived for a type with variables in it.
    type Failure =
        /// Nothing in scope is the context of this variable, which the derived instance requires.
        | NoInstanceOf of TyVar
        /// The call is not a hole's: only a hole can call the derived definition with the instances it requires.
        | NotAHole
        /// A part of the type is this function, and no function has an instance.
        | HoldsFunction of Ty

    /// The error for a call by `requirer` (see `InterpolationHoles.requirer`) that requires `ctxName` of the type
    /// `ty`, which has variables in it, and is not a hole's.
    let notAHole (requirer: string) (ctxName: Identifier) (ty: Ty) =
        let ctxText = $"{ctxName}({Unifier.tyToDisplayString Ascii ty})"
        $"{requirer} requires the context '{ctxText}', which the compiler derives from the parts of the type, but it cannot yet pass a function the one for a type with type variables in it: turn the value into a string with a hole first, as in \"{{value}}\""

    /// Where the call `call`, at `span`, that requires `ctxName` of the type `ty` finds it, if an instance can be
    /// derived for the type. For a type with no variables it is the derived definition, as the context's member. For
    /// one with variables, the call must be a hole (`isHole`), which calls the derived definition in place of the
    /// hole function, with the instance `instanceOf` finds for each variable. `rigid` holds the variables of the
    /// function around the call.
    let instance
        (rigid: Set<int>)
        (instanceOf: TyVar -> TypeExpr option)
        (isHole: bool)
        (call: NodeId)
        (span: SourceSpan)
        (ctxName: Identifier)
        (ty: Ty)
        (state: InferState)
        : Result<ContextInstance * InferState, Failure> option =
        match functionPart ty, variables rigid ty with
        | Some held, _ -> Some (Error (HoldsFunction held))
        | None, Some (_ :: _) when not isHole -> Some (Error NotAHole)
        | None, _ ->
            derive rigid span ctxName ty state
            |> Option.map (fun (name, vars, next) ->
                match vars, vars |> List.tryFind (instanceOf >> Option.isNone) with
                | [], _ -> Ok (InstanceSynthesized [ memberOf ctxName, name ], next)
                | _, Some missing -> Error (NoInstanceOf missing)
                | _, None ->
                    let instances = vars |> List.map (instanceOf >> Option.map InstanceInScope)
                    Ok (InstanceBuiltIn, { next with DerivedCalls = next.DerivedCalls |> Map.add call (name, instances) }))

    /// The `==` derived for the type `ty`, which the comparison `call`, at `span`, of two values of it calls, and
    /// `state` with the comparison finding the instance `instanceOf` finds for each variable of the type. `None` if
    /// none can be derived, or a variable has no instance: the comparison is then the compiler's own. `rigid` is as
    /// `instance` has it.
    let comparison (rigid: Set<int>) (instanceOf: TyVar -> TypeExpr option) (call: NodeId) (span: SourceSpan) (ty: Ty) (state: InferState) : (Identifier * InferState) option =
        derive rigid span EqualityOperators.context ty state
        |> Option.bind (fun (name, vars, next) ->
            if vars |> List.forall (instanceOf >> Option.isSome) then
                let instances = vars |> List.map (instanceOf >> Option.map InstanceInScope)
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
