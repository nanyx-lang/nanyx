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

    /// The type a map from `key` to `value` is held as, its store: a hash trie,
    /// `#empty | #bucket(int, list((k, v))) | #node(int, array(__mapStore(k, v)))`. A bucket holds the entries, each a
    /// key and its value, of the keys whose hash is its `int`: one, or more whose hashes collide, in the compiler's own
    /// order of their keys' structure. A node holds the keys whose hashes agree on the bits the nodes above it read,
    /// split by the next five, with a bit of its `int` set for each five-bit value some key has and a trie for each in
    /// its array, in the order of the values. `#empty` is only ever a whole map. The shape is defined here, and every
    /// algorithm that builds or reads it is in the prelude, in `stdlib/nanyx/prelude-maps.nyx`, which turns a map into
    /// its store and back through two intrinsics (see `Intrinsics`): whoever changes one changes the other. The backend
    /// lays a map out as its store, and reads one only through the prelude (see `toList`).
    let storeOf (key: Ty) (value: Ty) =
        let tuple (items: Ty list) = TyRecord(items |> List.mapi (fun index ty -> string index, ty) |> Map.ofList, FieldOrder.none)
        let intTy = TyPrimitive "int"
        let body =
            TyUnion
                [ TyTag("bucket", Some (tuple [ intTy; Lists.ofType (tuple [ TyParam 0; TyParam 1 ]) ]))
                  TyTag("empty", None)
                  TyTag("node", Some (tuple [ intTy; Arrays.ofType (TySelf [ TyParam 0; TyParam 1 ]) ])) ]
        RecursiveAliases.reference storeName body [ key; value ]

    /// The function that gives a map's entries, each a key and its value, as a list: how the compiler reads a map's
    /// entries, as a `for` loop and `dbg` do, so that only the prelude knows how a map is held.
    let toList = typeName + ".toList"

    /// The prelude's function that gives a map's store with each key and each value converted, its hashes and shape as
    /// they are: how the backend boxes a map's keys and values for its type's description, and unboxes them again,
    /// without hashing anything.
    let convertedStore = "__mapStoreConverted"

    /// Whether `name` names the type.
    let isTypeName (name: string) = name = typeName

    /// The map from `key` to `value`.
    let ofTypes (key: Ty) (value: Ty) = TyApply(typeName, [ key; value ])

    /// The key and value types of a map type.
    let (|MapType|_|) (ty: Ty) =
        match ty with
        | TyApply(name, [ key; value ]) when name = typeName -> Some (key, value)
        | _ -> None
