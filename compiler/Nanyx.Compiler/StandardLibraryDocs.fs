/// The standard library's reference, as the docs site shows it (docs/src/content/stdlib): a page for each file of the
/// library, and on it the names the file exports, in the order it declares them, each with its signature and doc
/// comment. It is read from the compile of each module that the compiler embeds, and of each public module of the
/// packages the compiler ships beside it, but for the hosts (see `ShippedPackages`), whose modules a program doesn't
/// import; and, for a page that pins a published package in its front matter, `package: nanyx.json 0.1.0`, from that
/// version of the package (see `pinOf`). The reference sits on its page between `beginMarkerOf` its pin and `endMarker`, and the
/// rest of the page is written by hand. Beside the pages, a list of the nominal types they name (`nominalTypesFile`).
module NyxCompiler.StandardLibraryDocs

open System
open System.IO
open System.Text.RegularExpressions
open Parser.Program

let beginMarker = "<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->"

/// The line a page's reference starts after: `beginMarker`, or, on a page that pins a published package (see `pinOf`),
/// one that names the package.
let beginMarkerOf (pin: (string * SemanticVersion) option) =
    match pin with
    | Some (name, version) ->
        $"<!-- The reference below is generated from the doc comments of {name} {version}, which this page pins, by `nanyx stdlib-docs`: edit those, in the package's repository, not this. -->"
    | None -> beginMarker

let endMarker = "<!-- End of the generated reference. -->"

/// The page of the library's file `file` (its resource name, as `nanyx/prelude-lists.nyx`, or the path of a file of a
/// shipped package): its name without the package, the directory and the extension, with a `-` for each `.`, so
/// `nanyx/host.runtime.nyx` is on `host-runtime`, and `packages/nanyx.cli/src/env.nyx` on `env`.
let pageOf (file: string) =
    Path.GetFileNameWithoutExtension(file).Replace('.', '-')

/// What a page's reference lists: an exported def, type or context, its doc comment, and, for a context, its
/// members, each with its doc comment.
type internal Export =
    { Symbol: Symbol
      Written: string
      Doc: string option
      Members: (string * string option) list }

/// An entry of a page's reference: an export (see `Export`), or a dimension or a unit, which has no symbol of its own
/// (see `unitEntries`). `Declaration` is where its name is declared, which a link to it leads to.
type private Entry =
    { Name: string
      Declaration: SourceSpan
      Written: string
      Doc: string option
      Members: (string * string option) list }

/// `export` as an entry of a page's reference.
let private entryOf (export: Export) : Entry =
    { Name = export.Symbol.Name
      Declaration = export.Symbol.Declaration
      Written = export.Written
      Doc = export.Doc
      Members = export.Members }

/// A name written as source writes it: an operator in backticks, after the type it is attached to if it is, as
/// `` `==` `` or `` int.`+` ``, and the prelude's sequence type by its alias, `seq.map` (see `Seqs`).
let private writtenName (name: string) =
    let name = Seqs.shownName name
    let at = name.LastIndexOf '.'
    let qualifier, own = if at > 0 then name.Substring(0, at + 1), name.Substring(at + 1) else "", name
    if Regex.IsMatch(own, @"^[A-Za-z_][A-Za-z0-9_]*$") then name else $"{qualifier}`{own}`"

/// `text` as inline code: in double backticks, with a space inside each, if it has a backtick of its own.
let private code (text: string) =
    if text.Contains '`' then $"`` {text} ``" else $"`{text}`"

/// The id the docs site gives a heading of `text`: lower case, without anything but letters, digits, `_`, `-` and
/// spaces, and with a `-` for each space, as GitHub's slugger makes it.
let private slug (text: string) =
    Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{M}\p{N}_\- ]", "").Replace(' ', '-')

/// What an entry's heading names: a custom pattern by its tags, `#Even | #Odd`, and anything else by its name as
/// source writes it.
let private headingName (name: string) =
    Signatures.patternName name |> Option.defaultWith (fun () -> writtenName name)

