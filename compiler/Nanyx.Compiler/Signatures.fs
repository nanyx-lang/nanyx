/// A def's type written for a reader, after the contexts it requires, as its annotation would write it: what hover,
/// the type lenses and `nanyx check` show, and what the official linter's `unannotatedExports` fix and the refactoring
/// that writes the inferred type write.
module NyxCompiler.Signatures

/// The type of a def, written for a reader as `type` is, after the contexts it requires, as its annotation would write
/// them: `[Show(α), Console] α -> string`. `written` are the contexts as written for a reader, named together with the
/// type.
let withContexts (written: string list) (typeWritten: string) =
    match written with
    | [] -> typeWritten
    | _ -> $"""[{String.concat ", " written}] {typeWritten}"""

/// `ty` written for a reader after the contexts `contexts` it requires, as `withContexts` writes them, and each of
/// `others` written for a reader, the type variables of them all named together and by `names`, so that a variable of
/// `ty` has the same name in `others`: a parameter's `α` is the `α` of its def's signature. A context of `typeDefs`
/// that has associated types is written with them by name, as a signature writes it: `[Iter(c, elem = a)] c -> a`.
let typesWithContexts (typeDefs: Map<string, TypeDefInfo>) (names: TypeNotation) (ty: Ty) (contexts: Ty list) (others: Ty list) : string * string list =
    match contexts, others with
    | [], [] -> Unifier.tyToDisplayStringIn typeDefs names ty, []
    | _ ->
        let written = Unifier.tysToDisplayStringsIn typeDefs names (ty :: contexts @ others)
        let contextsWritten, othersWritten = List.splitAt contexts.Length (List.tail written)
        withContexts contextsWritten (List.head written), othersWritten

/// `ty` written for a reader after the contexts `contexts` it requires, as `typesWithContexts` writes it.
let typeWithContexts (typeDefs: Map<string, TypeDefInfo>) (names: TypeNotation) (ty: Ty) (contexts: Ty list) =
    fst (typesWithContexts typeDefs names ty contexts [])

/// The name a reader knows the custom pattern whose def is named `name` by, its tags: `#Num`, or `#Even | #Odd`. None
/// if `name` is not a pattern's (see `PatternSyntax`).
let patternName (name: string) : string option =
    Parser.Program.PatternSyntax.shapeOf name |> Option.map (fun shape -> shape.Tags |> List.map (fun tag -> $"#{tag}") |> String.concat " | ")

/// The declaration of the custom pattern whose def is named `name`, as it was written, `typeWritten` being the def's
/// type as written for a reader: `pattern string -> #Num(float) | _`, with `_` for the tag a partial pattern returns
/// when it doesn't match. None if `name` is not a pattern's.
let patternDeclaration (name: string) (typeWritten: string) : string option =
    Parser.Program.PatternSyntax.shapeOf name
    |> Option.map (fun shape ->
        // A partial pattern with one tag and no payload is a function that returns a `bool` (see `CustomPatterns`).
        let results =
            if shape.ReturnsBool && typeWritten.EndsWith "bool" then typeWritten.Substring(0, typeWritten.Length - "bool".Length) + $"#{shape.Tags.Head} | _"
            else typeWritten.Replace("#" + Parser.Program.PatternSyntax.noMatchTag, "_")
        $"{Parser.Program.PatternSyntax.keyword} {results}")

