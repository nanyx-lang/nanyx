/// A module defines each top-level name once (#202). Values and types are named apart, so a def and a type may share
/// a name. A dimension is a type, so it shares a name with neither a type nor another dimension (#3115). An attached def's name has its type in it (`list.map`, `string.map`), and a def in a `module name =` block
/// is lifted to the top level under its qualified name, so neither is mistaken for another of the same short name.
module NyxCompiler.DuplicateDefinitions

open Parser.Program

type private Namespace =
    | Value
    | Type

/// The name `item` defines, what it is a name of, and where it is written, if the parser saw it written: a def the
/// compiler made up is left out.
let private definedName (declared: DeclaredNames) (item: TopLevelItem) =
    let named space kind name nodeId declarer =
        declared
        |> Map.tryFind (nodeId, declarer)
        |> Option.bind List.tryHead
        |> Option.map (fun (_, span) -> (space, name), kind, span)
    match item.Kind with
    | Def (ValueDef(_, name, _, value)) -> named Value "name" name value.Id DefValue
    | Def (TypeDef(name, _, _, body)) -> named Type "type" name body.Id TypeDefBody
    | DimensionDecl(_, name, _, _) -> named Type "dimension" name item.Id MeasureDeclaration
    | _ -> None

/// `module'` without the second and later definitions of a name, so nothing is inferred from them, and an error at
/// each of those, pointing back at the first.
let check (declared: DeclaredNames) (module': TopLevelItem list) : TopLevelItem list * Diagnostic list =
    let mutable first = Map.empty
    let mutable errors = []
    let kept =
        module'
        |> List.filter (fun item ->
            match definedName declared item with
            | None -> true
            | Some (key, kind, span) ->
                match first |> Map.tryFind key with
                | None ->
                    first <- first |> Map.add key span
                    true
                | Some firstSpan ->
                    let name = snd key
                    errors <-
                        errors
                        @ [ Diagnostics.at DiagnosticCodes.DuplicateDefinition span $"The {kind} '{name}' is already defined in this module"
                            |> Diagnostics.about name
                            |> Diagnostics.withRelated firstSpan $"'{name}' is first defined here" ]
                    false)
    kept, errors
