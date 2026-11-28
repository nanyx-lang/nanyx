namespace NyxCompiler

open System
open System.IO
open System.Text.Json
open Parser.Program
open Parser.Program.DataValues

/// What a name a public module exports is.
type ApiKind =
    | ApiDef
    | ApiType
    | ApiContext

/// A name a public module of a package exports, with what another package sees of it: a def's type, after the contexts
/// it requires, or a type's or a context's whole definition.
type ApiExport =
    { Kind: ApiKind
      Name: string
      /// A def's type, `[Eq(a)] list(a), a -> bool`, or a type's definition, `type Point = (x: int, y: int)`.
      Signature: string
      /// A nominal type's parameters' variances, as its definition decides them (see `Variances`): what may be given
      /// for what, which can change with no change to how the definition is written, when a type it is made from
      /// changes. None for any other export, and in an `api.json` written before they were recorded.
      Variances: Variance list option }

/// A package's public API, as `api.json` records it: what each module it exports with `export module` exports.
type PackageApi =
    { Package: string
      Version: string
      /// By module name, sorted; each module's exports sorted by name.
      Modules: (string * ApiExport list) list }

/// How big a change to a public API is, smallest first: what semantic versioning makes the version say.
type Bump =
    /// Nothing another package sees changed.
    | PatchBump
    /// Something was added, or made more general: whatever built against the old version builds against the new.
    | MinorBump
    /// Something was removed or changed: what built against the old version may not build against the new.
    | MajorBump

/// One difference between two versions' APIs, with the bump it needs.
type ApiChange = { Bump: Bump; Description: string }

/// What `nanyx bump` did: the version compared against, if a version was published before, the changes since it, and
/// the version the manifest says now, with its new text.
type Bumped =
    { Baseline: SemanticVersion option
      Changes: ApiChange list
      Version: string
      Manifest: string }