/// The ids of the headings of `exports`, in order: a heading's slug, with `-1`, `-2`, ... after it when an earlier one
/// has the same, as the site numbers them. A pattern's is its heading's, as `#Even | #Odd` is `even--odd`.
let private anchors (exports: Entry list) : (Entry * string) list =
    exports
    |> List.mapFold (fun (seen: Map<string, int>) export ->
        let name = export.Name
        let id = slug (Signatures.patternName name |> Option.defaultValue name)
        match seen |> Map.tryFind id with
        | Some count -> (export, $"{id}-{count}"), seen |> Map.add id (count + 1)
        | None -> (export, id), seen |> Map.add id 1) Map.empty
    |> fst

/// A def's signature, as hover writes its declaration: `def name: type`, after the contexts it requires. A custom
/// pattern's is its declaration's, `pattern string -> #Even | #Odd`, with `_` for no match (see
/// `Signatures.patternDeclaration`).
let private defSignature (typed: TypedModule) (symbol: Symbol) =
    match Hover.declaredType typed symbol with
    | Some (ty, contexts) ->
        match Signatures.patternDeclaration symbol.Name (Signatures.typeWithContexts typed.TypeDefs Source ty []) with
        | Some pattern -> pattern
        | None ->
            let contexts = contexts |> FunctionContexts.flatten |> Unifier.writtenContexts
            $"def {writtenName symbol.Name}: {Signatures.typeWithContexts typed.TypeDefs Source ty contexts}"
    | None -> $"def {writtenName symbol.Name}"

/// A type's definition, as hover writes it, but for a context's members: one to a line, in the order it declares them.
let private typeSignature (typed: TypedModule) (symbol: Symbol) (info: TypeDefInfo) (members: Symbol list) =
    let typeDefs = Definition.typeDefsBesides info typed.TypeDefs
    let writeShape (shape: Ty) =
        match shape with
        | TyRecord(fields, _) when info.IsContext && not members.IsEmpty ->
            let written = members |> List.choose (fun memberSymbol -> fields |> Map.tryFind memberSymbol.Name |> Option.map (fun ty -> memberSymbol.Name, ty))
            let types = Unifier.tysToDisplayStringsIn typeDefs Source (List.map snd written)
            // A context's associated types come first, as it declares them (see `AssociatedTypes`).
            let associated = info.AssociatedTypes |> List.map (fun name -> $"  {name}: type")
            let lines = associated @ List.map2 (fun (name, _) ty -> $"  {writtenName name}: {ty}") written types
            "(\n" + String.concat "\n" lines + "\n)"
        | _ -> Unifier.tyToDisplayStringIn typeDefs Source shape
    fst (Definition.writtenTypeDefinition writeShape symbol info true)

/// The compile of the standard-library module `moduleName`, as an import of it checks it: from the checked library (see
/// `Compiler.compileStandardLibraryModuleReporting`).
/// Whether `symbol` of `typed` is a dimension's, which its `dimension` line documents (see `unitEntries`).
let private isDimension (typed: TypedModule) (symbol: Symbol) =
    Definition.typeDefinitionOf typed symbol |> Option.exists _.IsDimension

let internal compile (moduleName: string) : CompileResult =
    Compiler.compileStandardLibraryModuleReporting CompileListener.none moduleName

/// The names the module `moduleName`, compiled as `typed`, exports, by the file of it that declares each, in the order
/// the file declares them, with their doc comments as `docOf` writes them. The names it reserves are left out.
let rec internal exportsOf (moduleName: string) (typed: TypedModule) (docOf: Symbol -> string option) : Map<string, Export list> =
    exportsIn (StandardLibrary.moduleFiles moduleName) typed docOf

