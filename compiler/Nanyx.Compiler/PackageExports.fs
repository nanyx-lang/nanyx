namespace NyxCompiler

open System.Collections.Concurrent
open System.IO
open Parser.Program

/// The modules a source file of a package may import, with the names each exports, for the fix that imports an unknown
/// name (see `ImportFixes`).
module PackageExports =
    /// The names each file read so far exports, by full path, with the text they were read from, so the language
    /// server, which compiles on every change, parses only files that changed.
    let private exportsByFile = ConcurrentDictionary<string, string * Set<string>>()

    /// The values, types and contexts the file at `filePath`, whose text is `source`, exports; none if it doesn't parse.
    let private exportedBy (filePath: string) (source: string) =
        match exportsByFile.TryGetValue filePath with
        | true, (read, names) when read = source -> names
        | _ ->
            let names =
                match parseModuleFileWithNames filePath source with
                | Result.Ok (items, _, _) -> Set.union (SyntaxTree.exportedValueNames items) (SyntaxTree.exportedTypeNames items)
                | Result.Error _ -> Set.empty
            exportsByFile.[filePath] <- (source, names)
            names

    /// The modules the file at `filePath`, of the package whose build is `modules`, may import, each by the path an
    /// import names it by, with the names it exports, its files' texts read by `readFile`: the package's other modules,
    /// those whose files are nearest the file first, then the public modules of the packages it depends on, by
    /// `<package>/<module>`, in the order the manifest lists them.
    let importable (modules: PackageModules) (readFile: string -> string) (filePath: string) : (string * Set<string>) list =
        let exportsOf (files: string list) = files |> List.map (fun file -> exportedBy file (readFile file)) |> Set.unionMany
        let directory = Path.GetDirectoryName filePath
        let distance (files: string list) = files |> List.map (fun file -> (PackageModules.relativePath directory file).Split('/').Length) |> List.min
        let own =
            modules.Modules
            |> Map.toList
            |> List.filter (fun (_, files) -> not (files |> List.exists (PackageModules.samePath filePath)))
            |> List.sortBy (fun (moduleName, files) -> distance files, moduleName)
            |> List.map (fun (moduleName, files) -> moduleName, exportsOf files)
        let dependencies =
            modules.Dependencies
            |> List.collect (fun dependency ->
                dependency.PublicModules
                |> List.choose (fun moduleName ->
                    dependency.Modules |> Map.tryFind moduleName |> Option.map (fun files -> $"{dependency.Package}/{moduleName}", exportsOf files)))
        own @ dependencies
