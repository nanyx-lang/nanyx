namespace NyxCompiler

/// The built-in `map(k, v)` type (see "Maps" in the docs' work in progress). A map literal, `[k => v, ...]`, is a call
/// to the prelude's `__map` (see `MapSyntax`), and calling a map, `m(k)`, is a call to the prelude's `map.get`: what
/// the map holds is the prelude's business, which reads and builds it through two intrinsics (see `Intrinsics`).
module Maps =
    /// The type's name, as a type writes it, `map(string, int)`, and as the prelude's map functions are attached to it.
    let typeName = "map"

    /// The function a call of a map is: `m(k)` is `map.get(m, k)`.
    let lookup = typeName + ".get"

    /// Whether `name` names the type.
    let isTypeName (name: string) = name = typeName

    /// The map from `key` to `value`.
    let ofTypes (key: Ty) (value: Ty) = TyApply(typeName, [ key; value ])

    /// The key and value types of a map type.
    let (|MapType|_|) (ty: Ty) =
        match ty with
        | TyApply(name, [ key; value ]) when name = typeName -> Some (key, value)
        | _ -> None