/// `exportsOf` a module whose files are `files`.
and internal exportsIn (files: string list) (typed: TypedModule) (docOf: Symbol -> string option) : Map<string, Export list> =
    let files = Set.ofList files
    typed.Symbols
    |> Map.values
    |> Seq.filter (fun symbol ->
        symbol.IsExported
        && (symbol.Kind = TopLevelValue || symbol.Kind = TypeDefinition)
        && files.Contains symbol.Declaration.File
        && not (ReservedNames.isHidden symbol)
        // A dimension is written as its `dimension` line declares it (see `unitEntries`), not as the type it is.
        && not (isDimension typed symbol))
    |> Seq.sortBy (fun symbol -> symbol.Declaration.File, symbol.Declaration.Start)
    |> Seq.map (fun symbol ->
        let export =
            match Definition.typeDefinitionOf typed symbol with
            | Some info when symbol.Kind = TypeDefinition ->
                let members =
                    info.Members
                    |> Map.values
                    |> Seq.choose typed.Symbols.TryFind
                    |> Seq.sortBy _.Declaration.Start
                    |> List.ofSeq
                { Symbol = symbol
                  Written = typeSignature typed symbol info members
                  Doc = docOf symbol
                  Members = members |> List.map (fun memberSymbol -> memberSymbol.Name, docOf memberSymbol) }
            | _ -> { Symbol = symbol; Written = defSignature typed symbol; Doc = docOf symbol; Members = [] }
        symbol.Declaration.File, export)
    |> Seq.groupBy fst
    |> Seq.map (fun (file, exports) -> file, exports |> Seq.map snd |> List.ofSeq)
    |> Map.ofSeq

/// The source of `file`: a file on disk, or the standard library's file of that resource name.
let private sourceOf (file: string) : string option =
    if Path.IsPathRooted file then (if File.Exists file then Some (File.ReadAllText file) else None)
    else StandardLibrary.tryLoadFile file

/// The dimensions and units `file` exports at its top level, in the order it declares them, each written as its
/// declaration is, but for `export`, with its doc comment as `docOf` writes the one of the name declared at a span.
/// They have no symbols (see `TypedModule.Units`), so they are read from the file's syntax.
let private unitEntries (file: string) (docOf: SourceSpan -> string option) : Entry list =
    match sourceOf file |> Option.map (fun source -> source.ReplaceLineEndings "\n", parseModuleFileWithDocs file source) with
    | Some (source, Ok ((items, _, _), docs)) ->
        let textOf (span: SourceSpan) = source.Substring(span.Start, span.End - span.Start)
        let declaredIn (item: TopLevelItem) (name: string) =
            docs.Value.TopLevel
            |> List.tryFind (fun (declared, span) -> declared = name && item.Span.Start <= span.Start && span.End <= item.Span.End)
            |> Option.map snd
        let entry (written: string) (item: TopLevelItem) (name: string) =
            declaredIn item name
            |> Option.map (fun declaration -> { Name = name; Declaration = declaration; Written = written; Doc = docOf declaration; Members = [] })
        let marked = UnitSyntax.unitsWithAttributes items
        items
        |> List.choose (fun item ->
            match item.Kind with
            | DimensionDecl(true, name, storage, derived) ->
                let storage = storage |> Option.map (fun storage -> $"({textOf storage.Span})") |> Option.defaultValue ""
                let derived = derived |> Option.map (fun derived -> $" = {textOf derived.Span}") |> Option.defaultValue ""
                entry $"{UnitSyntax.dimensionKeyword} {name}{storage}{derived}" item name
            | UnitDecl(true, name, dimension, multiple) ->
                let attributes =
                    marked
                    |> List.tryFind (fun (unit, _) -> obj.ReferenceEquals(unit, item))
                    |> Option.map (snd >> List.map (fun attribute -> $"@{attribute} ") >> String.concat "")
                    |> Option.defaultValue ""
                let multiple = multiple |> Option.map (fun multiple -> $" = {textOf multiple.Span}") |> Option.defaultValue ""
                entry $"{attributes}{UnitSyntax.unitKeyword} {name}: {textOf dimension.Span}{multiple}" item name
            | _ -> None)
    | _ -> []

