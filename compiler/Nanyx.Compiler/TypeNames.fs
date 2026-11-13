/// The name of a type or a context starts with a capital letter, after the `_` of one marked unused (#573). In a type,
/// a lowercase name is a type variable if it is one letter, or a built-in type (`int`, `list`); longer ones are reserved
/// for associated types, and `TypeResolution` reports them where they are written.
module NyxCompiler.TypeNames

open Parser.Program

/// `name` with its first letter, after an unused `_`, a capital: `point` is `Point`.
let private capitalised (name: string) =
    let start = name.Length - name.TrimStart('_').Length
    name.Substring(0, start) + string (System.Char.ToUpperInvariant name[start]) + name.Substring(start + 1)

/// An error at the name of each type or context defined in the module whose names are `declared`, at the top level or
/// in a block, whose name does not start with a capital letter.
let definitionErrors (declared: DeclaredNames) : Diagnostic list =
    declared
    |> Map.toList
    |> List.collect (fun ((_, declarer), names) ->
        match declarer with
        | TypeDefBody -> List.truncate 1 names
        | _ -> [])
    |> List.filter (fun (name, _) ->
        let own = name.TrimStart('_')
        own <> "" && not (System.Char.IsUpper own[0]))
    |> List.sortBy (fun (_, span) -> span.File, span.Start)
    |> List.map (fun (name, span) ->
        let message =
            if TypeResolution.isBuiltInTypeName name then
                $"'{name}' is a built-in type, so it cannot be defined again: a type's name starts with a capital letter"
            else
                $"'{name}' cannot be a type's name, which starts with a capital letter: write '{capitalised name}'"
        Diagnostics.at DiagnosticCodes.InvalidTypeName span message)
