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

/// The built-in `marray(a)` type: a fixed-length array whose elements are changed in place, laid out as an `array` is,
/// which the standard library's `MArray(a)` is declared over. Only the standard library makes, reads or changes one,
/// through intrinsics of its own (see `Intrinsics`). It is a type of its own, not an `array`, so that it has no `Eq`,
/// `Ord` or `Hash`, and a def's type is not generalised over its elements, as for a box (see `Boxes`).
module MutableArrays =
    /// The type's name, as a type writes it, `marray(int)`.
    let typeName = "marray"

    /// Whether `name` names the type.
    let isTypeName (name: string) = name = typeName

    /// The mutable array of `element`s.
    let ofType (element: Ty) = TyApply(typeName, [ element ])

    /// The element type of a mutable array type.
    let (|MutableArrayType|_|) (ty: Ty) =
        match ty with
        | TyApply(name, [ element ]) when name = typeName -> Some element
        | _ -> None