/// A package's public API, and the rules for the version a change to it needs, as Elm has them (#1470): `api.json` is
/// made from a package's exported modules, two versions' APIs are compared, and each difference needs a major, minor or
/// patch bump. Below 1.0.0, a change that needs a major bump needs a minor one, and any other a patch.
module PackageApi =
    let private kindName (kind: ApiKind) =
        match kind with
        | ApiDef -> "def"
        | ApiType -> "type"
        | ApiContext -> "context"

    /// The API of `package`, from the compile of each of its public modules, or the errors that keep it from compiling.
    let ofPackage (package: Package) : Result<PackageApi, Diagnostic list> =
        let modules = Package.modules package
        let compiled =
            modules.PublicModules
            |> List.sort
            |> List.choose (fun moduleName -> modules.Modules |> Map.tryFind moduleName |> Option.map (fun files -> moduleName, files))
            |> List.map (fun (moduleName, files) ->
                let file = List.head files
                moduleName, files, Compiler.compileSourceIn modules Linters.none file (File.ReadAllText file))
        match compiled |> List.collect (fun (_, _, result) -> result.Diagnostics |> List.filter Diagnostics.isError) with
        | _ :: _ as errors -> Result.Error(List.distinct errors)
        | [] ->
            let exportsOf (files: string list) (typed: TypedModule) =
                StandardLibraryDocs.exportsIn files typed (fun _ -> None)
                |> Map.values
                |> Seq.concat
                |> Seq.map (fun export ->
                    let symbol = export.Symbol
                    match Definition.typeDefinitionOf typed symbol with
                    | Some info when symbol.Kind = TypeDefinition ->
                        { Kind = (if info.IsContext then ApiContext else ApiType)
                          Name = symbol.Name
                          Signature = export.Written
                          Variances = if info.IsNominal && not info.IsContext then Some info.Variances else None }
                    | _ ->
                        // `def name: type`, of which the type is what matters.
                        let written = export.Written
                        let signature = match written.IndexOf ": " with -1 -> "" | at -> written.Substring(at + 2)
                        { Kind = ApiDef; Name = symbol.Name; Signature = signature; Variances = None })
                |> Seq.sortBy (fun export -> export.Name, kindName export.Kind)
                |> List.ofSeq
            Result.Ok
                { Package = package.Manifest.Name
                  Version = package.Manifest.Version
                  Modules = compiled |> List.map (fun (moduleName, files, result) -> moduleName, exportsOf files result.Typed.Value) }

    /// How `variance` is written in an `api.json`.
    let private varianceName (variance: Variance) =
        match variance with
        | Unused -> "unused"
        | Covariant -> "covariant"
        | Contravariant -> "contravariant"
        | Invariant -> "invariant"

    /// The variance `name` writes in an `api.json`.
    let private varianceOf (name: string) =
        match name with
        | "unused" -> Unused
        | "covariant" -> Covariant
        | "contravariant" -> Contravariant
        | _ -> Invariant

    /// `api` as `api.json` holds it.
    let toJson (api: PackageApi) : string =
        use stream = new MemoryStream()
        (
            // Types are read by people too: `->` stays as it is written.
            use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping))
            writer.WriteStartObject()
            writer.WriteString("package", api.Package)
            writer.WriteString("version", api.Version)
            writer.WriteStartArray "modules"
            for moduleName, exports in api.Modules do
                writer.WriteStartObject()
                writer.WriteString("name", moduleName)
                writer.WriteStartArray "exports"
                for export in exports do
                    writer.WriteStartObject()
                    writer.WriteString("kind", kindName export.Kind)
                    writer.WriteString("name", export.Name)
                    writer.WriteString((if export.Kind = ApiDef then "type" else "definition"), export.Signature)
                    match export.Variances with
                    | Some variances ->
                        writer.WriteStartArray "variances"
                        for variance in variances do
                            writer.WriteStringValue(varianceName variance)
                        writer.WriteEndArray()
                    | None -> ()
                    writer.WriteEndObject()
                writer.WriteEndArray()
                writer.WriteEndObject()
            writer.WriteEndArray()
            writer.WriteEndObject()
        )
        Text.Encoding.UTF8.GetString(stream.ToArray()).ReplaceLineEndings "\n" + "\n"

    /// The API `json`, the text of an `api.json`, records, or why it can't be read.
    let ofJson (json: string) : Result<PackageApi, string> =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement
            let text (element: JsonElement) (name: string) = element.GetProperty(name).GetString()
            let kindOf (name: string) =
                match name with
                | "def" -> ApiDef
                | "context" -> ApiContext
                | _ -> ApiType
            Result.Ok
                { Package = text root "package"
                  Version = text root "version"
                  Modules =
                    [ for moduleElement in root.GetProperty("modules").EnumerateArray() ->
                          text moduleElement "name",
                          [ for export in moduleElement.GetProperty("exports").EnumerateArray() ->
                                let kind = kindOf (text export "kind")
                                let variances =
                                    match export.TryGetProperty "variances" with
                                    | true, written -> Some [ for variance in written.EnumerateArray() -> varianceOf (variance.GetString()) ]
                                    | false, _ -> None
                                { Kind = kind
                                  Name = text export "name"
                                  Signature = text export (if kind = ApiDef then "type" else "definition")
                                  Variances = variances } ] ] }
        with
        | :? JsonException
        | :? Collections.Generic.KeyNotFoundException
        | :? InvalidOperationException as error -> Result.Error $"it is not an api.json: {error.Message}"

    // Comparing types, as written: a def's type is parsed as the parser parses an annotation, and a new type is more
    // general than an old one when the old one is the new one with its type variables replaced.

    /// The type `signature`, a def's type as an `api.json` writes it, as the parser reads it.
    let private parseType (signature: string) : TypeExpr option =
        match parseModuleFile "api.nyx" $"def api: {signature}" with
        | Result.Ok [ { Kind = Def(ValueDef(_, _, typeExpr, _)) } ] -> typeExpr
        | _ -> None

    /// The type definition `signature` writes, as the parser reads it.
    let private parseTypeDefinition (signature: string) =
        match parseModuleFile "api.nyx" signature with
        | Result.Ok [ { Kind = Def(TypeDef(_, modifiers, parameters, shape)) } ] -> Some(modifiers, parameters |> List.map fst, shape)
        | _ -> None

    /// `typeExpr` with each type variable `substitution` names replaced by what it gives.
    let rec private substitute (substitution: Map<string, TypeExprKind>) (typeExpr: TypeExpr) : TypeExpr =
        let go = substitute substitution
        let field (field: TypeRecordField) =
            match field with
            | TypeField(name, mutability, Some fieldType, defaultValue) -> TypeField(name, mutability, Some(go fieldType), defaultValue)
            | other -> other
        let kind =
            match typeExpr.Kind with
            | TypeVar name -> substitution |> Map.tryFind name |> Option.defaultValue typeExpr.Kind
            | TypeTuple items -> TypeTuple(List.map go items)
            | TypeRecord fields -> TypeRecord(List.map field fields)
            | TypeOpenRecord(fields, rest) -> TypeOpenRecord(List.map field fields, rest)
            | TypeApply(name, arguments) -> TypeApply(name, List.map go arguments)
            | TypeFunc(parameter, result) -> TypeFunc(go parameter, go result)
            | TypeTag(name, payload) -> TypeTag(name, Option.map go payload)
            | TypeUnion members -> TypeUnion(List.map go members)
            | TypeIntersection members -> TypeIntersection(List.map go members)
            | TypeContext contexts -> TypeContext(List.map go contexts)
            | TypeWithContext(contexts, body) -> TypeWithContext(List.map go contexts, go body)
            | other -> other
        { typeExpr with Kind = kind }

    /// The type variables of `general` bound so that it is `specific`, given those bound already, if they can be.
    let rec private matching (bound: Map<string, TypeExprKind>) (general: TypeExpr) (specific: TypeExpr) : Map<string, TypeExprKind> option =
        let all (generals: TypeExpr list) (specifics: TypeExpr list) =
            if generals.Length <> specifics.Length then None
            else List.fold2 (fun found g s -> found |> Option.bind (fun bound -> matching bound g s)) (Some bound) generals specifics
        let fieldType (field: TypeRecordField) =
            match field with
            | TypeField(name, _, fieldType, _) -> Some(name, fieldType)
            | TypeFieldAttribute _ -> None
        let fields (generals: TypeRecordField list) (specifics: TypeRecordField list) =
            let generals = generals |> List.choose fieldType |> List.sortBy fst
            let specifics = specifics |> List.choose fieldType |> List.sortBy fst
            if List.map fst generals <> List.map fst specifics then
                None
            else
                List.fold2
                    (fun found (_, g) (_, s) ->
                        match g, s with
                        | Some g, Some s -> found |> Option.bind (fun bound -> matching bound g s)
                        | None, None -> found
                        | _ -> None)
                    (Some bound)
                    generals
                    specifics
        match general.Kind, specific.Kind with
        | TypeVar name, kind ->
            match bound |> Map.tryFind name with
            | Some already when already = kind -> Some bound
            | Some _ -> None
            | None -> Some(bound |> Map.add name kind)
        | TypeApply(g, generals), TypeApply(s, specifics) when g = s -> all generals specifics
        | TypeTuple generals, TypeTuple specifics
        | TypeUnion generals, TypeUnion specifics
        | TypeIntersection generals, TypeIntersection specifics
        | TypeContext generals, TypeContext specifics -> all generals specifics
        | TypeFunc(gp, gr), TypeFunc(sp, sr) -> all [ gp; gr ] [ sp; sr ]
        | TypeTag(g, Some gp), TypeTag(s, Some sp) when g = s -> matching bound gp sp
        | TypeWithContext(gc, gb), TypeWithContext(sc, sb) -> all (gb :: gc) (sb :: sc)
        | TypeRecord gf, TypeRecord sf -> fields gf sf
        | TypeOpenRecord(gf, gr), TypeOpenRecord(sf, sr) when gr.IsSome = sr.IsSome -> fields gf sf
        | g, s -> if g = s then Some bound else None

    /// A type's contexts, written before it, `[Eq(a)]`, and the type after them.
    let private splitContexts (typeExpr: TypeExpr) =
        match typeExpr.Kind with
        | TypeWithContext(contexts, body) -> contexts, body
        | _ -> [], typeExpr

    /// How the def `name`'s type changed from `oldSignature` to `newSignature`: no change another package can see, a
    /// requirement dropped or a type made more general, which needs a minor bump, or anything else, which needs a major
    /// one.
    let private defChanges (name: string) (oldSignature: string) (newSignature: string) : ApiChange list =
        let changed = { Bump = MajorBump; Description = $"the type of {name} changed from {oldSignature} to {newSignature}" }
        match parseType oldSignature, parseType newSignature with
        | Some oldType, Some newType ->
            let oldContexts, oldBody = splitContexts oldType
            let newContexts, newBody = splitContexts newType
            match matching Map.empty newBody oldBody with
            | None -> [ changed ]
            | Some bound ->
                let written (contexts: TypeExpr list) = contexts |> List.map (fun context -> context.Kind)
                let newContexts = newContexts |> List.map (substitute bound) |> written
                let oldContexts = written oldContexts
                let required = newContexts |> List.filter (fun context -> not (List.contains context oldContexts))
                let dropped = oldContexts |> List.filter (fun context -> not (List.contains context newContexts))
                let isRenaming =
                    let targets = bound |> Map.values |> List.ofSeq
                    targets |> List.forall (function TypeVar _ -> true | _ -> false) && (List.distinct targets).Length = targets.Length
                if not required.IsEmpty then
                    [ { Bump = MajorBump; Description = $"{name} requires a context it didn't: its type changed from {oldSignature} to {newSignature}" } ]
                elif not dropped.IsEmpty then
                    [ { Bump = MinorBump; Description = $"{name} no longer requires a context: its type changed from {oldSignature} to {newSignature}" } ]
                elif not isRenaming then
                    [ { Bump = MinorBump; Description = $"the type of {name} was made more general, from {oldSignature} to {newSignature}" } ]
                else
                    []
        | _ when oldSignature = newSignature -> []
        | _ -> [ changed ]

    /// The names of the fields of `fields`.
    let private fieldNames (fields: TypeRecordField list) =
        fields |> List.choose (function TypeField(name, _, _, _) -> Some name | TypeFieldAttribute _ -> None)

    /// The names of the tags of a union, `#a | #b(int)`, or of one tag, `#a`; none for another type.
    let private tagNames (shape: TypeExpr) =
        let members =
            match shape.Kind with
            | TypeUnion members -> members
            | TypeTag _ -> [ shape ]
            | _ -> []
        members |> List.choose (fun memberType -> match memberType.Kind with TypeTag(name, _) -> Some name | _ -> None)

    /// How the type `name`'s definition changed from `oldDefinition` to `newDefinition`. Any change to an exported
    /// type's definition needs a major bump: each is described as what it is, where it can be told.
    let private typeChanges (name: string) (oldDefinition: string) (newDefinition: string) : ApiChange list =
        let major description = { Bump = MajorBump; Description = description }
        let changed = major $"the definition of {name} changed from {oldDefinition} to {newDefinition}"
        match parseTypeDefinition oldDefinition, parseTypeDefinition newDefinition with
        | Some (oldModifiers, oldParameters, oldShape), Some (newModifiers, newParameters, newShape) ->
            let hidden =
                [ Private, "private"; Protected, "protected" ]
                |> List.filter (fun (modifier, _) -> List.contains modifier newModifiers && not (List.contains modifier oldModifiers))
                |> List.map (fun (_, word) -> major $"{name} was made {word}")
            if oldParameters.Length <> newParameters.Length then
                let written (parameters: string list) = String.concat ", " parameters
                hidden @ [ major $"the type parameters of {name} changed from ({written oldParameters}) to ({written newParameters})" ]
            else
                // A parameter renamed is the same type.
                let renamed = List.zip newParameters (oldParameters |> List.map TypeVar) |> Map.ofList
                let newShape = substitute renamed newShape
                let shapes =
                    match oldShape.Kind, newShape.Kind with
                    | _ when oldShape = newShape -> []
                    | (TypeRecord oldFields | TypeOpenRecord(oldFields, _)), (TypeRecord newFields | TypeOpenRecord(newFields, _)) ->
                        let oldNames, newNames = fieldNames oldFields, fieldNames newFields
                        let removed = oldNames |> List.filter (fun field -> not (List.contains field newNames))
                        let added = newNames |> List.filter (fun field -> not (List.contains field oldNames))
                        match removed, added with
                        | [], [] -> [ changed ]
                        | _ ->
                            (removed |> List.map (fun field -> major $"the field {field} was removed from {name}"))
                            @ (added |> List.map (fun field -> major $"the field {field} was added to {name}, so a record of it written without it no longer is one"))
                    | _ when not (tagNames oldShape).IsEmpty && not (tagNames newShape).IsEmpty ->
                        let oldTags, newTags = tagNames oldShape, tagNames newShape
                        let removed = oldTags |> List.filter (fun tag -> not (List.contains tag newTags))
                        let added = newTags |> List.filter (fun tag -> not (List.contains tag oldTags))
                        match removed, added with
                        | [], [] -> [ changed ]
                        | _ ->
                            (removed |> List.map (fun tag -> major $"the tag #{tag} was removed from {name}"))
                            @ (added |> List.map (fun tag -> major $"the tag #{tag} was added to {name}, so a match on it that names each tag is no longer exhaustive"))
                    | _ -> [ changed ]
                let hiddenOnly = not hidden.IsEmpty && oldShape.Kind = newShape.Kind
                if hiddenOnly then hidden else hidden @ shapes
        | _ when oldDefinition = newDefinition -> []
        | _ -> [ changed ]

    /// Whether a parameter that was of variance `before` and is of `after` takes everything it took: one that is unused
    /// takes any argument, an invariant one takes only the same, and a covariant and a contravariant one each take what
    /// the other doesn't.
    let private takesAll (before: Variance) (after: Variance) =
        before = after || after = Unused || before = Invariant

    /// How the variances of the type `name`'s parameters changed from `before` to `after`, as both `api.json`s record
    /// them, its parameters named as its new definition, `definition`, writes them: a parameter that takes less than it
    /// did needs a major bump, since a value given for one at another argument may no longer be, and one that takes more
    /// a minor bump. Nothing when either doesn't record them.
    let private varianceChanges (name: string) (definition: string) (before: Variance list option) (after: Variance list option) : ApiChange list =
        match before, after with
        | Some before, Some after when before.Length = after.Length && before <> after ->
            let parameters =
                match parseTypeDefinition definition with
                | Some (_, parameters, _) when parameters.Length = after.Length -> parameters
                | _ -> List.init after.Length (fun index -> $"#{index + 1}")
            List.zip3 parameters before after
            |> List.filter (fun (_, old, current) -> old <> current)
            |> List.map (fun (parameter, old, current) ->
                let was = $"the parameter {parameter} of {name} was {varianceName old} and is {varianceName current}"
                if takesAll old current then
                    { Bump = MinorBump; Description = $"{was}, so a value of it is given where it wasn't" }
                else
                    { Bump = MajorBump; Description = $"{was}, so a value of it at one argument may no longer be given where one at another is expected" })
        | _ -> []

    /// The changes from the API `before` to the API `after`, each with the bump it needs: a module or an export removed
    /// needs a major bump, one added a minor bump, and a changed one what `defChanges` or `typeChanges` says, and for a
    /// nominal type what `varianceChanges` says of its parameters.
    let compare (before: PackageApi) (after: PackageApi) : ApiChange list =
        let oldModules, newModules = Map.ofList before.Modules, Map.ofList after.Modules
        let removedModules =
            before.Modules
            |> List.filter (fun (moduleName, _) -> not (newModules.ContainsKey moduleName))
            |> List.map (fun (moduleName, _) -> { Bump = MajorBump; Description = $"the module {moduleName} was removed, or is no longer exported" })
        let addedModules =
            after.Modules
            |> List.filter (fun (moduleName, _) -> not (oldModules.ContainsKey moduleName))
            |> List.map (fun (moduleName, _) -> { Bump = MinorBump; Description = $"the module {moduleName} was added" })
        let inBoth =
            before.Modules
            |> List.choose (fun (moduleName, oldExports) -> newModules |> Map.tryFind moduleName |> Option.map (fun newExports -> moduleName, oldExports, newExports))
            |> List.collect (fun (moduleName, oldExports, newExports) ->
                let key (export: ApiExport) = export.Kind = ApiDef, export.Name
                let shown (export: ApiExport) = $"{moduleName}'s {kindName export.Kind} {export.Name}"
                let removed =
                    oldExports
                    |> List.filter (fun export -> not (newExports |> List.exists (fun other -> key other = key export)))
                    |> List.map (fun export -> { Bump = MajorBump; Description = $"{shown export} was removed, or is no longer exported" })
                let added =
                    newExports
                    |> List.filter (fun export -> not (oldExports |> List.exists (fun other -> key other = key export)))
                    |> List.map (fun export -> { Bump = MinorBump; Description = $"{shown export} was added" })
                let changed =
                    oldExports
                    |> List.collect (fun oldExport ->
                        match newExports |> List.tryFind (fun other -> key other = key oldExport) with
                        | Some newExport ->
                            let written =
                                if newExport.Signature = oldExport.Signature && newExport.Kind = oldExport.Kind then []
                                elif oldExport.Kind = ApiDef then defChanges (shown oldExport) oldExport.Signature newExport.Signature
                                else typeChanges (shown oldExport) oldExport.Signature newExport.Signature
                            written @ varianceChanges (shown oldExport) newExport.Signature oldExport.Variances newExport.Variances
                        | None -> [])
                removed @ changed @ added)
        removedModules @ inBoth @ addedModules

    /// The biggest bump `changes` need: a patch when there are none.
    let required (changes: ApiChange list) : Bump =
        changes |> List.map _.Bump |> List.fold max PatchBump

    /// The version after `version` that a change needing `bump` makes. Below 1.0.0, a major change bumps the minor
    /// version and any other the patch.
    let bumped (version: SemanticVersion) (bump: Bump) : SemanticVersion =
        match bump with
        | MajorBump when version.Major = 0 -> SemanticVersion.nextMinor version
        | MajorBump -> SemanticVersion.nextMajor version
        | MinorBump when version.Major = 0 -> SemanticVersion.nextPatch version
        | MinorBump -> SemanticVersion.nextMinor version
        | PatchBump -> SemanticVersion.nextPatch version

    /// Whether going from `before` to `after` is a bump at least as big as `bump`.
    let allows (before: SemanticVersion) (after: SemanticVersion) (bump: Bump) : bool =
        after > before && after >= { bumped before bump with PreRelease = after.PreRelease }

    /// What is wrong with publishing `after`, whose API is `afterApi`, when the newest version published below it is
    /// `before`, whose API is `beforeApi`: a version not after it, or a bump too small for the changes, each of which
    /// is listed. What `nanyx publish` refuses (#865).
    let versionErrors (before: SemanticVersion, beforeApi: PackageApi) (after: SemanticVersion, afterApi: PackageApi) : string list =
        let changes = compare beforeApi afterApi
        let needed = required changes
        if after <= before then
            [ $"{afterApi.Package} {after} is not after {before}, the newest version published" ]
        elif allows before after needed then
            []
        else
            let least = bumped before needed
            $"{afterApi.Package} {after} is too small a bump from {before} for what changed, which needs {least} at least:"
            :: (changes |> List.filter (fun change -> change.Bump = needed) |> List.map (fun change -> $"  {change.Description}"))

    /// `api.json`, the name of the file a packed package holds its API in.
    let fileName = "api.json"

    /// The API the packed package `bytes` holds in its `api.json`, at the zip's root; or why it has none to read. `what`
    /// names the package, to say so.
    let ofArchive (what: string) (bytes: byte array) : Result<PackageApi, string> =
        try
            use zip = new IO.Compression.ZipArchive(new MemoryStream(bytes), IO.Compression.ZipArchiveMode.Read)
            match zip.GetEntry fileName with
            | null -> Result.Error $"{what} has no {fileName}, so its API can't be compared: pack it with `nanyx pack`, which writes one"
            | entry ->
                use reader = new StreamReader(entry.Open())
                ofJson (reader.ReadToEnd()) |> Result.mapError (fun message -> $"The {fileName} of {what} can't be read: {message}")
        with :? InvalidDataException ->
            Result.Error $"{what} is not a packed package, which is a zip"

    /// Compares `older` with `newer`, two versions of a package as `api.json` records them, as the package index does
    /// before it records `newer`: the changes between them, if `newer`'s version is a big enough bump for them (see
    /// `versionErrors`); or what is wrong, each a line, ending with what to do about it.
    let checkVersions (older: PackageApi) (newer: PackageApi) : Result<ApiChange list, string list> =
        match SemanticVersion.tryParse older.Version, SemanticVersion.tryParse newer.Version with
        | _ when older.Package <> newer.Package ->
            Result.Error [ $"The older package is {older.Package}, and the newer {newer.Package}: give two versions of one package" ]
        | Some before, Some after ->
            match versionErrors (before, older) (after, newer) with
            | [] -> Result.Ok(compare older newer)
            | errors -> Result.Error(errors @ [ $"Run `nanyx bump` in {newer.Package} to set the version its changes need, then pack and publish that version" ])
        | None, _ -> Result.Error [ $"{older.Package}'s version {older.Version} is not a semantic version" ]
        | _, None -> Result.Error [ $"{newer.Package}'s version {newer.Version} is not a semantic version" ]

    /// `package` packed as `PackageArchive.pack` packs it, with its API in `api.json`; or what keeps it from being packed,
    /// a module that doesn't compile among them.
    let pack (package: Package) : Result<byte array, Diagnostic list> =
        match PackageArchive.packErrors package.Manifest with
        | _ :: _ as errors -> Result.Error errors
        | [] ->
            ofPackage package
            |> Result.bind (fun api -> PackageArchive.packWith [ fileName, Text.Encoding.UTF8.GetBytes(toJson api) ] package)

    /// The newest version of the package `name` published at or below `version`, from `store`'s index, with its API,
    /// read from the `api.json` its `.nyxpkg` holds; none if none was published.
    let private baseline (store: PackageStore) (name: string) (version: SemanticVersion) : Result<(SemanticVersion * PackageApi) option, string> =
        PackageIndex.versions store.Index name
        |> Result.bind (fun (_, listed) ->
            match listed |> List.filter (fun listedVersion -> listedVersion.Version <= version && listedVersion.Available && not listedVersion.Unlisted) |> List.tryLast with
            | None -> Result.Ok None
            | Some newest ->
                PackageIndex.download store { Name = name; Version = newest.Version; Sha256 = newest.Sha256; Dependencies = [] }
                |> Result.bind (fun directory ->
                    let file = Path.Combine(directory, fileName)
                    if File.Exists file then
                        ofJson (File.ReadAllText file)
                        |> Result.map (fun api -> Some(newest.Version, api))
                        |> Result.mapError (fun message -> $"the {fileName} of {name} {newest.Version} can't be read: {message}")
                    else
                        Result.Error $"{name} {newest.Version} has no {fileName}, so there is nothing to compare against"))

    /// `text`, a manifest's, with its package's `version` written as `version`.
    let private withVersion (manifestPath: string) (text: string) (version: string) =
        match DataValue.read manifestPath text with
        | Result.Ok { Data = DataRecord fields } ->
            match DataValue.field "package" fields with
            | Some { DataField.Value = { Data = DataRecord package } } ->
                match DataValue.field "version" package with
                | Some field -> text.Substring(0, field.Value.Location.Start) + $"\"{version}\"" + text.Substring field.Value.Location.End
                | None -> text
            | _ -> text
        | _ -> text

    /// Bumps the version of `package` to the one its API's changes need: compared against the newest version of it
    /// published at or below the one its manifest says, from `store`'s index, its version is that one bumped as
    /// `required` says, unless the manifest says a later version already that is a big enough bump. With nothing
    /// published, the version stays.
    let bump (store: PackageStore) (package: Package) : Result<Bumped, string list> =
        let manifest = package.Manifest
        let text = (File.ReadAllText manifest.Path).ReplaceLineEndings "\n"
        match SemanticVersion.tryParse manifest.Version with
        | None -> Result.Error [ $"{manifest.Name}'s version {manifest.Version} is not a semantic version" ]
        | Some current ->
            baseline store manifest.Name current
            |> Result.mapError List.singleton
            |> Result.bind (fun found ->
                match found with
                | None -> Result.Ok { Baseline = None; Changes = []; Version = manifest.Version; Manifest = text }
                | Some (before, beforeApi) ->
                    ofPackage package
                    |> Result.mapError (List.map Manifest.describe)
                    |> Result.map (fun api ->
                        let changes = compare beforeApi api
                        let needed = required changes
                        // A version the author chose already, big enough for the changes, stays.
                        let version = if current > before && allows before current needed then current else bumped before needed
                        let version = string version
                        { Baseline = Some before; Changes = changes; Version = version; Manifest = withVersion manifest.Path text version }))

    /// What `nanyx publish` checks of `package`'s version against `store`'s index (#865): that it isn't published
    /// already, and that it is a big enough bump from the newest version published below it (`versionErrors`). Gives
    /// that version, with the changes since it; none for a package's first version. Or what is wrong, each a line.
    let publishCheck (store: PackageStore) (package: Package) : Result<(SemanticVersion * ApiChange list) option, string list> =
        let manifest = package.Manifest
        match SemanticVersion.tryParse manifest.Version with
        | None -> Result.Error [ $"{manifest.Name}'s version {manifest.Version} is not a semantic version" ]
        | Some version ->
            PackageIndex.versions store.Index manifest.Name
            |> Result.mapError List.singleton
            |> Result.bind (fun (_, listed) ->
                if listed |> List.exists (fun published -> published.Version = version) then
                    Result.Error [ $"{manifest.Name} {version} is published already, and a published version can't change: run `nanyx bump` to choose the next version" ]
                else
                    baseline store manifest.Name version |> Result.mapError List.singleton)
            |> Result.bind (function
                | None -> Result.Ok None
                | Some(before, beforeApi) ->
                    ofPackage package
                    |> Result.mapError (List.map Manifest.describe)
                    |> Result.bind (fun api ->
                        match versionErrors (before, beforeApi) (version, api) with
                        | [] -> Result.Ok(Some(before, compare beforeApi api))
                        | errors -> Result.Error(errors @ [ "Run `nanyx bump` to set the version these changes need" ])))
