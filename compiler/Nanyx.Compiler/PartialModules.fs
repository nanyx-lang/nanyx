namespace NyxCompiler

open System.IO
open Parser.Program

/// A file as parsed: its items, the names its nodes declare, and its `module name =` blocks.
type ParsedFile = Module * DeclaredNames * (ModuleName * SourceSpan) list

/// The other files of a module split across files, each as its path and its text, with how messages name a file.
type ModuleFragments =
    { Others: (string * string) list
      Display: string -> string }

/// Modules split across files: each file declares `@partial module name`, and together they are one module, as if
/// their items were written in one file. The imports of every fragment apply to the whole module.
module PartialModules =
    /// The other files that declare the module `name` that the file from `origin` declares: in a package, the
    /// package's other files that declare it (see `PackageModules`); in the standard library, its other files.
    let otherFragments (package: PackageModules option) (origin: ModuleOrigin) (name: string) : ModuleFragments =
        match origin, package with
        | SourceFile path, Some modules when PackageModules.contains modules path ->
            let others =
                modules.Modules
                |> Map.tryFind name
                |> Option.defaultValue []
                |> List.filter (PackageModules.samePath path >> not)
            { Others = others |> List.map (fun file -> file, File.ReadAllText file); Display = PackageModules.display modules }
        | StandardLibraryModule moduleName, _ when moduleName = name ->
            { Others = StandardLibrary.otherFragments moduleName; Display = id }
        | _ -> { Others = []; Display = id }

    /// `syntax` without its module declaration's `@partial`, and whether it had one.
    let private unmarked (syntax: Module) : Module * bool =
        let rec loop (items: Module) =
            match items with
            | attribute :: next :: rest when TopLevelItem.isPartialDeclaration attribute next -> next :: rest, true
            | item :: rest -> let rest, marked = loop rest in item :: rest, marked
            | [] -> [], false
        loop syntax

    /// The module the file `parsed` declares, with the items of its other fragments (see `otherFragments`) added, each
    /// parsed by `parse`. Each fragment must be marked `@partial`; a fragment's own module declaration is left out.
    let assemble
        (parse: string -> string -> Result<ParsedFile, Diagnostic list>)
        (fragmentsOf: string -> ModuleFragments)
        ((syntax, declared, moduleBlocks): ParsedFile)
        : Result<ParsedFile, Diagnostic list> =
        let rootSyntax, rootMarked = unmarked syntax
        match FileImports.declaration moduleBlocks syntax with
        | None -> Ok (rootSyntax, declared, moduleBlocks)
        | Some (name, span) ->
            let fragments = fragmentsOf name
            if fragments.Others.IsEmpty then
                Ok (rootSyntax, declared, moduleBlocks)
            else
                let parsed = fragments.Others |> List.map (fun (path, text) -> path, parse path text)
                match parsed |> List.collect (function _, Error errors -> errors | _, Ok _ -> []) with
                | _ :: _ as errors -> Error errors
                | [] ->
                    let others =
                        parsed
                        |> List.choose (function
                            | path, Ok (items, names, blocks) -> Some (path, unmarked items, names, blocks)
                            | _, Error _ -> None)
                    let unmarkedFiles = others |> List.filter (fun (_, (_, marked), _, _) -> not marked) |> List.map (fun (path, _, _, _) -> path)
                    if rootMarked && unmarkedFiles.IsEmpty then
                        let withoutDeclaration (items: Module) blocks =
                            match FileImports.declaration blocks items with
                            | Some (_, declaration) -> items |> List.filter (fun item -> item.Span <> declaration)
                            | None -> items
                        // In order of path, whichever fragment is being compiled, so each reports the same diagnostics.
                        let items =
                            (span.File, rootSyntax) :: (others |> List.map (fun (path, (items, _), _, blocks) -> path, withoutDeclaration items blocks))
                            |> List.sortWith (fun (a, _) (b, _) -> System.String.CompareOrdinal(a, b))
                            |> List.collect snd
                        Ok
                            (items,
                             others |> List.fold (fun acc (_, _, names, _) -> Map.fold (fun acc key value -> Map.add key value acc) acc names) declared,
                             moduleBlocks @ (others |> List.collect (fun (_, _, _, blocks) -> blocks)))
                    else
                        let shown = others |> List.map (fun (path, _, _, _) -> $"'{fragments.Display path}'") |> String.concat ", "
                        let unmarkedShown =
                            match (if rootMarked then [] else [ span.File ]) @ unmarkedFiles |> List.map (fun path -> $"'{fragments.Display path}'") with
                            | [ one ] -> $"{one} does"
                            | several -> $"""{String.concat ", " several} do"""
                        Error
                            [ Diagnostics.at
                                  DiagnosticCodes.DuplicateModule
                                  span
                                  $"'{fragments.Display span.File}' declares the module '{name}', which {shown} declares too: to split a module across files, start each with @partial module {name}, which {unmarkedShown} not" ]