/// The entries of each of `files`, those of a module compiled as `typed`, by file, in the order the file declares
/// them: its exports (see `exportsIn`) and its dimensions and units (see `unitEntries`), with their doc comments as
/// `reader` writes them.
let private entriesIn (files: string list) (typed: TypedModule) (reader: Documentation.Reader option) : Map<string, Entry list> =
    let exports = exportsIn files typed (fun symbol -> reader |> Option.bind (fun reader -> reader.ForSymbol symbol))
    let docOf (declaration: SourceSpan) = reader |> Option.bind (fun reader -> reader.ForDeclaration declaration)
    files
    |> List.choose (fun file ->
        let entries =
            (exports |> Map.tryFind file |> Option.defaultValue [] |> List.map entryOf) @ unitEntries file docOf
            |> List.sortBy _.Declaration.Start
        if entries.IsEmpty then None else Some (file, entries))
    |> Map.ofList

/// `text` with each line but the first indented by `indent` spaces, as a list item's continuation.
let private indented (indent: int) (text: string) =
    text.Split('\n') |> Array.mapi (fun i line -> if i = 0 || line = "" then line else String(' ', indent) + line) |> String.concat "\n"

/// The reference of one page: the module's doc comment, `moduleDoc`, if this is the file named for the module, then
/// each export with its heading (`anchors`).
let private render (moduleDoc: string option) (exports: (Entry * string) list) =
    let entry (export: Entry, _) =
        let members =
            export.Members
            |> List.choose (fun (name, doc) -> doc |> Option.map (fun doc -> $"- {code (writtenName name)}: {indented 2 doc}"))
        [ $"## {code (headingName export.Name)}"
          $"```nanyx fragment\n{export.Written}\n```"
          yield! Option.toList export.Doc
          if not members.IsEmpty then String.concat "\n" members ]
    Option.toList moduleDoc @ (exports |> List.collect entry) |> String.concat "\n\n"

/// A module whose reference is written: its files, its compile, the file it is compiled from, whose doc comment is the
/// module's, and, for a module of a published package, the package and the version its pages pin.
type private Documented =
    { Files: string list
      Result: CompileResult
      ModuleFile: string
      Pin: (string * SemanticVersion) option }

/// The public modules of `modules`, a package's, each compiled as a program that imports it checks it, from the package
/// `pin` names if it is a published one.
let private publicModules (pin: (string * SemanticVersion) option) (modules: PackageModules) : Documented list =
    modules.PublicModules
    |> List.choose (fun moduleName -> modules.Modules |> Map.tryFind moduleName)
    |> List.choose (function
        | file :: _ as files ->
            Some
                { Files = files
                  Result = Compiler.compileSourceIn modules Linters.none file (File.ReadAllText file)
                  ModuleFile = file
                  Pin = pin }
        | [] -> None)

/// The modules whose reference is written: each of the standard library's, and each public module of a package the
/// compiler ships but for the hosts' and the host libraries', compiled as a program that imports it checks it.
let private documented () : Documented list =
    let library =
        StandardLibrary.preludeModuleName :: StandardLibrary.moduleNames
        |> List.map (fun moduleName ->
            { Files = StandardLibrary.moduleFiles moduleName
              Result = compile moduleName
              ModuleFile = StandardLibrary.resourceName moduleName
              Pin = None })
    let packages =
        ShippedPackages.names ()
        // A host is what programs run under, not what they call.
        |> List.filter (fun name -> Manifest.tryReadIn (ShippedPackages.directoryOf name) |> Option.exists (fun manifest -> manifest.Declares.IsNone))
        |> List.choose ShippedPackages.tryModules
        // A host library is what hosts are written on, which a program can't call (see `HostLibraries`).
        |> List.filter (fun modules -> not modules.HostLibrary)
        |> List.collect (publicModules None)
    library @ packages

