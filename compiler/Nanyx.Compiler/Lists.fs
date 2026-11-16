namespace NyxCompiler

/// The built-in `list(a)` type: a list literal, `[a, b]`, and a list pattern have it, and the prelude's list functions
/// are attached to it.
module Lists =
    /// The type's name, as a type writes it, `list(int)`, and as the prelude's list functions are attached to it.
    let typeName = "list"

    /// Whether `name` names the type.
    let isTypeName (name: string) = name = typeName

    /// The list of `element`s.
    let ofType (element: Ty) = TyApply(typeName, [ element ])

    /// The element type of a list type.
    let (|ListType|_|) (ty: Ty) =
        match ty with
        | TyApply(name, [ element ]) when name = typeName -> Some element
        | _ -> None
