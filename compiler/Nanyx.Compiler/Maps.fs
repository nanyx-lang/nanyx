namespace NyxCompiler

/// The built-in `map(k, v)` type (see "Maps" in the docs' work in progress). A map literal, `[k => v, ...]`, is a call
/// to the prelude's `__map` (see `MapSyntax`), and calling a map, `m(k)`, is a call to the prelude's `map.get`: what
/// the map holds is the prelude's business, which reads and builds it through two intrinsics (see `Intrinsics`).
module Maps =
    /// The type's name, as a type writes it, `map(string, int)`, and as the prelude's map functions are attached to it.
    let typeName = "map"

    /// The function a call of a map is: `m(k)` is `map.get(m, k)`.
    let lookup = typeName + ".get"

    /// The name of the type a map is held as, `__mapStore(k, v)`, which only the prelude writes.
    let storeName = "__mapStore"

    /// The type a map from `key` to `value` is held as: the list of its entries, each a tuple of a key and its value.
    /// The shape is defined here, and every algorithm that builds or reads it is in the prelude, in
    /// `stdlib/nanyx/prelude-maps.nyx`, which turns a map into its store and back through two intrinsics (see
    /// `Intrinsics`): whoever changes one changes the other. The backend lays a map out as its store.
    let storeOf (key: Ty) (value: Ty) =
        let entry = TyRecord(Map.ofList [ "0", key; "1", value ], FieldOrder.none)
        TyAlias(storeName, [ key; value ], Lists.ofType entry)

    /// The function that gives a map's entries, each a key and its value, as a list: how the compiler reads a map's
    /// entries, as a `for` loop and `dbg` do, so that only the prelude knows how a map is held.
    let toList = typeName + ".toList"

    /// Whether `name` names the type.
    let isTypeName (name: string) = name = typeName

    /// The map from `key` to `value`.
    let ofTypes (key: Ty) (value: Ty) = TyApply(typeName, [ key; value ])

    /// The key and value types of a map type.
    let (|MapType|_|) (ty: Ty) =
        match ty with
        | TyApply(name, [ key; value ]) when name = typeName -> Some (key, value)
        | _ -> None
