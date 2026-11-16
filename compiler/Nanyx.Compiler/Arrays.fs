namespace NyxCompiler

/// The built-in `array(a)` type, written `Array(a)`: an immutable, fixed-length sequence with constant-time indexing,
/// which the backend holds as one block of its elements (see "Mutation" in the docs' work in progress). `nanyx/array`
/// makes and reads one through four intrinsics (see `Intrinsics`).
module Arrays =
    /// The type's name, as a type writes it, `array(int)`, and as `nanyx/array`'s functions are attached to it.
    let typeName = "array"

    /// Whether `name` names the type.
    let isTypeName (name: string) = name = typeName

    /// The array of `element`s.
    let ofType (element: Ty) = TyApply(typeName, [ element ])

    /// The element type of an array type.
    let (|ArrayType|_|) (ty: Ty) =
        match ty with
        | TyApply(name, [ element ]) when name = typeName -> Some element
        | _ -> None
