/// Rename: a name and every use of it, within the module, or across the files of its package. Positions are character
/// offsets into the source as the parser saw it, with line endings normalised to `\n`, as for `Hover`.
module NyxCompiler.Refactorings.Rename

open Parser.Program
open NyxCompiler

/// The name an editor renames: its own part (`shout` in `string.shout`), and where that is written at the request.
type Target = { Name: string; Span: SourceSpan }

/// Where a rename looks for uses of the name: the module alone, for a file in no package, or every source file of the
/// package, by full path, as `nanyx build` checks them.
type Scope =
    | Module
    | Package of files: string list

/// A file a rename reads: its full path (empty for source compiled directly), its text and its compile.
type ModuleFile = { Path: string; Source: string; Result: CompileResult }

let private normalise (source: string) = source.Replace("\r\n", "\n").Replace("\r", "\n")

/// The part of a name after its last dot: `shout` in `string.shout`.
let private ownName (name: string) = name.Substring(name.LastIndexOf '.' + 1)

/// The text of `span` in `source`.
let private textOf (source: string) (span: SourceSpan) = source.Substring(span.Start, span.End - span.Start)

/// The part of `span`, written in `source`, that is `name`'s own part, if it ends with it.
let private ownPart (source: string) (name: string) (span: SourceSpan) =
    let own = ownName name
    let text = textOf source span
    if text.EndsWith own then Some { span with Start = span.End - own.Length; StartColumn = span.StartColumn + text.Length - own.Length }
    else None

/// The part of `span`, written in `source`, that is the type's part `typeName` of an attached definition's name, if it
/// starts with it: `Point` in `Point.show`.
let private typePart (source: string) (typeName: string) (span: SourceSpan) =
    if (textOf source span).StartsWith(typeName + ".") then Some { span with End = span.Start + typeName.Length }
    else None

/// Whether the import `symbol`, declared in `source`, is written with an alias (`import nanyx/text as t`).
let private hasAlias (source: string) (symbol: Symbol) =
    System.Text.RegularExpressions.Regex.IsMatch(textOf source symbol.Declaration, $@"\bas\s+{System.Text.RegularExpressions.Regex.Escape symbol.Name}$")

/// Why `symbol`, declared in `source` if it is declared in the module compiled as `file`, can't be renamed in `scope`,
/// if it can't.
let private refusal (scope: Scope) (file: string) (source: string) (symbol: Symbol) =
    let inScope =
        match scope with
        | Module -> symbol.Declaration.File = file
        | Package files -> files |> List.contains symbol.Declaration.File
    if SourceSpan.isNone symbol.Declaration then Some "It is built in"
    elif not inScope then
        match scope with
        | Module -> Some "It is declared in another module, and this file is in no package, so rename can't find the files that use it"
        | Package _ -> Some "It is declared outside this package"
    elif MadeUpNames.isMadeUp symbol.Name then Some "The compiler made it up"
    elif symbol.IsExported && scope = Module then Some "It is exported, and this file is in no package, so rename can't find the files that use it"
    else
        match symbol.Kind with
        | TopLevelValue
        | LocalValue
        | Parameter
        | PatternVariable
        | TypeDefinition -> None
        | ImportedModule when symbol.Declaration.File = file && not (hasAlias source symbol) ->
            Some "It is imported without an alias: add `as` and a name to rename it"
        | ImportedModule -> None
        | ContextMember -> Some "Renaming a context's member isn't supported yet"

/// The symbol named at `offset`, with the span of the name, or the name that ends there: an editor's cursor is often
/// just after the name it is on.
let private symbolAt (file: string) (result: CompileResult) (offset: int) =
    Definition.symbolAndNameAt file result offset
    |> Option.orElseWith (fun () -> if offset > 0 then Definition.symbolAndNameAt file result (offset - 1) else None)

/// The name at `offset` in `source`, the text of the root module of `result` compiled as `file`, that an editor can
/// rename in `scope`, or why it can't. `None` where no name is, or where the module did not type-check.
let prepare (scope: Scope) (file: string) (source: string) (result: CompileResult) (offset: int) : Result<Target, string> option =
    let source = normalise source
    symbolAt file result offset
    |> Option.map (fun (symbol, span) ->
        match refusal scope file source symbol, ownPart source symbol.Name span with
        | Some reason, _ -> Error reason
        | None, Some own -> Ok { Name = ownName symbol.Name; Span = own }
        | None, None -> Error "It isn't written by its name here")

