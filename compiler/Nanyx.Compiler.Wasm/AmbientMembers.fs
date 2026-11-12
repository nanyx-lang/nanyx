/// The ambient instance of a context: the top-level functions named like its members, which stand in for them where
/// nothing in scope supplies them (the runtime's `Console`).
module internal Transpiler.Wasm.AmbientMembers

open Parser.Program

/// The top-level definitions that can stand in for the members of the contexts `module'` declares (the ambient
/// instance, like the runtime's `Console`), each with the member it stands in for, in the order the contexts declare
/// them: the definition of the member's own name, or else, for a context without parameters whose every member is
/// defined under one qualifier, the definition under it (`c.write`, where `nanyx/console` is imported as `c`). The type
/// checker finds an aliased import's ambient instance the same way.
let ambientMemberDefinitions (module': Module) : (string * string) list =
    let defined = module' |> List.choose (function Kind (Def (ValueDef(_, name, _, _))) -> Some name | _ -> None) |> Set.ofList
    module'
    |> List.collect (function
        | Kind (Def (TypeDef(_, modifiers, parameters, Kind (TypeRecord fields)))) when modifiers |> List.contains Context ->
            let members = fields |> List.map (function TypeField(fieldName, _, _, _, _) | TypeMember(fieldName, _) -> fieldName)
            let qualifiers =
                match members with
                | first :: _ when parameters.IsEmpty ->
                    defined
                    |> Seq.choose (fun name ->
                        match name.LastIndexOf '.' with
                        | dot when dot > 0 && name.Substring(dot + 1) = first -> Some (name.Substring(0, dot))
                        | _ -> None)
                    |> Seq.filter (fun qualifier -> members |> List.forall (fun memberName -> defined.Contains $"{qualifier}.{memberName}"))
                    |> List.ofSeq
                | _ -> []
            members
            |> List.choose (fun memberName ->
                if defined.Contains memberName then Some (memberName, memberName)
                else qualifiers |> List.tryHead |> Option.map (fun qualifier -> memberName, $"{qualifier}.{memberName}"))
        | _ -> [])
    |> List.distinctBy fst
