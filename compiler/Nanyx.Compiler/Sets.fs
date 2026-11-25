namespace NyxCompiler

/// The built-in `set(a)` type (see "Maps and sets" in the docs' work in progress). What a set holds is the prelude's
/// business, which reads and builds it through two intrinsics (see `Intrinsics`), as it does a map's (see `Maps`).
module Sets =
    /// The type's name, as a type writes it, `set(int)`, and as the prelude's set functions are attached to it.
    let typeName = "set"

    /// The name of the type a set is held as, `__setStore(a)`, which only the prelude writes.
    let storeName = "__setStore"

    /// The type a set of `element`s is held as: the list of its elements. As a map's (see `Maps.storeOf`), the shape
    /// is defined here and every algorithm that builds or reads it is in `stdlib/nanyx/prelude-maps.nyx`.
    let storeOf (element: Ty) = TyAlias(storeName, [ element ], Lists.ofType element)

    /// The function that gives a set's elements as a list: how the compiler reads a set's elements (see `Maps.toList`).
    let toList = typeName + ".toList"

    /// Whether `name` names the type.
    let isTypeName (name: string) = name = typeName

    /// The set of `element`s.
    let ofType (element: Ty) = TyApply(typeName, [ element ])

    /// The element type of a set type.
    let (|SetType|_|) (ty: Ty) =
        match ty with
        | TyApply(name, [ element ]) when name = typeName -> Some element
        | _ -> None
