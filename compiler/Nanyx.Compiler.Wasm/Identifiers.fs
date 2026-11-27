/// The names the backend derives from names in the source: context member keys and WebAssembly identifiers.
module internal Transpiler.Wasm.Identifiers

open System
open Parser.Program

/// `t` as a type key: `ToString(a)` is `TypeKey("ToString", [ TypeKey("a", []) ])`. A structural type is keyed by
/// a name no type can have, as `(,)` for a tuple and `#x` for a tag, with its parts, so that contexts applied to
/// different ones (`ToString((int, bool))` and `ToString(#x(a) | #y)`) have different keys.
let rec private typeKeyOf (t: TypeExpr) =
    match t.Kind with
    | TypeName name
    | TypeVar name -> TypeKey(name, [])
    | TypeApply(name, typeArgs) -> TypeKey(name, typeArgs |> List.map typeKeyOf)
    | TypeUnit -> TypeKey("()", [])
    | TypeTuple items -> TypeKey("(,)", items |> List.map typeKeyOf)
    | TypeRecord fields ->
        let names, types =
            fields
            |> List.choose (function
                | TypeField(name, _, typeOpt, _) -> Some (name, typeOpt |> Option.map typeKeyOf |> Option.defaultValue (TypeKey("_", [])))
                | TypeFieldAttribute _ -> None)
            |> List.unzip
        TypeKey("(" + String.concat "," names + ")", types)
    | TypeTag(name, payload) -> TypeKey("#" + name, payload |> Option.map typeKeyOf |> Option.toList)
    | TypeUnion items -> TypeKey("|", items |> List.map typeKeyOf)
    | TypeFunc(input, output) -> TypeKey("->", [ typeKeyOf input; typeKeyOf output ])
    | _ -> TypeKey("unknown", [])

/// The key a context member is known by in the backend: the member's name for a plain context, and the instance's
/// member for an instance of a parameterised one (`toString` of `ToString(int)`).
let contextMemberKey (contextType: TypeExpr option) (memberName: string) =
    match contextType with
    | Some ({ TypeExpr.Kind = TypeApply _ } as applied) -> InstanceMember(typeKeyOf applied, memberName)
    | _ -> NamedMember memberName

/// `ty` as a type key, as `typeKeyOf` keys the type an annotation would write for it (`Show(Point)`); none for a type
/// with a type variable in it, or one an annotation cannot write. A tuple is keyed as a written one is.
let rec typeKeyOfType (ty: NyxCompiler.Ty) : TypeKey option =
    let all (tys: NyxCompiler.Ty list) =
        let keys = tys |> List.map typeKeyOfType
        if keys |> List.forall Option.isSome then Some (keys |> List.map Option.get) else None
    match ty with
    | NyxCompiler.TyVar _
    | NyxCompiler.TyOpenRecord _
    | NyxCompiler.TyRecursive _
    | NyxCompiler.TySelf _
    | NyxCompiler.TyMember _
    | NyxCompiler.TyGroup _
    | NyxCompiler.TyParam _
    | NyxCompiler.TyScope _
    | NyxCompiler.TyContexts _ -> None
    | NyxCompiler.TyPrimitive "unit" -> Some (TypeKey("()", []))
    | NyxCompiler.TyPrimitive "?error" -> None
    | NyxCompiler.TyPrimitive name
    | NyxCompiler.TyAlias(name, [], _) -> Some (TypeKey(name, []))
    | NyxCompiler.TyNominal(nominal, _, _, _) -> Some (TypeKey(nominal.Name, []))
    | NyxCompiler.TyMeasure(dimension, storage) -> all [ storage ] |> Option.map (fun keys -> TypeKey(NyxCompiler.Dimension.text dimension, keys))
    | NyxCompiler.TyAlias(name, args, _)
    | NyxCompiler.TyApply(name, args) -> all args |> Option.map (fun args -> TypeKey(name, args))
    | NyxCompiler.TyRecord(fields, _) ->
        let names, tys = fields |> Map.toList |> List.unzip
        let isTuple = names |> List.forall (fun name -> fst (Int32.TryParse name))
        let tys = if isTuple then names |> List.sortBy int |> List.map (fun name -> fields.[name]) else tys
        all tys |> Option.map (fun keys -> TypeKey((if isTuple then "(,)" else "(" + String.concat "," names + ")"), keys))
    | NyxCompiler.TyTag(name, payload) -> all (Option.toList payload) |> Option.map (fun keys -> TypeKey("#" + name, keys))
    | NyxCompiler.TyUnion items -> all items |> Option.map (fun keys -> TypeKey("|", keys))
    | NyxCompiler.TyFunc(input, output, _) -> all [ input; output ] |> Option.map (fun keys -> TypeKey("->", keys))

/// `name` as a WebAssembly identifier. ASCII letters, digits, `_` and `-` are kept, and a `.` becomes `_`. Any other
/// character becomes its code in hex between dashes, `-20-` for a space, so that names that differ only there, such
/// as `` `add points` `` and `add_points`, or `` Point.`==` `` and `` Point.`!=` ``, stay apart. Some names are
/// sanitized twice, so a sanitized name is left as it is.
let sanitizeIdentifier (name: string) =
    let sanitized =
        name
        |> Seq.map (fun c ->
            if Char.IsAsciiLetterOrDigit c || c = '_' || c = '-' then string c
            elif c = '.' then "_"
            else $"-%X{int c}-")
        |> String.concat ""
    if String.IsNullOrWhiteSpace sanitized then "_"
    elif Char.IsDigit sanitized.[0] then "_" + sanitized
    else sanitized

/// The first of `baseName`, `baseName_1`, `baseName_2` and so on that `taken` does not hold.
let freeName (taken: string -> bool) (baseName: string) =
    let mutable candidate = baseName
    let mutable index = 1
    while taken candidate do
        candidate <- $"{baseName}_{index}"
        index <- index + 1
    candidate

/// Names made unique among `taken`, each the first of `baseName`, `baseName_1`, `baseName_2` and so on that `taken` does
/// not hold, which it then holds. It remembers how far it got with each base name, so that the k-th name made from one
/// costs a lookup or two, not k: a block of thousands of stores made a local `__stored` at each (#2408).
type NameSupply(taken: System.Collections.Generic.HashSet<string>) =
    let next = System.Collections.Generic.Dictionary<string, int>()

    /// A name made from `baseName` that nothing has yet.
    member _.Fresh(baseName: string) =
        let mutable index = match next.TryGetValue baseName with | true, index -> index | _ -> 0
        let nameAt index = if index = 0 then baseName else $"{baseName}_{index}"
        while taken.Contains(nameAt index) do
            index <- index + 1
        next.[baseName] <- index + 1
        let name = nameAt index
        taken.Add name |> ignore
        name

/// The name of the parameter a function that requires a context takes one of its members in, by the member's key.
/// A lambda lifted out of the function captures the member under the same name.
let contextMemberParameter (key: ContextMemberKey) = $"__ctx_{sanitizeIdentifier (ContextMemberKey.text key)}"
