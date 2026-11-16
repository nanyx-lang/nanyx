/// Rename: a name and every use of it, within the module, or across the files of its package. Positions are character
/// offsets into the source as the parser saw it, with line endings normalised to `\n`, as for `Hover`.
module NyxCompiler.Refactorings.Rename

open System.IO
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

/// What a rename changes: the edits to each file, by full path, and the files it renames, each from its full path to
/// its new one. A module's file is renamed with the module when it is named for it.
type Renaming = { Edits: (string * TextEdit list) list; Files: (string * string) list }

/// What is renamed: a symbol (a context's member among them, which is also written where an instance of its context
/// is), a record's field, which has no symbol, by the record it belongs to, or a module, by its name.
type private Subject =
    | Named of Symbol
    | Field of name: string * FieldSites.Record
    | ModuleName of string

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

/// The line of `offset` in `source`, counted from 1.
let private lineOf (source: string) (offset: int) = (source.Substring(0, offset) |> Seq.filter ((=) '\n') |> Seq.length) + 1

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
        | TypeDefinition
        | ContextMember -> None
        | ImportedModule when symbol.Declaration.File = file && not (hasAlias source symbol) ->
            Some "It is imported without an alias: add `as` and a name to rename it"
        | ImportedModule -> None

/// The symbol named at `offset`, with the span of the name, or the name that ends there: an editor's cursor is often
/// just after the name it is on.
let private symbolAt (file: string) (result: CompileResult) (offset: int) =
    Definition.symbolAndNameAt file result offset
    |> Option.orElseWith (fun () -> if offset > 0 then Definition.symbolAndNameAt file result (offset - 1) else None)

/// Whether `span` holds `offset`, or ends there.
let private touches (offset: int) (span: SourceSpan) = span.Start <= offset && offset <= span.End

/// The package the file at `file` is a source file of, if it is in one.
let private packageOf (file: string) =
    match NyxCompiler.Package.modulesFor file with
    | Ok (Some modules) -> Some modules.Package
    | _ -> None

/// What is named at `offset` in `source`, the text of the root module of `result` compiled as `file`, with where it is
/// written, or why it can't be renamed in `scope`: a module where its `module` line or an import of it names it, a
/// context's member where an instance of its context names it, a symbol, or else a record's field. `None` where nothing
/// is named.
let private subjectAt (scope: Scope) (file: string) (source: string) (result: CompileResult) (offset: int) : Result<Subject * SourceSpan, string> option =
    let moduleNamed =
        match scope with
        | Package files when file <> "" ->
            let declared = ModuleSites.declaration source result |> Option.filter (snd >> touches offset)
            let imported =
                packageOf file
                |> Option.bind (fun package -> ModuleSites.importAt package source result offset)
                |> Option.filter (fun (name, _) ->
                    result.Typed
                    |> Option.exists (fun typed -> typed.ImportedFiles |> Map.exists (fun _ imported -> imported.Name = Some name && files |> List.exists (PackageModules.samePath imported.File))))
            declared |> Option.orElse imported |> Option.map (fun (name, span) -> Ok (ModuleName name, span))
        | _ -> None
    let memberNamed () =
        FieldSites.members result |> List.tryFind (fun site -> touches offset site.Span) |> Option.map (fun site -> Ok (Named site.Member, site.Span))
    let symbolNamed () =
        symbolAt file result offset
        |> Option.map (fun (symbol, span) ->
            match refusal scope file source symbol, ownPart source symbol.Name span with
            | Some reason, _ -> Error reason
            | None, Some own -> Ok (Named symbol, own)
            | None, None -> Error "It isn't written by its name here")
    let fieldNamed () =
        FieldSites.fields result
        |> List.tryFind (fun site -> touches offset site.Span && site.Shorthand.IsNone)
        |> Option.bind (fun site ->
            match site.Record with
            | FieldSites.Open -> Some (Error $"Rename can't tell which record's `{site.Name}` this is: the type of the record is not known here")
            | FieldSites.NotARecord -> None
            | record -> Some (Ok (Field(site.Name, record), site.Span)))
    moduleNamed
    |> Option.orElseWith memberNamed
    |> Option.orElseWith symbolNamed
    |> Option.orElseWith fieldNamed

/// The name a subject has, as an editor shows it to be renamed.
let private nameOf (subject: Subject) =
    match subject with
    | Named symbol -> ownName symbol.Name
    | Field(name, _) -> name
    | ModuleName name -> name

/// The name at `offset` in `source`, the text of the root module of `result` compiled as `file`, that an editor can
/// rename in `scope`, or why it can't. `None` where no name is, or where the module did not type-check.
let prepare (scope: Scope) (file: string) (source: string) (result: CompileResult) (offset: int) : Result<Target, string> option =
    let source = normalise source
    subjectAt scope file source result offset |> Option.map (Result.map (fun (subject, span) -> { Name = nameOf subject; Span = span }))

