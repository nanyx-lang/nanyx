/// A def's type written for a reader, after the contexts it requires, as its annotation would write it: what hover,
/// the type lenses and `nanyx check` show, and what the official linter's `unannotatedExports` fix and the refactoring
/// that writes the inferred type write.
module NyxCompiler.Signatures

/// The type of a def, written for a reader as `type` is, after the contexts it requires, as its annotation would write
/// them: `[Show(α) & Console] α -> string`. `written` are the contexts as written for a reader, named together with the
/// type.
let withContexts (written: string list) (typeWritten: string) =
    match written with
    | [] -> typeWritten
    | _ -> $"""[{String.concat " & " written}] {typeWritten}"""

/// `ty` written for a reader after the contexts `contexts` it requires, as `withContexts` writes them, and each of
/// `others` written for a reader, the type variables of them all named together and by `names`, so that a variable of
/// `ty` has the same name in `others`: a parameter's `α` is the `α` of its def's signature.
let typesWithContexts (names: TypeNotation) (ty: Ty) (contexts: Ty list) (others: Ty list) : string * string list =
    match contexts, others with
    | [], [] -> Unifier.tyToDisplayString names ty, []
    | _ ->
        let written = Unifier.tysToDisplayStrings names (ty :: contexts @ others)
        let contextsWritten, othersWritten = List.splitAt contexts.Length (List.tail written)
        withContexts contextsWritten (List.head written), othersWritten

/// `ty` written for a reader after the contexts `contexts` it requires, as `typesWithContexts` writes it.
let typeWithContexts (names: TypeNotation) (ty: Ty) (contexts: Ty list) =
    fst (typesWithContexts names ty contexts [])

/// `ty` with each type and context it names given the name a module writes it by, from the module's `typeDefs` and
/// `symbols`: a type a module imports under an alias is named through the alias, `s.Circle`, though the compiler
/// knows it as `Circle`; any other keeps its name. What an annotation an editor inserts writes, with `Source`.
let writable (typeDefs: Map<string, TypeDefInfo>) (symbols: Map<SymbolId, Symbol>) (ty: Ty) : Ty =
    let aliases =
        symbols |> Map.values |> Seq.filter (fun symbol -> symbol.Kind = ImportedModule) |> Seq.map (fun symbol -> symbol.Name) |> Set.ofSeq
    let written (isIt: TypeDefInfo -> bool) (name: string) =
        typeDefs
        |> Map.tryFindKey (fun key info ->
            let dot = key.LastIndexOf '.'
            dot > 0 && aliases.Contains(key.Substring(0, dot)) && isIt info)
        |> Option.defaultValue name
    let rec rename (ty: Ty) =
        match ty with
        | TyVar _
        | TyPrimitive _
        | TyParam _ -> ty
        | TyNominal(id, args, underlying, isPrivate) ->
            TyNominal({ id with Name = written (fun info -> info.IsNominal && TypeDefInfo.nominalId info = id) id.Name }, List.map rename args, underlying, isPrivate)
        | TyAlias(name, args, underlying) -> TyAlias(written (fun info -> not info.IsNominal && info.Name = name) name, List.map rename args, underlying)
        | TyApply(name, args) -> TyApply(written (fun info -> info.IsContext && info.Name = name) name, List.map rename args)
        | TyFunc(arg, ret, contexts) -> TyFunc(rename arg, rename ret, List.map rename contexts)
        | TyRecord fields -> TyRecord(Map.map (fun _ field -> rename field) fields)
        | TyOpenRecord(fields, row) -> TyOpenRecord(Map.map (fun _ field -> rename field) fields, row)
        | TyTag(name, payload) -> TyTag(name, Option.map rename payload)
        | TyUnion items -> TyUnion(List.map rename items)
        | TyRecursive(body, args) -> TyRecursive(body, List.map rename args)
        | TySelf args -> TySelf(List.map rename args)
    rename ty