/// `ty` with the name of each type and context it names replaced by `written`, given a test for the definitions that
/// define the same type and the name it has.
let private renamed (written: (TypeDefInfo -> bool) -> string -> string) (ty: Ty) : Ty =
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
        | TyRecord(fields, order) -> TyRecord(Map.map (fun _ field -> rename field) fields, order)
        | TyOpenRecord(fields, row) -> TyOpenRecord(Map.map (fun _ field -> rename field) fields, row)
        | TyTag(name, payload) -> TyTag(name, Option.map rename payload)
        | TyUnion items -> TyUnion(List.map rename items)
        | TyRecursive(body, args) -> TyRecursive(body, List.map rename args)
        | TySelf args -> TySelf(List.map rename args)
        | TyScope scope -> TyScope(rename scope)
        | TyContexts contexts -> TyContexts(List.map rename contexts)
        | TyMeasure(dimension, storage) ->
            let dimension =
                dimension |> List.map (fun (id, exponent) -> { id with Name = written (fun info -> info.IsDimension && TypeDefInfo.nominalId info = id) id.Name }, exponent)
            TyMeasure(dimension, rename storage)
    rename ty

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
    renamed written ty

/// The paths of the module blocks around `offset` in `syntax`, innermost first: `Points.Inner`, then `Points`.
let private moduleBlocksAt (syntax: Parser.Program.Module) (offset: int) : string list =
    let rec around (prefix: string) (items: Parser.Program.TopLevelItem list) =
        items
        |> List.tryPick (fun item ->
            match item.Kind with
            | Parser.Program.ModuleBlock(name, inner) when item.Span.Start <= offset && offset < item.Span.End ->
                let path = if prefix = "" then name else $"{prefix}.{name}"
                Some (around path inner @ [ path ])
            | _ -> None)
        |> Option.defaultValue []
    around "" syntax

/// The simplest name the code inside the module blocks `blocks` (innermost first) can write the type that `isIt`
/// defines, known as `name`, by, from the module's `typeDefs` (see `relativeAt`).
let private relativeName (typeDefs: Map<string, TypeDefInfo>) (blocks: string list) (isIt: TypeDefInfo -> bool) (name: string) =
    let names = name :: (typeDefs |> Map.filter (fun _ info -> isIt info) |> Map.keys |> List.ofSeq)
    let withinBlocks =
        names
        |> List.collect (fun candidate ->
            blocks
            |> List.choose (fun block -> if candidate.StartsWith(block + ".") then Some (candidate.Substring(block.Length + 1)) else None))
    names @ withinBlocks
    |> List.minBy (fun candidate -> candidate |> Seq.filter ((=) '.') |> Seq.length, candidate.Length, candidate)

/// `ty` with each type and context it names written by the simplest name the code at `offset` in `syntax` can write it
/// by, from the module's `typeDefs`: one an import brings in (`Point` after `import Points`, `s.Circle` through an
/// alias), or, inside a module block, the name the block's own code writes (`Point` inside `module Points`, which
/// declares it), rather than the name the compiler knows it by, `Points.Point`: the one with the fewest dots, then the
/// shortest, then the first by name. What the info view writes.
let relativeAt (typeDefs: Map<string, TypeDefInfo>) (syntax: Parser.Program.Module) (offset: int) (ty: Ty) : Ty =
    renamed (relativeName typeDefs (moduleBlocksAt syntax offset)) ty

/// Each type and context of `typeDefs` that has a definition, by the name the code at `offset` in `syntax` writes it by,
/// as `relativeAt` names it, with the symbol of its definition: what the info view links a type's name to.
let typeNamesAt (typeDefs: Map<string, TypeDefInfo>) (symbols: Map<SymbolId, Symbol>) (syntax: Parser.Program.Module) (offset: int) : (string * Symbol) list =
    let blocks = moduleBlocksAt syntax offset
    typeDefs
    |> Map.values
    |> Seq.choose (fun info -> info.Symbol |> Option.bind (fun id -> symbols |> Map.tryFind id) |> Option.map (fun symbol -> info, symbol))
    |> Seq.distinctBy (fun (_, symbol) -> symbol.Id)
    |> Seq.map (fun (info, symbol) ->
        let isIt (other: TypeDefInfo) =
            other.IsNominal = info.IsNominal
            && (if info.IsNominal then TypeDefInfo.nominalId other = TypeDefInfo.nominalId info else other.Name = info.Name)
        relativeName typeDefs blocks isIt info.Name, symbol)
    |> List.ofSeq
