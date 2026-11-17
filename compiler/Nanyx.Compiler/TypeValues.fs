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
