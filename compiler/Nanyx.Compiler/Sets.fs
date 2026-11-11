namespace NyxCompiler

/// The built-in `set(a)` type (see "Maps and sets" in the docs' work in progress). What a set holds is the prelude's
/// business, which reads and builds it through two intrinsics (see `Intrinsics`), as it does a map's (see `Maps`).
module Sets =
    /// The type's name, as a type writes it, `set(int)`, and as the prelude's set functions are attached to it.
    let typeName = "set"

    /// Whether `name` names the type.
    let isTypeName (name: string) = name = typeName

    /// The set of `element`s.
    let ofType (element: Ty) = TyApply(typeName, [ element ])

    /// The element type of a set type.
    let (|SetType|_|) (ty: Ty) =
        match ty with
        | TyApply(name, [ element ]) when name = typeName -> Some element
        | _ -> None