/// Why `name` can't be the new name of `subject`, if it can't.
let private invalidName (subject: Subject) (name: string) =
    let invalidPart (part: string) =
        if not (Names.isIdentifier part) then Some $"`{name}` is not a name"
        elif Keywords.reserved.Contains part then Some $"`{name}` is a keyword"
        elif MadeUpNames.isMadeUp part then Some "Names that start with `__` are the compiler's"
        else None
    match subject with
    | ModuleName _ -> name.Split '.' |> Array.tryPick invalidPart
    | Named symbol when symbol.Kind = TypeDefinition && invalidPart name = None && Names.isTypeVariable name ->
        Some $"`{name}` would be a type variable, not a type's name"
    | _ -> invalidPart name

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
/// `Point.show`), at their declarations and qualified uses; for a context's member, its name where an instance of the
/// context is written; and for an import, its alias before each type written under it. A symbol of another compile is
/// found by `sameSymbol`.
let private symbolEdits (symbol: Symbol) (newName: string) (file: ModuleFile) =
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
        let instances =
            if symbol.Kind <> ContextMember then []
            else FieldSites.members file.Result |> List.filter (fun site -> Definition.sameSymbol symbol site.Member) |> List.map _.Span
        // A type is written under an import's alias too (`s.Point`), though it refers to the type, not the import.
        let aliasInTypes =
            match file.Result.Syntax with
            | Some syntax when symbol.Kind = ImportedModule && symbol.Declaration.File = file.Path ->
                (syntax |> List.collect SyntaxTree.topLevelTypes) @ (Hover.allExpressions syntax |> List.collect SyntaxTree.expressionTypes)
                |> List.collect TypeExpr.within
                |> List.choose Definition.typeNameSpan
                |> List.choose (typePart file.Source symbol.Name)
            | _ -> []
        own @ attached @ instances @ aliasInTypes |> List.map (fun span -> { Span = span; NewText = newName })

/// Where `file` writes the field `name` of `record`, each with its new text, or, where a field of that name is read from
/// a record whose type is not known, why rename can't tell whether it is the one renamed. A tuple pattern that takes the
/// field apart by its name alone, `(x, y)`, is written by member, `(newName = x, y = y)`, since the two can't be mixed.
let private fieldEdits (name: string) (record: FieldSites.Record) (newName: string) (file: ModuleFile) =
    let sites = FieldSites.fields file.Result
    let named = sites |> List.filter (fun site -> site.Name = name)
    match named |> List.tryFind (fun site -> site.Record = FieldSites.Open) with
    | Some unknown ->
        let where = if file.Path = "" then "" else $" of {Path.GetFileName file.Path}"
        Error $"Rename can't tell whether `{name}` on line {lineOf file.Source unknown.Span.Start}{where} is the same field: the type of its record is not known there"
    | None ->
        let renamed = named |> List.filter (fun site -> site.Record = record)
        let tuples = renamed |> List.choose _.Shorthand |> Set.ofList
        let byMember =
            sites
            |> List.filter (fun site -> site.Shorthand |> Option.exists tuples.Contains)
            |> List.map (fun site -> { Span = { site.Span with End = site.Span.Start }; NewText = $"{(if site.Name = name then newName else site.Name)} = " })
        let byName = renamed |> List.filter (fun site -> site.Shorthand.IsNone) |> List.map (fun site -> { Span = site.Span; NewText = newName })
        Ok (byName @ byMember)

/// The files of the module `name` among `files`, each renamed to `newName` where it is named for the module, with
/// the extension it has, unless a file of that name is there already.
let private renamedFiles (name: string) (newName: string) (files: ModuleFile list) =
    files
    |> List.filter (fun file -> file.Result.ModuleName = Some name && Path.GetFileNameWithoutExtension file.Path = name)
    |> List.map (fun file -> file.Path, Path.Combine(Path.GetDirectoryName file.Path, newName + Path.GetExtension file.Path))
    |> List.filter (fun (_, renamed) -> not (File.Exists renamed))

/// The edits in `file` that rename the module `name` of the package `package` to `newName`: its `module` line, if it
/// declares it, and each import of it, with the name of each file imported by path among `renamed`.
let private moduleEdits (package: string) (name: string) (newName: string) (renamed: (string * string) list) (file: ModuleFile) =
    let declared =
        ModuleSites.declaration file.Source file.Result
        |> Option.filter (fst >> (=) name)
        |> Option.map (fun (_, span) -> { Span = span; NewText = newName })
        |> Option.toList
    declared @ ModuleSites.imports package name newName renamed file.Path file.Source file.Result

