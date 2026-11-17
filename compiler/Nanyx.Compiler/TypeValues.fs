namespace NyxCompiler

/// The built-in `Type(a)` type: a value that stands for the type `a`, passed as a type argument. `type Point` is one, of
/// type `Type(Point)`, and a parameter declared `into: type` or `type a` takes one (see `TypeArguments`). A `Type` value
/// is known at compile time, so it has nothing to hold at run time: the backend lays it out as unit.
module TypeValues =
    /// The type's name, as a type writes it, `Type(Point)`.
    let typeName = "Type"

    /// Whether `name` names the type.
    let isTypeName (name: string) = name = typeName

    /// The type of a value that stands for `ty`.
    let ofType (ty: Ty) = TyApply(typeName, [ ty ])

    /// The type a `Type` value stands for.
    let (|TypeValueType|_|) (ty: Ty) =
        match ty with
        | TyApply(name, [ standsFor ]) when name = typeName -> Some standsFor
        | _ -> None

    /// The description of a record type, `RecordInfo(a)`, which a match on a `Type(a)` finds (see `TypeDescriptions`).
    let recordInfo = "RecordInfo"

    /// The description of a union of tags, `UnionInfo(a)`.
    let unionInfo = "UnionInfo"

    /// The description of a nominal type outside its home module, which shows no structure: `NominalInfo`.
    let nominalInfo = "NominalInfo"

    /// A record's field, which packages the field's type: `Field(a)`.
    let field = "Field"

    /// A union's case, which packages its payload's type: `Case(a)`.
    let case = "Case"

    /// A type argument of a generic type, which packages the type: `SomeType`.
    let someType = "SomeType"

    /// The built-in description types, by name, with the number of type arguments each takes.
    let descriptionArities = Map.ofList [ recordInfo, 1; unionInfo, 1; nominalInfo, 0; field, 1; case, 1; someType, 0 ]

    /// Whether `name` names a built-in description type.
    let isDescriptionType (name: string) = descriptionArities.ContainsKey name

    /// The description types that package a type of their own.
    let packaging = set [ field; case; someType ]

    /// The mark the name of a type the checker never learns carries, before the number that makes it its own: `b#12`
    /// (see `TypeDescriptions.skolem`).
    let packagedMark = '#'

    /// A type the checker never learns, a field's or an element's, with the number that makes it its own.
    let (|PackagedType|_|) (ty: Ty) =
        match ty with
        | TyApply(name, []) when name.Contains packagedMark -> Some (int (name.Substring(name.IndexOf packagedMark + 1)))
        | _ -> None

    let private stringTy = TyPrimitive "string"

    /// The type of a value of the record `fields`, in their order.
    let private recordOf (fields: (string * Ty) list) = TyRecord(Map.ofList fields, FieldOrder.ofList fields)

    /// What a named type's description says of it besides its structure: its name, its module, the type arguments it is
    /// applied to, and its doc comment. A record or union written out has an empty name and module.
    let private naming =
        [ "name", stringTy; "module", stringTy; "parameters", Lists.ofType (TyApply(someType, [])); "doc", stringTy ]

    /// An attribute on a field, as its description gives it: its name, and its argument's values as text, each by its
    /// field's name or position (see `FieldAttribute`).
    let attributeTy =
        recordOf [ "name", stringTy; "arguments", Lists.ofType (recordOf [ "0", stringTy; "1", stringTy ]) ]

    /// The members of a value of the description type `name` applied to `args`, whose packaged type is `packaged`.
    let descriptionMembers (name: string) (args: Ty list) (packaged: Ty) : (string * Ty) list =
        let described = List.tryHead args |> Option.defaultValue (TyPrimitive "unit")
        match name with
        | n when n = recordInfo -> naming @ [ "fields", Lists.ofType (TyApply(field, [ described ])) ]
        | n when n = unionInfo -> naming @ [ "cases", Lists.ofType (TyApply(case, [ described ])) ]
        | n when n = nominalInfo -> naming
        | n when n = field ->
            [ "name", stringTy; "type", ofType packaged; "get", TyFunc(described, packaged, []); "attributes", Lists.ofType attributeTy ]
        | n when n = case ->
            let payload = TyUnion [ TyTag("some", Some packaged); TyTag("none", None) ]
            [ "name", stringTy; "type", ofType packaged; "get", TyFunc(described, payload, []); "build", TyFunc(packaged, described, []) ]
        | _ -> [ "type", ofType packaged ]

    /// A value of the description type `name` applied to `args`, as the record it is, its packaged type `packaged`.
    let descriptionRecord (name: string) (args: Ty list) (packaged: Ty) : Ty = recordOf (descriptionMembers name args packaged)

    /// The tags of a type's description that stand for a built-in type with no parts, each with that type.
    let builtInDescriptions =
        [ "int", TyPrimitive "int"
          "float", TyPrimitive "float"
          "bool", TyPrimitive "bool"
          "string", TyPrimitive "string"
          "unit", TyPrimitive "unit"
          "f32", F32.ty ]
        @ (SizedInteger.all |> List.map (fun integer -> (SizedInteger.name integer).ToLowerInvariant(), SizedInteger.ty integer))

    /// The tags of a collection's description, each with how many element types it has and the collection of them.
    let collectionDescriptions =
        [ "list", 1, (fun (args: Ty list) -> Lists.ofType args.Head)
          "array", 1, (fun args -> Arrays.ofType args.Head)
          "set", 1, (fun args -> Sets.ofType args.Head)
          "map", 2, (fun args -> Maps.ofTypes args.[0] args.[1]) ]

    /// The tag of a function type's description, which describes nothing more.
    let functionTag = "function"

    /// The tag of a record type's description, whose payload is a `RecordInfo(a)`.
    let recordTag = "record"

    /// The tag of a union's description, whose payload is a `UnionInfo(a)`.
    let unionTag = "union"

    /// The tag of a nominal type's description outside its home module, whose payload is a `NominalInfo`.
    let nominalTag = "nominal"

    /// The tags of a type's description, in the order a diagnostic lists them.
    let descriptionTags =
        (builtInDescriptions |> List.map fst) @ (collectionDescriptions |> List.map (fun (tag, _, _) -> tag))
        @ [ functionTag; recordTag; unionTag; nominalTag ]

    /// The union of tags a description of `standsFor` is, with each element type `element`: what a match on a
    /// `Type(standsFor)` matches.
    let descriptionUnion (standsFor: Ty) (element: Ty) : Ty =
        let collection (tag, count, _) =
            let elementTy = ofType element
            TyTag(tag, Some (if count = 1 then elementTy else recordOf (List.init count (fun index -> string index, elementTy))))
        TyUnion(
            (builtInDescriptions |> List.map (fun (tag, _) -> TyTag(tag, None)))
            @ (collectionDescriptions |> List.map collection)
            @ [ TyTag(functionTag, None)
                TyTag(recordTag, Some (TyApply(recordInfo, [ standsFor ])))
                TyTag(unionTag, Some (TyApply(unionInfo, [ standsFor ])))
                TyTag(nominalTag, Some (TyApply(nominalInfo, []))) ])

    /// The prelude's context that gives a type as a value, `Typed(a) = (typeOf: Type(a))`, whose instance the
    /// compiler gives for every type.
    let typedContext = "Typed"

    /// The prelude value that is the member of the compiler's own `Typed` instance: `__typeOf`, which is `type a`.
    let builtInTypeOf = "__typeOf"

    /// The members of the compiler's own `Typed` instance, as `InstanceSynthesized` has them.
    let builtInMembers = [ "typeOf", builtInTypeOf ]

    /// Whether a call that requires `ctxName` at `argTys`, with no instance in scope, is passed the compiler's own
    /// `Typed`: one of a type with no type variable in it, when `env` has the prelude. A type variable's is required, as
    /// any context of one is.
    let passesBuiltIn (env: TypeEnv) (ctxName: string) (argTys: Ty list) =
        match argTys with
        | [ ty ] -> ctxName = typedContext && Set.isEmpty (TypeEnv.freeTyVars ty) && (TypeEnv.tryFind builtInTypeOf env).IsSome
        | _ -> false
