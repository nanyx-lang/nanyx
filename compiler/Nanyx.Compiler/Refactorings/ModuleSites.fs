/// Where a module's name is written in a file of its package: the `module` line that declares it, and each import of it;
/// and where a `module name =` block's name is written: its `module` line, each import of it, and each name written
/// with it.
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

/// The module of the package `package` that an import in the compiled root module `result`, whose text is `source`,
/// names at `offset`, and where its name is written there: `shapes` in `import shapes` or `import app/shapes`, not an
/// import of another package's module.
let importAt (package: string) (source: string) (result: CompileResult) (offset: int) : (string * SourceSpan) option =
    match result.Syntax with
    | Some syntax ->
        SyntaxTree.allImports syntax
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

/// A `module name =` block of a file: its name, its full name (after the file's module's and those of the blocks it is
/// in), the span of the block, where its name is written in its `module` line, the span of the block it is in, if any,
/// and whether it exports anything.
type Block =
    { Name: string
      FullName: string
      Span: SourceSpan
      NameSpan: SourceSpan
      Parent: SourceSpan option
      Exports: bool }

/// The innermost of `blocks` that holds `span`, if any.
let private blockAround (blocks: Block list) (span: SourceSpan) =
    blocks |> List.filter (fun block -> SourceSpan.contains block.Span span && block.Span <> span) |> List.sortBy (fun block -> block.Span.End - block.Span.Start) |> List.tryHead

/// The `module name =` blocks of the compiled root module `result`, whose text is `source`, nested ones among them. The
/// parser lifts a block's items to the top level, under qualified names, and leaves a module declaration of its full
/// name where the block is written.
let blocks (source: string) (result: CompileResult) : Block list =
    match result.Syntax with
    | Some syntax ->
        let declared =
            syntax
            |> List.choose (fun item ->
                match item.Kind with
                | ModuleDecl(_, fullName) when Some fullName <> result.ModuleName && not (SourceSpan.isNone item.Span) ->
                    let name = fullName.Substring(fullName.LastIndexOf '.' + 1)
                    let written = Regex.Match(SourceSpan.textIn source item.Span, $@"^module\s+{Regex.Escape name}\s*=")
                    if written.Success then
                        let at = written.Value.IndexOf(name, "module".Length)
                        Some { Name = name; FullName = fullName; Span = item.Span; NameSpan = within item.Span at name.Length; Parent = None; Exports = false }
                    else None
                | _ -> None)
        let exported (block: Block) =
            syntax
            |> List.exists (fun item ->
                SourceSpan.contains block.Span item.Span
                && match item.Kind with
                   | Def (ValueDef(true, _, _, _)) -> true
                   | Def (TypeDef(_, modifiers, _, _)) -> modifiers |> List.contains Export
                   | _ -> false)
        declared |> List.map (fun block -> { block with Parent = blockAround declared block.Span |> Option.map _.Span; Exports = exported block })
    | None -> []

/// Each import of a block, among `blocks`, of the compiled root module `result`, whose text is `source`, with the block
/// it names and where its name is written: an import at the top level of the file, or of a block, of a block's short
/// name names a block nested directly in where it is written, or else one beside that, as the compiler finds it.
let private blockImports (blocks: Block list) (source: string) (result: CompileResult) : (Block * SourceSpan) list =
    match result.Syntax with
    | Some syntax ->
        syntax
        |> List.collect (function
            | Kind (Import imported) -> imported
            | _ -> [])
        |> List.filter (fun item -> not (SourceSpan.isNone item.Span))
        |> List.choose (fun item ->
            match item.Source with
            | ModuleImport name when not (name.Contains '/') && not (name.Contains '.') ->
                let site = blockAround blocks item.Span |> Option.map _.Span
                let named = blocks |> List.filter (fun block -> block.Name = name && Some block.Span <> site)
                let children = named |> List.filter (fun block -> block.Parent = site)
                let siblings =
                    match site with
                    | Some site -> named |> List.filter (fun block -> block.Parent = (blocks |> List.tryFind (fun other -> other.Span = site) |> Option.bind _.Parent))
                    | None -> []
                let at = (SourceSpan.textIn source item.Span).IndexOf name
                List.tryHead (children @ siblings)
                |> Option.filter (fun _ -> at >= 0)
                |> Option.map (fun block -> block, within item.Span at name.Length)
            | _ -> None)
    | None -> []

/// The block of the compiled root module `result`, whose text is `source`, whose name is written at `offset`: in its
/// `module` line, or in an import of it.
let blockAt (source: string) (result: CompileResult) (offset: int) : Block option =
    let blocks = blocks source result
    let touches (span: SourceSpan) = span.Start <= offset && offset <= span.End
    blocks
    |> List.tryFind (fun block -> touches block.NameSpan)
    |> Option.orElseWith (fun () -> blockImports blocks source result |> List.tryFind (snd >> touches) |> Option.map fst)

/// Where the compiled root module `result`, the file at `path` whose text is `source`, writes the name of `block`, a
/// block of the file `blockFile`, each with what it is to be written as, given the block's new name `newName`: in its
/// `module` line and each import of it, if `path` is `blockFile`, and before each name it declares that is written
/// with the block's name (`money.Money`).
let blockEdits (block: Block) (blockFile: string) (newName: string) (path: string) (source: string) (result: CompileResult) : TextEdit list =
    let own =
        if path <> blockFile then []
        else
            let blocks = blocks source result
            { Span = block.NameSpan; NewText = newName }
            :: (blockImports blocks source result |> List.filter (fun (imported, _) -> imported.Span = block.Span) |> List.map (fun (_, span) -> { Span = span; NewText = newName }))
    let qualified =
        match result.Typed with
        | Some typed ->
            Definition.referenceSites result
            |> List.choose (fun (span, id) ->
                typed.Symbols
                |> Map.tryFind id
                |> Option.filter (fun symbol ->
                    symbol.Declaration.File = blockFile
                    && SourceSpan.contains block.Span symbol.Declaration
                    && symbol.Name.StartsWith(block.FullName + "."))
                |> Option.bind (fun symbol ->
                    let written = $"{block.Name}.{symbol.Name.Substring(block.FullName.Length + 1)}"
                    let text = SourceSpan.textIn source span
                    let at = text.Length - written.Length
                    if text.EndsWith written && (at = 0 || text.[at - 1] = '.') then Some { Span = within span at block.Name.Length; NewText = newName }
                    else None))
        | None -> []
    own @ qualified

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
        SyntaxTree.allImports syntax
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