/// The package and version the page `text` pins in its front matter, `package: nanyx.json 0.1.0`, whose reference it
/// shows rather than the standard library's; none if it pins none; or what is wrong with the pin.
let pinOf (text: string) : Result<(string * SemanticVersion) option, string> =
    let lines = text.ReplaceLineEndings("\n").Split('\n')
    let frontMatter =
        if lines.Length > 0 && lines[0] = "---" then
            lines |> Array.skip 1 |> Array.takeWhile ((<>) "---")
        else
            [||]
    match frontMatter |> Array.tryFind (fun line -> line.StartsWith "package:") with
    | None -> Ok None
    | Some line ->
        let written = line.Substring("package:".Length).Trim().Trim('"')
        match written.Split(' ', StringSplitOptions.RemoveEmptyEntries) with
        | [| name; version |] when (SemanticVersion.tryParse version).IsSome -> Ok(Some(name, (SemanticVersion.tryParse version).Value))
        | _ -> Error $"pins its package as '{written}', but a pin is a package and its exact version, `package: nanyx.json 0.1.0`"

/// The errors of `documented`, the public modules of the published package in `packageDirectory`, each on one line at
/// its place in the package, if any of them doesn't compile.
let private compileErrors (packageDirectory: string) (documented: Documented list) : string option =
    let line (diagnostic: Diagnostic) =
        let place =
            match diagnostic.Span with
            | Some span when span.File <> "" ->
                $"{Path.GetRelativePath(packageDirectory, span.File).Replace('\\', '/')}:{span.StartLine + 1}:{span.StartColumn + 1}: "
            | _ -> ""
        $"{place}error {diagnostic.Code}: {Diagnostics.oneLine diagnostic}"
    let errors =
        documented
        |> List.collect _.Result.Diagnostics
        |> List.filter (fun diagnostic -> diagnostic.Severity = Severity.Error || diagnostic.Severity = Severity.Critical)
        |> List.map line
        |> List.distinct
    if errors.IsEmpty && documented |> List.forall _.Result.Typed.IsSome then None
    else Some(if errors.IsEmpty then "it doesn't compile" else String.concat "; " errors)

/// The public modules of each published package that `pins` names at a version (see `pinOf`), read from the package
/// cache of `store`, and downloaded into it from its index first if it isn't there, as a script's pins are (see
/// `ScriptDependencies`); or, for a pin, why it can't be: it can't be downloaded or read, or a public module of it
/// doesn't compile. `directory` is where the pages are.
let private published (store: PackageStore) (directory: string) (pins: (string * SemanticVersion) list) =
    pins
    |> List.map (fun (name, version) ->
        let pin = name, version
        // The pin is a script's beside the pages, as its messages name it.
        let script = Path.Combine(directory, "stdlib-docs.nyx")
        let dependency =
            { Name = name
              Source = FromIndex(VersionRequirement.at version)
              NameSpan = SourceSpan.none
              SourceSpan = SourceSpan.none }
        let pulled =
            if Directory.Exists(PackageStore.directoryOf store name version) then Ok()
            else ScriptDependencies.pull store script [ pin ] |> Result.map ignore |> Result.mapError (String.concat "; ")
        let modules =
            pulled
            |> Result.bind (fun () -> ScriptDependencies.cached store script [ pin ] |> Result.mapError (String.concat "; "))
            |> Result.bind (fun locked ->
                Package.scriptModules Hosts.consolePackage store locked script SourceSpan.none None [ dependency ] []
                |> Result.mapError (List.map _.Message >> String.concat "; "))
            |> Result.bind (fun modules ->
                match modules.Dependencies |> List.tryFind (fun found -> found.Package = name) with
                | Some found -> Ok found
                | None -> Error $"'{name}' {version} has no modules")
        let documented =
            modules
            |> Result.map (publicModules (Some pin))
            |> Result.bind (fun documented ->
                match compileErrors (PackageStore.directoryOf store name version) documented with
                | Some errors -> Error errors
                | None -> Ok documented)
        pin, documented)

