namespace NyxCompiler

open System.Text.RegularExpressions
open Parser.Program

/// Fixes that import the standard-library module a name comes from. A name that is not in scope, but that a
/// standard-library module exports, gets one fix per module that exports it: `string.trim` gets
/// `import nanyx/text`. A name qualified by a module's name, as `math.abs`, gets the aliased import that makes the
/// qualifier mean that module: `import nanyx/math as math`. The prelude is left out, since its names are always in
/// scope.
module ImportFixes =
    /// The standard-library modules that export each value, in alphabetical order, by the name they export it under.
    /// Built from the library's sources the first time it is needed.
    let standardLibraryExports : Lazy<Map<string, string list>> =
        lazy (
            StandardLibrary.moduleNames
            |> List.collect (fun moduleName ->
                StandardLibrary.moduleSources moduleName
                |> List.collect (fun (_, source) ->
                    match parseModuleFileWithNames "" source with
                    | Result.Ok (items, _, _) ->
                        items
                        |> List.choose (function
                            | Kind (Def (ValueDef(true, name, _, _))) -> Some (name, moduleName)
                            | _ -> None)
                    | Result.Error _ -> []))
            |> List.groupBy fst
            |> List.map (fun (name, pairs) -> name, pairs |> List.map snd |> List.distinct)
            |> Map.ofList)

    /// `standardLibraryExports` with each module named by its import path, as `nanyx/text`.
    let standardLibraryExportPaths : Lazy<Map<string, string list>> =
        lazy (standardLibraryExports.Value |> Map.map (fun _ modules -> modules |> List.map ((+) StandardLibrary.importPrefix)))

    /// A name as it is written in source: identifiers joined by dots.
    let private writtenName = Regex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")

    /// The imports that would bring `name` into scope, given the modules that export each name (`exports`):
    /// `nanyx/<module> as <module>` when `name` is qualified by the name of a module that exports the rest of it,
    /// then `nanyx/<module>` for each module that exports `name` itself.
    let candidates (exports: Map<string, string list>) (name: string) : ImportItem list =
        let exportersOf name = exports |> Map.tryFind name |> Option.defaultValue []
        let import moduleName alias =
            ImportItem.synthetic (ModuleImport (StandardLibrary.importPrefix + moduleName)) alias
        let qualified =
            match name.IndexOf '.' with
            | dot when dot > 0 ->
                let qualifier = name.Substring(0, dot)
                if exportersOf (name.Substring(dot + 1)) |> List.contains qualifier then [ import qualifier (Some qualifier) ]
                else []
            | _ -> []
        qualified @ (exportersOf name |> List.map (fun moduleName -> import moduleName None))

    let private importLine (item: ImportItem) =
        let source =
            match item.Source with
            | ModuleImport name -> name
            | PathImport path -> $"\"{path}\""
        match item.Alias with
        | Some alias -> $"import {source} as {alias}"
        | None -> $"import {source}"

    /// Where a new import goes in `source`, and the text to insert there for the line `line`: on the line after the
    /// module's last top-level import; else after its `module` line, with a blank line between; else after the `#!`
    /// line and `::` directives at the top, which must come before any code, with a blank line between; else at the
    /// top, with a blank line after it.
    let private insertion (source: string) (syntax: Module) (line: string) : int * string =
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

    let private fixesFor (exports: Map<string, string list>) (source: string) (syntax: Module) (span: SourceSpan) : Fix list =
        let written = source.Substring(span.Start, span.End - span.Start)
        if not (writtenName.IsMatch written) then
            []
        else
            let existing =
                syntax
                |> List.collect (function Kind (Import items) -> items |> List.map (fun item -> item.Source, item.Alias) | _ -> [])
                |> Set.ofList
            candidates exports written
            |> List.filter (fun item -> not (existing.Contains (item.Source, item.Alias)))
            |> List.map (fun item ->
                let line = importLine item
                let offset, text = insertion source syntax line
                { Title = $"Add `{line}`"; Edits = [ { Span = TextEdit.spanBetween source span offset offset; NewText = text } ] })

    /// `diagnostics` with a fix for each import that would bring an unknown name into scope, given the modules that
    /// export each name (`exports`). The names are those of the module `syntax`, whose text is `source` (with its line
    /// endings made `\n`, as for the offsets of spans); a diagnostic about another module gets none.
    let addWith (exports: Map<string, string list>) (source: string) (syntax: Module) (diagnostics: Diagnostic list) : Diagnostic list =
        let file = syntax |> List.tryPick (fun item -> if SourceSpan.isNone item.Span then None else Some item.Span.File)
        let isUnknownName (diagnostic: Diagnostic) =
            diagnostic.Code = DiagnosticCodes.UnknownIdentifier.Code || diagnostic.Code = DiagnosticCodes.UnknownFunction.Code
        diagnostics
        |> List.map (fun diagnostic ->
            match diagnostic.Span with
            | Some span when isUnknownName diagnostic && Some span.File = file && span.End <= source.Length ->
                { diagnostic with Fixes = diagnostic.Fixes @ fixesFor exports source syntax span }
            | _ -> diagnostic)

    /// `addWith` for the standard library's exports.
    let add (source: string) (syntax: Module) (diagnostics: Diagnostic list) : Diagnostic list =
        addWith standardLibraryExports.Value source syntax diagnostics
