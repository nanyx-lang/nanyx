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
            |> List.map (function
                | TypeField(name, _, typeOpt, _)
                | TypeMember(name, typeOpt) -> name, typeOpt |> Option.map typeKeyOf |> Option.defaultValue (TypeKey("_", [])))
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

/// The name of the parameter a function that requires a context takes one of its members in, by the member's key.
/// A lambda lifted out of the function captures the member under the same name.
let contextMemberParameter (key: ContextMemberKey) = $"__ctx_{sanitizeIdentifier (ContextMemberKey.text key)}"
