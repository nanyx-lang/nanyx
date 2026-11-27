namespace NyxCompiler

open System.Text.RegularExpressions
open Parser.Program
open Nanyx.Formatter

/// Fixes that import the module a name comes from. A name that is not in scope, but that a module the file may import
/// exports, gets one fix per module that exports it: another module of the file's package, those nearest the file
/// first, then a public module of a package it depends on, then a standard-library module, as `string.trim` gets
/// `import nanyx/text`. A name qualified by a module's name, as `math.abs`, gets the aliased import that makes the
/// qualifier mean that module: `import nanyx/math as math`. The prelude is left out, since its names are always in
/// scope. An import is written as the file writes its imports (see `importEdit`), and one that would make a name the
/// file uses ambiguous is not offered: the error says so instead.
module ImportFixes =
    /// The standard-library modules that export each value, type and context, in alphabetical order, by the name they
    /// export it under. Built from the library's sources the first time it is needed.
    let standardLibraryExports : Lazy<Map<string, string list>> =
        lazy (
            StandardLibrary.moduleNames
            |> List.collect (fun moduleName ->
                StandardLibrary.moduleSources moduleName
                |> List.collect (fun (_, source) ->
                    match parseModuleFileWithNames "" source with
                    | Result.Ok (items, _, _) ->
                        Set.union (SyntaxTree.exportedValueNames items) (SyntaxTree.exportedTypeNames items)
                        |> Set.toList
                        |> List.map (fun name -> name, moduleName)
                    | Result.Error _ -> []))
            |> List.groupBy fst
            |> List.map (fun (name, pairs) -> name, pairs |> List.map snd |> List.distinct)
            |> Map.ofList)

    /// `exports`, the standard-library modules that export each name, with each module named by its import path, as
    /// `nanyx/text`.
    let exportPaths (exports: Map<string, string list>) =
        exports |> Map.map (fun _ modules -> modules |> List.map ((+) StandardLibrary.importPrefix))

    /// `standardLibraryExports` with each module named by its import path. A compile takes them from the checked library
    /// instead, if it can (see `CheckedLibrary.standardLibraryExportPaths`).
    let standardLibraryExportPaths : Lazy<Map<string, string list>> = lazy (exportPaths standardLibraryExports.Value)

    /// A name as it is written in source: identifiers joined by dots.
    let private writtenName = Regex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")

    /// Where a new import goes in `source`, and the text to insert there for the line `line`: on the line after the
    /// module's last top-level import; else after its `module` line, with a blank line between; else after the `#!`
    /// line and `::` directives at the top, which must come before any code, with a blank line between; else at the
    /// top, with a blank line after it.
    let private insertionFor (source: string) (syntax: Module) (line: string) : int * string =
        let lineAfter (offset: int) =
            match source.IndexOf('\n', offset) with
            | -1 -> None
            | newline -> Some (newline + 1)
        let lastImport = syntax |> List.tryFindBack (function Kind (Import _) -> true | _ -> false)
        let moduleLine = syntax |> List.tryFind (function Kind (ModuleDecl _) -> true | _ -> false)
        match lastImport, moduleLine with
        | Some item, _ ->
            match lineAfter item.Span.End with
            | Some offset -> offset, line + "\n"
            | None -> source.Length, "\n" + line
        | None, Some item ->
            match lineAfter item.Span.End with
            | Some offset -> offset, "\n" + line + "\n"
            | None -> source.Length, "\n\n" + line
        | None, None ->
            match Directives.headerEnd source with
            | 0 -> 0, line + "\n\n"
            | headerEnd -> headerEnd, "\n\n" + line

    /// The top-level imports the module `syntax` writes.
    let private importItems (syntax: Module) =
        syntax |> List.collect (function Kind (Import items) -> items | _ -> [])

    /// The imports the module `syntax` writes, each with its alias if it has one.
    let private existingImports (syntax: Module) =
        importItems syntax |> List.map (fun item -> item.Source, item.Alias) |> Set.ofList

    /// The spaces the line `offset` is on starts with.
    let private indentAt (source: string) (offset: int) =
        let lineStart = source.LastIndexOf('\n', max 0 (offset - 1)) + 1
        String.replicate (source.Substring(lineStart, offset - lineStart) |> Seq.takeWhile ((=) ' ') |> Seq.length) " "

    /// The edit that adds the import `import` to the file `syntax`, whose text is `source`, as it writes its imports: to
    /// its last `import ( ... )` block, in the order the formatter keeps imports in (`ImportOrder`); or as a line where
    /// `insertionFor` puts one.
    let private moduleEdit (source: string) (syntax: Module) (import: StandardLibraryImports.Import) : int * string =
        let block =
            syntax
            |> List.tryFindBack (fun item ->
                match item.Kind with
                | Import (_ :: _) -> Regex.IsMatch(SourceSpan.textIn source item.Span, @"^import\s*\(")
                | _ -> false)
        match block with
        | Some { Kind = Import items; Span = span } ->
            let written = (StandardLibraryImports.line import).Substring "import ".Length
            let added = ImportItem.synthetic (ModuleImport import.Path) import.Alias
            let multiLine = (SourceSpan.textIn source span).Contains '\n'
            match items |> List.tryFind (fun item -> ImportOrder.compare added item < 0) with
            | Some next when multiLine ->
                let lineStart = source.LastIndexOf('\n', max 0 (next.Span.Start - 1)) + 1
                lineStart, indentAt source next.Span.Start + written + "\n"
            | Some next -> next.Span.Start, written + " "
            | None ->
                let last = List.last items
                if multiLine then last.Span.End, "\n" + indentAt source last.Span.Start + written else last.Span.End, " " + written
        | _ -> insertionFor source syntax (StandardLibraryImports.line import)

    /// The edit that imports `name` by `import`, written as the file `syntax`, whose text is `source`, writes its
    /// imports: `name` added to an import of the module that selects names, `import billing as (Invoice)`; or the
    /// module's import added (see `moduleEdit`).
    let private importEdit (source: string) (syntax: Module) (import: StandardLibraryImports.Import) (name: string) : int * string =
        let selecting =
            importItems syntax
            |> List.tryPick (fun item ->
                match item.Source, item.Selection with
                | ModuleImport path, Some ((_ :: _) as selection) when path = import.Path && import.Alias.IsNone -> Some selection
                | _ -> None)
        match selecting with
        | Some selection ->
            let first, last = snd (List.head selection), snd (List.last selection)
            // A type's attached definition, `Money.of`, comes in with its type.
            let selected = name.Substring(0, match name.IndexOf '.' with -1 -> name.Length | dot -> dot)
            if (SourceSpan.textIn source (SourceSpan.between first last)).Contains '\n' then last.End, "\n" + indentAt source last.Start + selected
            else last.End, ", " + selected
        | None -> moduleEdit source syntax import

    /// The names written in `source` that importing the module at `path` would make ambiguous: names it exports, by
    /// `exportsOf`, that the file uses and that a module it imports without an alias brings in already.
    let private clashes (exportsOf: string -> Set<string>) (source: string) (syntax: Module) (path: string) (name: string) : (string * string list) list =
        let brought =
            importItems syntax
            |> List.choose (fun item ->
                match item.Source, item.Alias, item.Selection with
                | ModuleImport imported, _, Some selection when imported <> path -> Some (imported, selection |> List.map fst |> Set.ofList)
                | ModuleImport imported, None, None when imported <> path -> Some (imported, exportsOf imported)
                | _ -> None)
        let isUsed (exported: string) = Regex.IsMatch(source, $@"(?<![\w.]){Regex.Escape exported}(?!\w)")
        Set.remove name (exportsOf path)
        |> Set.toList
        |> List.choose (fun exported ->
            match brought |> List.filter (fun (_, names) -> names.Contains exported) |> List.map fst with
            | [] -> None
            | modules when isUsed exported -> Some (exported, modules)
            | _ -> None)

    /// The fixes for the unknown name written at `span`, each import that would bring it into scope (see `addFrom`), and
    /// what keeps each that isn't offered from being one. `packageMap` is each name `packageExports` export, with the
    /// paths of the modules that export it.
    let private fixesFor
        (packageExports: (string * Set<string>) list)
        (packageMap: Map<string, string list>)
        (standardExports: Map<string, string list>)
        (source: string)
        (syntax: Module)
        (span: SourceSpan)
        : Fix list * string list =
        let written = SourceSpan.textIn source span
        if not (writtenName.IsMatch written) then
            [], []
        else
            // An import that selects names brings in only those, so another can be added to it.
            let existing = importItems syntax |> List.filter (fun item -> item.Selection.IsNone) |> List.map (fun item -> item.Source, item.Alias) |> Set.ofList
            let exportsOf (path: string) =
                match packageExports |> List.tryFind (fst >> (=) path) with
                | Some (_, names) -> names
                | None -> standardExports |> Map.filter (fun _ paths -> List.contains path paths) |> Map.keys |> Set.ofSeq
            // A type's attached definition, `Money.of`, is found by its type's name.
            let qualifier = match written.IndexOf '.' with -1 -> None | dot -> Some (written.Substring(0, dot))
            let fromPackage =
                StandardLibraryImports.candidates packageMap written
                |> List.map (fun import -> import, written)
                |> fun found ->
                    match found, qualifier with
                    | [], Some typeName -> StandardLibraryImports.candidates packageMap typeName |> List.map (fun import -> import, typeName)
                    | _ -> found
            let fromLibrary = StandardLibraryImports.candidates standardExports written |> List.map (fun import -> import, written)
            let offered =
                fromPackage @ fromLibrary
                |> List.filter (fun (import, _) -> not (existing.Contains (ModuleImport import.Path, import.Alias)))
                |> List.distinctBy fst
            let isPackage (import: StandardLibraryImports.Import) = packageMap |> Map.exists (fun _ paths -> List.contains import.Path paths)
            let results =
                offered
                |> List.map (fun (import, name) ->
                    let found = if import.Alias.IsSome then [] else clashes exportsOf source syntax import.Path name
                    match found with
                    | [] ->
                        let offset, text = importEdit source syntax import name
                        let title = if isPackage import && import.Alias.IsNone then $"Import `{name}` from `{import.Path}`" else $"Add `{StandardLibraryImports.line import}`"
                        Ok { Title = title; Edits = [ { Span = TextEdit.spanBetween source span offset offset; NewText = text } ] }
                    | clashing ->
                        let named =
                            clashing
                            |> List.map (fun (exported, modules) -> $"""'{exported}' ambiguous between {modules @ [ import.Path ] |> List.map (sprintf "'%s'") |> Diagnostics.listed}""")
                        Error $"'{import.Path}' exports '{name}', but importing it would make {Diagnostics.listed named}")
            (results |> List.choose (function Ok fix -> Some fix | Error _ -> None)), (results |> List.choose (function Error reason -> Some reason | Ok _ -> None))

    /// `diagnostics` with a fix for each import that would bring an unknown name into scope, given the modules of the
    /// file's package and of those it depends on that it may import, each by its import path with the names it exports
    /// (`packageExports`, see `PackageExports.importable`), and the standard-library modules that export each name
    /// (`standardExports`). The package's modules are read only for an unknown name: a file a compile reads is one its
    /// module depends on, and the language server compiles it again whenever that file changes (#2222). An import that would make another name ambiguous is not offered: the diagnostic's message
    /// says why. The names are those of the module `syntax`, whose text is `source` (with its line endings made `\n`, as
    /// for the offsets of spans); a diagnostic about another module gets none.
    let addFrom
        (packageExports: Lazy<(string * Set<string>) list>)
        (standardExports: Map<string, string list>)
        (source: string)
        (syntax: Module)
        (diagnostics: Diagnostic list)
        : Diagnostic list =
        // Each name the package's modules export, with the paths of those that export it, made once for every diagnostic.
        let packageMap =
            lazy
                (packageExports.Value
                 |> List.collect (fun (path, names) -> names |> Set.toList |> List.map (fun exported -> exported, path))
                 |> List.groupBy fst
                 |> List.map (fun (exported, paths) -> exported, paths |> List.map snd)
                 |> Map.ofList)
        let file = syntax |> List.tryPick (fun item -> if SourceSpan.isNone item.Span then None else Some item.Span.File)
        let isUnknownName (diagnostic: Diagnostic) =
            [ DiagnosticCodes.UnknownIdentifier; DiagnosticCodes.UnknownFunction; DiagnosticCodes.UnknownType; DiagnosticCodes.UnknownContext ]
            |> List.exists (fun code -> code.Code = diagnostic.Code)
        diagnostics
        |> List.map (fun diagnostic ->
            match diagnostic.Span with
            | Some span when isUnknownName diagnostic && Some span.File = file && span.End <= source.Length ->
                let fixes, refused = fixesFor packageExports.Value packageMap.Value standardExports source syntax span
                let message = refused |> List.fold (fun (message: string) reason -> $"{message.TrimEnd '.'}. {reason}") diagnostic.Message
                { diagnostic with Message = message; Fixes = diagnostic.Fixes @ fixes }
            | _ -> diagnostic)


    /// Where an import goes in a module's text, and what to insert there, as the quick fix places it.
    type ImportInsertion =
        | Insert of offset: int * text: string
        /// The module imports it already, without an alias.
        | AlreadyImported
        /// The text does not parse, so there is nowhere to put it yet.
        | NotParsed

    /// The insertion that imports the module at `path` (`nanyx/text`) into the module whose text is `source` (with its
    /// line endings made `\n`), placed as the missing-import quick fix places one: for a library browser's button.
    let insertion (source: string) (path: string) : ImportInsertion =
        match parseModuleFileWithNames "" source with
        | Result.Error _ -> NotParsed
        | Result.Ok (syntax, _, _) when (existingImports syntax).Contains (ModuleImport path, None) -> AlreadyImported
        | Result.Ok (syntax, _, _) -> Insert (moduleEdit source syntax { Path = path; Alias = None })
