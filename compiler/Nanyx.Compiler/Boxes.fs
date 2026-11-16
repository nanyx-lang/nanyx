namespace NyxCompiler

/// The built-in `box(a)` type: a cell holding one value that can be changed in place, which the backend holds as the box
/// a captured cell is (see "Mutation" in the docs' work in progress). The standard library's `MRef(a)` is declared over
/// it, and makes, reads and changes one through three intrinsics (see `Intrinsics`). It has no equality, order or hash.
module Boxes =
    /// The type's name, as a type writes it, `box(int)`.
    let typeName = "box"

    /// Whether `name` names the type.
    let isTypeName (name: string) = name = typeName

    /// The box of an `element`.
    let ofType (element: Ty) = TyApply(typeName, [ element ])

    /// The type of what a box type holds.
    let (|BoxType|_|) (ty: Ty) =
        match ty with
        | TyApply(name, [ element ]) when name = typeName -> Some element
        | _ -> None
