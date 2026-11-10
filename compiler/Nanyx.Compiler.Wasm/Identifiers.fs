/// The names the backend derives from names in the source: context member keys and WebAssembly identifiers.
module internal Transpiler.Wasm.Identifiers

open System
open Parser.Program

// Convert a TypeExpr to a string key (e.g., TypeApply("ToString", [TypeVar "a"]) -> "ToString_a")
let rec typeExprToKey (t: TypeExpr) =
    match t.Kind with
    | TypeName name -> name
    | TypeVar name -> name
    | TypeApply(name, typeArgs) -> 
        let argsStr = typeArgs |> List.map typeExprToKey |> String.concat "_"
        $"{name}_{argsStr}"
    | _ -> "unknown"

/// The key a context member is known by in the backend: the member's name for a plain context, and
/// `<context key>_<member>` for an instance of a parameterised one (`ToString_int_toString`).
let contextMemberKey (contextType: TypeExpr option) (memberName: string) =
    match contextType with
    | Some ({ TypeExpr.Kind = TypeApply _ } as applied) -> $"{typeExprToKey applied}_{memberName}"
    | _ -> memberName

let sanitizeIdentifier (name: string) =
    let sanitized =
        name
        |> Seq.map (fun c -> if Char.IsLetterOrDigit c || c = '_' then c else '_')
        |> Seq.toArray
        |> String
    if String.IsNullOrWhiteSpace sanitized then "_"
    elif Char.IsDigit sanitized.[0] then "_" + sanitized
    else sanitized

/// The name of the parameter a function that requires a context takes one of its members in, by the member's key.
/// A lambda lifted out of the function captures the member under the same name.
let contextMemberParameter (key: string) = $"__ctx_{sanitizeIdentifier key}"
