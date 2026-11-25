/// A type's owned definitions are in scope wherever its values are, whether or not their module is imported (#1514,
/// "Extending a type" in the docs' Attached definitions page). A definition is owned when it is attached in the module that
/// defines its type, or, for a built-in type, in the standard library: `Point.norm` in `Point`'s module, `string.length`
/// in `nanyx/text`. So a module that gets a `Point` from a function of another module pipes it to `norm` without
/// importing `Point`'s module, and `"abc" \length` needs no import. Each module that owns definitions of a type the
/// module can see is merged as a made-up import that selects only those definitions (see `Library.checkModule`).
module internal NyxCompiler.OwnedDefinitions

open Parser.Program
open NyxCompiler.InferState

/// The nominal types in `ty`, each by its name and home.
let rec private nominalsIn (ty: Ty) : (string * ModuleOrigin) list =
    match ty with
    | TyNominal(id, _, _, _) -> (id.Name, id.Home) :: (Ty.children ty |> List.collect nominalsIn)
    | _ -> Ty.children ty |> List.collect nominalsIn

/// The types a module whose imports have made `env` and `state` can see, each by its name and home: those the imports
/// bring in, and those an imported type or definition is written with, as a `Point` is the result of a function that
/// makes one.
let private visibleTypes (env: TypeEnv) (state: InferState) : (string * ModuleOrigin) list =
    (state.TypeDefs |> Map.values |> Seq.map (fun info -> info.Name, info.Home) |> List.ofSeq)
    @ (env.Names |> Map.values |> Seq.collect (fun scheme -> nominalsIn scheme.Type) |> List.ofSeq)
    |> List.filter (fun (_, home) -> home <> InMemory)
    |> List.distinct

/// The modules that own definitions of a type a module whose imports have made `env` and `state` can see (see
/// `visibleTypes`), other than the module itself and those it imports (`merged`, by their `originKey`s, as `keyOf`
/// makes them): the standard-library modules that attach definitions to a built-in type, and each visible type's
/// module. A module the module imports, with an alias or without, is reached as the import says.
let owners (keyOf: ModuleOrigin -> string) (merged: Set<string>) (env: TypeEnv) (state: InferState) : ModuleOrigin list =
    let builtInOwners =
        state.StandardLibraryExports.Value
        |> Map.toList
        |> List.filter (fun (name, _) -> name.LastIndexOf '.' > 0 && TypeResolution.isBuiltInTypeName (name.Substring(0, name.LastIndexOf '.')))
        |> List.collect snd
        |> List.distinct
        // An import path, `nanyx/text`, names the module `text`.
        |> List.map (fun path -> StandardLibraryModule (path.Substring(path.IndexOf '/' + 1)))
    builtInOwners @ (visibleTypes env state |> List.map snd)
    |> List.distinct
    |> List.filter (fun owner -> not (merged.Contains (keyOf owner)))

/// The definitions `exports`, those the module `owner` exports, that it owns, each one that `env` does not already
/// bind: those attached to a type it defines (one of `ownTypes`, the types it knows), or, for a module of the standard
/// library, to a built-in type. A type whose name two visible types have (see `visibleTypes`) is left out, as its
/// definitions' names would say neither.
let selection (owner: ModuleOrigin) (ownTypes: Map<string, TypeDefInfo>) (env: TypeEnv) (state: InferState) (exports: string seq) : Identifier list =
    let ambiguous =
        visibleTypes env state |> List.countBy fst |> List.filter (fun (_, count) -> count > 1) |> List.map fst |> Set.ofList
    let owns (typeName: string) =
        match owner with
        | StandardLibraryModule _ when TypeResolution.isBuiltInTypeName typeName -> true
        | _ ->
            not (ambiguous.Contains typeName)
            && ownTypes |> Map.tryFind typeName |> Option.exists (fun info -> info.Home = owner && not info.IsContext)
    exports
    |> Seq.filter (fun name ->
        let dot = name.LastIndexOf '.'
        dot > 0 && owns (name.Substring(0, dot)) && (TypeEnv.tryFind name env).IsNone)
    |> List.ofSeq