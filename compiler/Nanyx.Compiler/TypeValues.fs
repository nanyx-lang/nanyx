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