/// The reference of each page, by page, with the published package and version it is from, if it is from one: one for
/// each file of `compiled` that exports a name. A link in a doc comment, `` [`name`] ``, leads to the name's entry, on
/// its own page or another.
let private referencesOf (compiled: Documented list) : Map<string, string * (string * SemanticVersion) option> =
    // Every entry's page and heading, by where its name is declared, for the links.
    let targets =
        compiled
        |> List.collect (fun documented ->
            entriesIn documented.Files documented.Result.Typed.Value None
            |> Map.toList
            |> List.collect (fun (file, exports) ->
                anchors exports |> List.map (fun (export, id) -> export.Declaration, (pageOf file, id))))
        |> Map.ofList
    compiled
    |> List.collect (fun documented ->
        documented.Files
        |> List.choose (fun file ->
            let page = pageOf file
            let linkTo =
                Func<SourceSpan, string>(fun span ->
                    match targets |> Map.tryFind span with
                    | Some (target, id) when target = page -> $"#{id}"
                    | Some (target, id) -> $"{target}#{id}"
                    | None -> null)
            let result = documented.Result
            let reader = Documentation.Reader(documented.ModuleFile, result.DocComments.Value, result, 0, linkTo)
            let exports =
                entriesIn documented.Files result.Typed.Value (Some reader)
                |> Map.tryFind file
                |> Option.map anchors
                |> Option.defaultValue []
            let moduleDoc = if file = documented.ModuleFile then reader.ForModule file else None
            if exports.IsEmpty then None else Some (page, (render moduleDoc exports, documented.Pin))))
    |> Map.ofList

/// The reference of each page of the standard library and the packages the compiler ships, by page (see `referencesOf`).
let references () : Map<string, string> = referencesOf (documented ()) |> Map.map (fun _ (reference, _) -> reference)
/// `page`, a page's text, which pins `pin`, with its reference between the markers replaced by `reference`. An error
/// when it has no markers, or they are out of order.
let withReference (pin: (string * SemanticVersion) option) (reference: string) (page: string) : Result<string, string> =
    let page = page.ReplaceLineEndings "\n"
    let beginMarker = beginMarkerOf pin
    match page.IndexOf beginMarker, page.IndexOf endMarker with
    | start, finish when start >= 0 && finish > start ->
        Ok (page.Substring(0, start + beginMarker.Length) + "\n\n" + reference + "\n\n" + page.Substring finish)
    | _ -> Error $"has no reference: it needs a line {beginMarker} and, after it, a line {endMarker}"

/// The file beside the pages that lists the nominal types the standard library and the packages it documents export,
/// for the docs site to show their names apart in code blocks, as the editor does (see `nominalTypes`).
let nominalTypesFile = "nominal-types.json"

/// The names of the protected and private types and contexts `compiled` exports, as JSON: each list sorted, a name once.
/// A type's own name is listed, without its module: `Point`, not `geometry.Point`.
let private nominalTypes (compiled: Documented list) : string =
    let nominal =
        compiled
        |> List.collect (fun documented ->
            let typed = documented.Result.Typed.Value
            let files = Set.ofList documented.Files
            typed.Symbols
            |> Map.values
            |> Seq.filter (fun symbol ->
                symbol.IsExported && symbol.Kind = TypeDefinition && files.Contains symbol.Declaration.File && not (ReservedNames.isHidden symbol)
                && not (isDimension typed symbol))
            |> Seq.choose (fun symbol ->
                Definition.typeDefinitionOf typed symbol
                |> Option.filter _.IsNominal
                |> Option.map (fun info -> Names.written (Names.ownPart info.Name), info.IsPrivate))
            |> List.ofSeq)
    let listed (isPrivate: bool) =
        nominal
        |> List.filter (snd >> (=) isPrivate)
        |> List.map fst
        |> List.distinct
        |> List.sort
        |> List.map (fun name -> $"    \"{name}\"")
        |> String.concat ",\n"
    $"{{\n  \"protected\": [\n{listed false}\n  ],\n  \"private\": [\n{listed true}\n  ]\n}}\n"