/// Why `name` can't be the new name of a declaration of `kind`, if it can't.
let private invalidName (kind: SymbolKind) (name: string) =
    if not (Names.isIdentifier name) then Some $"`{name}` is not a name"
    elif Keywords.reserved.Contains name then Some $"`{name}` is a keyword"
    elif MadeUpNames.isMadeUp name then Some "Names that start with `__` are the compiler's"
    elif kind = TypeDefinition && Names.isTypeVariable name then Some $"`{name}` would be a type variable, not a type's name"
    else None

/// Where each name in `result`, the compile of the file `path`, refers to: the file and start of the name, and the
/// file and start of the declaration of what it refers to, with each moved by `move`, given the file it is in.
let private resolutions (path: string) (move: string -> int -> int) (result: CompileResult) =
    match result.Typed with
    | Some typed ->
        Definition.referenceSites result
        |> List.choose (fun (span, id) ->
            typed.Symbols
            |> Map.tryFind id
            |> Option.map (fun symbol ->
                let declaration = symbol.Declaration
                (path, move path span.Start), (declaration.File, move declaration.File declaration.Start)))
        |> Set.ofList
    | None -> Set.empty

/// The spans in `file` that renaming `symbol` rewrites, each with its new text: its own part at its declaration and
/// at each use; for a type, the type's part of the name of each of its attached definitions (`Point` in
/// `Point.show`), at their declarations and qualified uses; and for an import, its alias before each type written
/// under it. A symbol of another compile is found by `sameSymbol`.
let private renamedSpans (symbol: Symbol) (newName: string) (file: ModuleFile) =
    match file.Result.Typed with
    | None -> []
    | Some typed ->
        let symbols = typed.Symbols |> Map.values |> Seq.toList
        let declarationAndUses (of': Symbol) =
            symbols
            |> List.filter (Definition.sameSymbol of')
            |> List.collect (Definition.referencesTo file.Result)
            |> List.append (if of'.Declaration.File = file.Path then [ of'.Declaration ] else [])
        let own = declarationAndUses symbol |> List.choose (ownPart file.Source symbol.Name)
        let attached =
            if symbol.Kind <> TypeDefinition || not (symbols |> List.exists (Definition.sameSymbol symbol)) then []
            else
                symbols
                |> List.filter (fun other -> other.Kind = TopLevelValue && other.Name.StartsWith(symbol.Name + "."))
                |> List.collect declarationAndUses
                |> List.choose (typePart file.Source symbol.Name)
        // A type is written under an import's alias too (`s.Point`), though it refers to the type, not the import.
        let aliasInTypes =
            match file.Result.Syntax with
            | Some syntax when symbol.Kind = ImportedModule && symbol.Declaration.File = file.Path ->
                (syntax |> List.collect SyntaxTree.topLevelTypes) @ (Hover.allExpressions syntax |> List.collect SyntaxTree.expressionTypes)
                |> List.collect TypeExpr.within
                |> List.choose Definition.typeNameSpan
                |> List.choose (typePart file.Source symbol.Name)
            | _ -> []
        own @ attached @ aliasInTypes
        |> List.distinctBy (fun span -> span.Start)
        |> List.sortBy (fun span -> span.Start)
        |> List.map (fun span -> { Span = span; NewText = newName })

/// `source` with `edits`, which do not overlap, made to it.
let private applied (source: string) (edits: TextEdit list) =
    edits
    |> List.sortByDescending (fun edit -> edit.Span.Start)
    |> List.fold (fun (text: string) edit -> text.Substring(0, edit.Span.Start) + edit.NewText + text.Substring(edit.Span.End)) source

/// Where an offset of a text is once `edits` are made to it.
let private movedBy (edits: TextEdit list) (offset: int) =
    offset + (edits |> List.filter (fun edit -> edit.Span.Start < offset) |> List.sumBy (fun edit -> edit.NewText.Length - (edit.Span.End - edit.Span.Start)))

/// The edits, file by file, that rename the name at `offset` in the file `file`, one of `files`, and every use of it in
/// `files`, to `newName`, or why it can't be. `files` are the module alone, for `Module`, or every source file of the
/// package, for `Package`, each with its compile. `recompile` compiles the files again, given each one's path and text,
/// as they were compiled, and gives their compiles in the same order: the renamed files are compiled, and the rename
/// refused unless every name in them refers to what it did before, so that a rename that would hide a name, or be
/// hidden by one, at any use is refused with the use named. An attached definition keeps its type's part
/// (`string.` in `string.shout`), and renaming a type renames that part of its attached definitions' names.
let editsIn
    (scope: Scope)
    (files: ModuleFile list)
    (file: string)
    (offset: int)
    (newName: string)
    (recompile: (string * string) list -> CompileResult list)
    : Result<(string * TextEdit list) list, string> =
    let files = files |> List.map (fun moduleFile -> { moduleFile with Source = normalise moduleFile.Source })
    let root = files |> List.tryFind (fun moduleFile -> moduleFile.Path = file)
    let prepared = root |> Option.bind (fun root -> prepare scope file root.Source root.Result offset)
    match prepared, root |> Option.bind (fun root -> symbolAt file root.Result offset) with
    | None, _
    | _, None -> Error "There is no name here to rename"
    | Some (Error reason), _ -> Error reason
    | Some (Ok target), Some _ when target.Name = newName -> Ok []
    | Some (Ok _), Some (symbol, _) ->
        match invalidName symbol.Kind newName with
        | Some reason -> Error reason
        | None ->
            let edits = files |> List.map (fun moduleFile -> moduleFile, renamedSpans symbol newName moduleFile)
            let editsOf path = edits |> List.tryPick (fun (moduleFile, edits) -> if moduleFile.Path = path then Some edits else None)
            let move path offset = match editsOf path with Some edits -> movedBy edits offset | None -> offset
            let renamed = edits |> List.map (fun (moduleFile, edits) -> moduleFile.Path, applied moduleFile.Source edits)
            let after = recompile renamed
            let compared = List.zip3 files renamed after
            let newError =
                compared |> List.tryPick (fun (before, _, after) -> RefactoringText.newError before.Result after)
            match newError with
            | Some reason ->
                let what = if files.Length = 1 then "module" else "package"
                Error $"The renamed {what} would not compile{reason}"
            | None ->
                let before = files |> List.map (fun moduleFile -> resolutions moduleFile.Path move moduleFile.Result) |> Set.unionMany
                let now = compared |> List.map (fun (moduleFile, _, after) -> resolutions moduleFile.Path (fun _ offset -> offset) after) |> Set.unionMany
                match Set.difference now before |> Set.toList with
                | [] -> Ok (edits |> List.filter (snd >> List.isEmpty >> not) |> List.map (fun (moduleFile, edits) -> moduleFile.Path, edits))
                | ((path, start), _) :: _ ->
                    let text = renamed |> List.find (fst >> (=) path) |> snd
                    let line = text.Substring(0, start) |> Seq.filter ((=) '\n') |> Seq.length
                    let length = text.Substring(start) |> Seq.takeWhile (fun c -> System.Char.IsLetterOrDigit c || c = '_' || c = '.') |> Seq.length
                    let where = if path = file then "" else $" of {System.IO.Path.GetFileName path}"
                    Error $"`{text.Substring(start, length)}` on line {line + 1}{where} would refer to something else"

/// The edits that rename the name at `offset` in `source`, the text of the root module of `result` compiled as `file`,
/// and every use of it in the module, to `newName`, or why it can't be, as `editsIn` finds them for the module alone.
/// `recompile` compiles a text of the module as `result` was compiled.
let edits
    (file: string)
    (source: string)
    (result: CompileResult)
    (offset: int)
    (newName: string)
    (recompile: string -> CompileResult)
    : Result<TextEdit list, string> =
    editsIn Module [ { Path = file; Source = source; Result = result } ] file offset newName (List.map (snd >> recompile))
    |> Result.map (List.collect snd)
