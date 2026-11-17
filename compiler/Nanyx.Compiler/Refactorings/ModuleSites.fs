/// Where a module's name is written in a file of its package: the `module` line that declares it, and each import of it.
/// Positions are character offsets into the source as the parser saw it, with line endings normalised to `\n`.
module NyxCompiler.Refactorings.ModuleSites

open System.IO
open System.Text.RegularExpressions
open Parser.Program
open NyxCompiler

/// `span` from `offset` characters into it, for `length` characters, on its first line.
let private within (span: SourceSpan) (offset: int) (length: int) =
    { span with Start = span.Start + offset; End = span.Start + offset + length; StartColumn = span.StartColumn + offset }

/// The name the compiled root module `result`, whose text is `source`, declares in its `module` line, and where the
/// name is written there. None for a file that declares no module, and for a `module name =` block.
let declaration (source: string) (result: CompileResult) : (string * SourceSpan) option =
    match result.Syntax, result.ModuleName with
    | Some syntax, Some name ->
        syntax
        |> List.tryPick (fun item ->
            match item.Kind with
            | ModuleDecl(_, declared) when declared = name && not (SourceSpan.isNone item.Span) ->
                let keyword = Regex.Match(SourceSpan.textIn source item.Span, $@"\bmodule\s+{Regex.Escape name}\b")
                if keyword.Success then Some (name, within item.Span (keyword.Index + keyword.Length - name.Length) name.Length) else None
            | _ -> None)
    | _ -> None

/// Every import in `syntax`: at the top level, in module blocks and in blocks, and every re-export.
let private importsIn (syntax: Module) : ImportItem list =
    let rec ofItem (item: TopLevelItem) =
        match item.Kind with
        | Import items -> items
        | ReExport item -> [ item ]
        | ModuleBlock(_, items) -> items |> List.collect ofItem
        | _ -> []
    let inBlocks =
        Hover.allExpressions syntax
        |> List.collect (fun expr ->
            match expr.Kind with
            | Block statements -> statements |> List.collect (fun statement -> match statement.Kind with ImportStatement items -> items | _ -> [])
            | _ -> [])
    (syntax |> List.collect ofItem) @ inBlocks

/// The module of the package `package` that an import in the compiled root module `result`, whose text is `source`,
/// names at `offset`, and where its name is written there: `shapes` in `import shapes` or `import app/shapes`, not an
/// import of another package's module.
let importAt (package: string) (source: string) (result: CompileResult) (offset: int) : (string * SourceSpan) option =
    match result.Syntax with
    | Some syntax ->
        importsIn syntax
        |> List.tryPick (fun item ->
            match item.Source with
            | ModuleImport name when not (SourceSpan.isNone item.Span) ->
                let moduleName =
                    match name.IndexOf '/' with
                    | -1 -> Some name
                    | slash when name.Substring(0, slash) = package -> Some (name.Substring(slash + 1))
                    | _ -> None
                let at = (SourceSpan.textIn source item.Span).IndexOf name
                moduleName
                |> Option.filter (fun _ -> at >= 0)
                |> Option.map (fun moduleName -> moduleName, within item.Span (at + name.Length - moduleName.Length) moduleName.Length)
                |> Option.filter (fun (_, span) -> span.Start <= offset && offset <= span.End)
            | _ -> None)
    | None -> None

/// Where each import in the compiled root module `result`, the file at `path` whose text is `source`, names the module
/// `moduleName` of its package `package`: the module's part of `import shapes` or `import app/shapes`, each with what
/// it is to be written as, given the module's new name `newName`. An import of a file by its path, `import
/// "./shapes.nyx"`, of one of `renamedFiles` (each an old full path and a new one) has the file's name written anew.
let imports
    (package: string)
    (moduleName: string)
    (newName: string)
    (renamedFiles: (string * string) list)
    (path: string)
    (source: string)
    (result: CompileResult)
    : TextEdit list =
    match result.Syntax with
    | Some syntax ->
        importsIn syntax
        |> List.filter (fun item -> not (SourceSpan.isNone item.Span))
        |> List.choose (fun item ->
            let text = SourceSpan.textIn source item.Span
            match item.Source with
            | ModuleImport name when name = moduleName || name = $"{package}/{moduleName}" ->
                let at = text.IndexOf name
                if at < 0 then None
                else Some { Span = within item.Span (at + name.Length - moduleName.Length) moduleName.Length; NewText = newName }
            | PathImport written ->
                let target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName path, written))
                match renamedFiles |> List.tryFind (fun (old, _) -> PackageModules.samePath old target) with
                | Some (old, renamed) ->
                    let oldName = Path.GetFileName old
                    let at = text.IndexOf written
                    if at < 0 || not (written.EndsWith oldName) then None
                    else Some { Span = within item.Span (at + written.Length - oldName.Length) oldName.Length; NewText = Path.GetFileName renamed }
                | None -> None
            | ModuleImport _ -> None)
    | None -> []
