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

    /// The tag of a store's node, whose payload is its bits and arrays (see `storeOf`).
    let nodeTag = "node"

    /// The type a map from `key` to `value` is held as, its store: a hash trie,
    /// `#empty | #node(int, array(list((k, v))), int, array(__mapStore(k, v)))`. A node holds the keys whose hashes agree
    /// on the bits the nodes above it read, split by the next five: the first `int` has a bit set for each five-bit value
    /// whose keys the first array holds as a bucket, the entries, each a key and its value, of the keys whose hash is
    /// the same, in the compiler's own order of their keys' structure; the second for each whose keys a node of the
    /// second array holds; each array in the order of the values. `#empty` is only ever a whole map. The shape is defined here, and every
    /// algorithm that builds or reads it is in the prelude, in `stdlib/nanyx/prelude-maps.nyx`, which turns a map into
    /// its store and back through two intrinsics (see `Intrinsics`): whoever changes one changes the other. The backend
    /// lays a map out as its store, and reads one only through the prelude (see `toList`), but for a `for` loop, which
    /// walks the store itself (`StoreWalk` in the backend's `CoreIr.fs`).
    let storeOf (key: Ty) (value: Ty) =
        let tuple (items: Ty list) = TyRecord(items |> List.mapi (fun index ty -> string index, ty) |> Map.ofList, FieldOrder.none)
        let intTy = TyPrimitive "int"
        let body =
            TyUnion
                [ TyTag("empty", None)
                  TyTag(
                      nodeTag,
                      Some (
                          tuple
                              [ intTy
                                Arrays.ofType (Lists.ofType (tuple [ TyParam 0; TyParam 1 ]))
                                intTy
                                Arrays.ofType (TySelf [ TyParam 0; TyParam 1 ]) ]
                      )
                  ) ]
        RecursiveAliases.reference storeName body [ key; value ]

    /// The function that gives a map's entries, each a key and its value, as a list: how the compiler reads a map's
    /// entries, as a `for` loop and `dbg` do, so that only the prelude knows how a map is held.
    let toList = typeName + ".toList"

    /// The type of an entry of a map with keys of `key` and values of `value`, as its `toList` gives them: a pair.
    let entryType (key: Ty) (value: Ty) : Ty = TyRecord(Map.ofList [ "0", key; "1", value ], FieldOrder.none)

    /// The prelude's function that gives a map's store with each key and each value converted, its shape as
    /// it is: how the backend boxes a map's keys and values for its type's description, and unboxes them again,
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