/// `source` with `edits`, which do not overlap, made to it.
let private applied (source: string) (edits: TextEdit list) =
    edits
    |> List.sortByDescending (fun edit -> edit.Span.Start)
    |> List.fold (fun (text: string) edit -> text.Substring(0, edit.Span.Start) + edit.NewText + text.Substring(edit.Span.End)) source

/// Where an offset of a text is once `edits` are made to it: text inserted at the offset comes before it.
let private movedBy (edits: TextEdit list) (offset: int) =
    edits
    |> List.filter (fun edit -> edit.Span.Start < offset || (edit.Span.Start = offset && edit.Span.End = offset))
    |> List.sumBy (fun edit -> edit.NewText.Length - (edit.Span.End - edit.Span.Start))
    |> (+) offset

/// The edits to each of `files` that rename `subject` to `newName`, and the files renamed, or why it can't be.
let private planned (subject: Subject) (newName: string) (files: ModuleFile list) : Result<(ModuleFile * TextEdit list) list * (string * string) list, string> =
    let distinct (edits: TextEdit list) = edits |> List.distinctBy (fun edit -> edit.Span.Start) |> List.sortBy (fun edit -> edit.Span.Start)
    match subject with
    | Named symbol -> Ok (files |> List.map (fun file -> file, distinct (symbolEdits symbol newName file)), [])
    | Field(name, record) ->
        let edits = files |> List.map (fun file -> file, fieldEdits name record newName file)
        match edits |> List.tryPick (fun (_, edits) -> match edits with Error reason -> Some reason | Ok _ -> None) with
        | Some reason -> Error reason
        | None -> Ok (edits |> List.map (fun (file, edits) -> file, edits |> Result.defaultValue [] |> distinct), [])
    | ModuleName name ->
        match files |> List.tryHead |> Option.bind (fun file -> packageOf file.Path) with
        | None -> Error "The file is in no package, so rename can't find the files that import its module"
        | Some _ when not (files |> List.exists (fun file -> file.Result.ModuleName = Some name)) -> Error $"No file of this package declares the module `{name}`"
        | Some package ->
            let renamed = renamedFiles name newName files
            Ok (files |> List.map (fun file -> file, distinct (moduleEdits package name newName renamed file)), renamed)

/// The edits, file by file, that rename the name at `offset` in the file `file`, one of `files`, and every use of it in
/// `files`, to `newName`, with the files renamed, or why it can't be. `files` are the module alone, for `Module`, or
/// every source file of the package, for `Package`, each with its compile. `recompile` compiles the files again, given
/// each one's path and text, as they were compiled, and gives their compiles in the same order: the renamed files are
/// compiled, and the rename refused unless every name in them refers to what it did before, so that a rename that would
/// hide a name, or be hidden by one, at any use is refused with the use named. An attached definition keeps its type's
/// part (`string.` in `string.shout`), and renaming a type renames that part of its attached definitions' names. A
/// record's field is renamed wherever a record of the same type writes it, and refused where a field of its name is
/// read from a record of a type not known; a module is renamed in its `module` line and each import of it, and its
/// file with it where the file is named for it.
let editsIn
    (scope: Scope)
    (files: ModuleFile list)
    (file: string)
    (offset: int)
    (newName: string)
    (recompile: (string * string) list -> CompileResult list)
    : Result<Renaming, string> =
    let files = files |> List.map (fun moduleFile -> { moduleFile with Source = normalise moduleFile.Source })
    let root = files |> List.tryFind (fun moduleFile -> moduleFile.Path = file)
    match root |> Option.bind (fun root -> subjectAt scope file root.Source root.Result offset) with
    | None -> Error "There is no name here to rename"
    | Some (Error reason) -> Error reason
    | Some (Ok (subject, _)) when nameOf subject = newName -> Ok { Edits = []; Files = [] }
    | Some (Ok (subject, _)) ->
        match invalidName subject newName, planned subject newName files with
        | Some reason, _
        | None, Error reason -> Error reason
        | None, Ok (edits, renamedFiles) ->
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
                | [] ->
                    Ok { Edits = edits |> List.filter (snd >> List.isEmpty >> not) |> List.map (fun (moduleFile, edits) -> moduleFile.Path, edits)
                         Files = renamedFiles }
                | ((path, start), _) :: _ ->
                    let text = renamed |> List.find (fst >> (=) path) |> snd
                    let length = text.Substring(start) |> Seq.takeWhile (fun c -> System.Char.IsLetterOrDigit c || c = '_' || c = '.') |> Seq.length
                    let where = if path = file then "" else $" of {Path.GetFileName path}"
                    Error $"`{text.Substring(start, length)}` on line {lineOf text start}{where} would refer to something else"

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
    |> Result.map (fun renaming -> renaming.Edits |> List.collect snd)
