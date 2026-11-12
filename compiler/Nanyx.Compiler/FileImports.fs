namespace NyxCompiler

open System.IO
open System.Text.RegularExpressions
open Parser.Program

/// The rules for importing a file of the user's: a file-path import has an alias, it imports a script module (one
/// that declares no module name), and no import brings in an entrypoint (a file with top-level statements).
module FileImports =
    let private identifier = Regex(@"^[A-Za-z_][A-Za-z0-9_]*$")

    /// The name a file declares with `module name`, if it does. The parser writes each `module name =` block as a
    /// module declaration too (see `expandMidFileModules`), at the block's span, so those are left out: a script that
    /// holds a block is still a script.
    let declaredName (moduleBlocks: (ModuleName * SourceSpan) list) (module': Module) : string option =
        let isBlock (span: SourceSpan) = moduleBlocks |> List.exists (fun (_, block) -> block.Start = span.Start)
        module'
        |> List.tryPick (fun item ->
            match item.Kind with
            | ModuleDecl name when not (isBlock item.Span) -> Some name
            | _ -> None)

    /// The full path of the file `item` imports, from the directory `baseDir` of the importing file: a file path as
    /// written, or a module import `a.b/c` as the file `a/b/c.nyx`.
    let resolvePath (baseDir: string) (item: ImportItem) : Result<string, Diagnostic> =
        let rawPath =
            match item.Source with
            | PathImport path ->
                if Path.IsPathRooted path then path else Path.Combine(baseDir, path)
            | ModuleImport name ->
                let relative =
                    name.Replace(".", string Path.DirectorySeparatorChar)
                        .Replace("/", string Path.DirectorySeparatorChar)
                Path.Combine(baseDir, relative + ".nyx")
        let fullPath =
            if Path.GetExtension rawPath = "" then rawPath + ".nyx" else rawPath
        if File.Exists fullPath then
            Ok (Path.GetFullPath fullPath)
        else
            Error (Diagnostics.at DiagnosticCodes.ImportNotFound item.Span $"Import not found: {rawPath}")

    /// The error for a file-path import with no alias, which would bring the file's exports in unqualified.
    let missingAlias (item: ImportItem) : Diagnostic option =
        match item.Source, item.Alias with
        | PathImport path, None ->
            let stem = Path.GetFileNameWithoutExtension path
            let example = if identifier.IsMatch stem then $": write import \"{path}\" as {stem}" else ""
            Some (Diagnostics.at DiagnosticCodes.PathImportWithoutAlias item.Span $"An import of a file path must have an alias{example}")
        | _ -> None

    /// The errors for importing, with `item`, a file that declares the module name `name` (if any) and is `syntax` as
    /// parsed: a named module imported by its path, and an entrypoint.
    let importedFile (item: ImportItem) (name: string option) (syntax: Module) : Diagnostic list =
        let shown =
            match item.Source with
            | PathImport path -> path
            | ModuleImport moduleName -> moduleName
        let named =
            match item.Source, name with
            | PathImport path, Some name ->
                [ Diagnostics.at DiagnosticCodes.NamedModuleImportedByPath item.Span $"'{path}' declares the module name '{name}', so it must be imported by its name, not by file path: write import {name}" ]
            | _ -> []
        let entrypoint =
            if syntax |> List.exists (function Kind (Expr _) -> true | _ -> false) then
                [ Diagnostics.at DiagnosticCodes.EntrypointImported item.Span $"'{shown}' has top-level statements, so it is an entrypoint and cannot be imported: move the definitions to share into a file without top-level statements" ]
            else
                []
        named @ entrypoint