/// What `update` finds for one page.
type PageUpdate =
    /// The page's reference, or the list of nominal types, is what the library, the packages it ships and the packages
    /// the pages pin give.
    | UpToDate
    /// The page's text, or the list's, as they give it.
    | OutOfDate of string
    /// The page cannot be written: there is no page for a file that exports names, or the page has no markers, or it
    /// has markers but nothing is its, or it pins a package that can't be read, or whose files are not its.
    | Unwritable of string

/// What each page of the library's reference in `directory` (docs/src/content/stdlib) needs, by its path: its text with
/// the library's reference, if that is not what it has; and the same for the list of nominal types beside them (see
/// `nominalTypesFile`). A page that pins a published package (see `pinOf`) has that
/// package's reference, read from the package cache of `store`, or downloaded from its index.
let update (store: PackageStore) (directory: string) : (string * PageUpdate) list =
    let pages =
        Directory.GetFiles(directory, "*.md")
        |> Array.sort
        |> Array.map (fun path -> Path.GetFileNameWithoutExtension path, (path, File.ReadAllText path))
        |> Map.ofArray
    let pins = pages |> Map.map (fun _ (_, text) -> pinOf text)
    let pinned = pins |> Map.values |> Seq.choose (function Ok pin -> pin | Error _ -> None) |> Seq.distinct |> List.ofSeq
    let fromPackages = published store directory pinned
    let compiled = documented () @ (fromPackages |> List.collect (fun (_, modules) -> Result.defaultValue [] modules))
    let references = referencesOf compiled
    let pinError (pin: (string * SemanticVersion) option) =
        match pin with
        | Some (name, version) ->
            fromPackages
            |> List.tryPick (fun (found, modules) -> match modules with Error message when found = (name, version) -> Some message | _ -> None)
            |> Option.map (fun message -> $"pins {name} {version}, which can't be read: {message}")
        | None -> None
    let written (pin: (string * SemanticVersion) option) =
        match pin with
        | Some (name, version) -> $"{name} {version}"
        | None -> "the standard library, or a package the compiler ships"
    let ofLibrary =
        references
        |> Map.toList
        |> List.map (fun (page, (reference, from)) ->
            match pages |> Map.tryFind page with
            | None -> Path.Combine(directory, page + ".md"), Unwritable $"is missing: a file of {written from} exports names it would list"
            | Some (path, text) ->
                match pins[page] with
                | Error message -> path, Unwritable message
                | Ok pin when pin <> from -> path, Unwritable $"pins {written pin}, but its reference is from {written from}"
                | Ok pin ->
                    match withReference pin reference text with
                    | Error message -> path, Unwritable message
                    | Ok written when written = text.ReplaceLineEndings "\n" -> path, UpToDate
                    | Ok written -> path, OutOfDate written)
    let stale =
        pages
        |> Map.toList
        |> List.filter (fun (page, _) -> not (references.ContainsKey page))
        |> List.choose (fun (page, (path, text)) ->
            match pins[page] with
            | Error message -> Some(path, Unwritable message)
            | Ok pin ->
                match pinError pin with
                | Some message -> Some(path, Unwritable message)
                | None when pin.IsSome -> Some(path, Unwritable $"pins {written pin}, but no file of it exports names for this page")
                | None when text.Contains(beginMarkerOf pin) -> Some(path, Unwritable $"has a reference, but no file of {written pin} is its")
                | None -> None)
    let nominal =
        let path = Path.Combine(directory, nominalTypesFile)
        let text = nominalTypes compiled
        path, (if File.Exists path && (File.ReadAllText path).ReplaceLineEndings "\n" = text then UpToDate else OutOfDate text)
    ofLibrary @ stale @ [ nominal ]
