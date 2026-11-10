namespace NyxCompiler

open System

/// The built-in `map(k, v)` type (see "Maps" in the docs' work in progress). A map literal, `[k => v, ...]`, is a call
/// to the prelude's `__map` (see `MapSyntax`), and calling a map, `m(k)`, is a call to the prelude's `Map.get`: what
/// the map holds is the prelude's business, which reads and builds it through two intrinsics (see `Intrinsics`).
module Maps =
    /// The type's name, as a type writes it: `map(string, int)`. `Map(string, int)` is the same type, as `List(int)` is
    /// `list(int)`.
    let typeName = "map"

    /// The name the prelude's map functions are attached to.
    let moduleName = "Map"

    /// The function a call of a map is: `m(k)` is `Map.get(m, k)`.
    let lookup = moduleName + ".get"

    /// Whether `name` names the type.
    let isTypeName (name: string) = name.Equals(typeName, StringComparison.OrdinalIgnoreCase)

    /// The map from `key` to `value`.
    let ofTypes (key: Ty) (value: Ty) = TyApply(typeName, [ key; value ])

    /// The key and value types of a map type.
    let (|MapType|_|) (ty: Ty) =
        match ty with
        | TyApply(name, [ key; value ]) when name = typeName -> Some (key, value)
        | _ -> None
