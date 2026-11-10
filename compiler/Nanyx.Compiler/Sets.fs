namespace NyxCompiler

open System

/// The built-in `set(a)` type (see "Maps and sets" in the docs' work in progress). What a set holds is the prelude's
/// business, which reads and builds it through two intrinsics (see `Intrinsics`), as it does a map's (see `Maps`).
module Sets =
    /// The type's name, as a type writes it: `set(int)`. `Set(int)` is the same type, as `List(int)` is `list(int)`.
    let typeName = "set"

    /// The name the prelude's set functions are attached to.
    let moduleName = "Set"

    /// Whether `name` names the type.
    let isTypeName (name: string) = name.Equals(typeName, StringComparison.OrdinalIgnoreCase)

    /// The set of `element`s.
    let ofType (element: Ty) = TyApply(typeName, [ element ])

    /// The element type of a set type.
    let (|SetType|_|) (ty: Ty) =
        match ty with
        | TyApply(name, [ element ]) when name = typeName -> Some element
        | _ -> None
