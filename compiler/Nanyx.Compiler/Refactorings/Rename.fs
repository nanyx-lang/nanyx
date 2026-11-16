/// Rename: a name and every use of it, within the module. Positions are character offsets into the source as the
/// parser saw it, with line endings normalised to `\n`, as for `Hover`.
module NyxCompiler.Refactorings.Rename

open Parser.Program
open NyxCompiler

/// The name an editor renames: its own part (`shout` in `string.shout`), and where that is written at the request.
type Target = { Name: string; Span: SourceSpan }

/// The part of a name after its last dot: `shout` in `string.shout`.
let private ownName (name: string) = name.Substring(name.LastIndexOf '.' + 1)

/// The part of `span`, written in `source`, that is `name`'s own part, if it ends with it.
let private ownPart (source: string) (name: string) (span: SourceSpan) =
    let own = ownName name
    let text = source.Substring(span.Start, span.End - span.Start)
    if text.EndsWith own then Some { span with Start = span.End - own.Length; StartColumn = span.StartColumn + text.Length - own.Length }
    else None

/// Why `symbol`, of the module compiled as `file`, can't be renamed, if it can't.
let private refusal (file: string) (symbol: Symbol) =
    if SourceSpan.isNone symbol.Declaration then Some "It is built in"
    elif symbol.Declaration.File <> file then Some "It is declared in another module: rename works within one module for now"
    elif MadeUpNames.isMadeUp symbol.Name then Some "The compiler made it up"
    elif symbol.IsExported then Some "It is exported: other modules may use it, and rename can't find uses in other files yet"
    else
        match symbol.Kind with
        | TopLevelValue
        | LocalValue
        | Parameter
        | PatternVariable
        | TypeDefinition -> None
        | ImportedModule -> Some "Renaming an import's alias isn't supported yet"
        | ContextMember -> Some "Renaming a context's member isn't supported yet"

/// The symbol named at `offset`, with the span of the name, or the name that ends there: an editor's cursor is often
/// just after the name it is on.
let private symbolAt (file: string) (result: CompileResult) (offset: int) =
    Definition.symbolAndNameAt file result offset
    |> Option.orElseWith (fun () -> if offset > 0 then Definition.symbolAndNameAt file result (offset - 1) else None)

/// The name at `offset` in `source`, the text of the root module of `result` compiled as `file`, that an editor can
/// rename, or why it can't. `None` where no name is, or where the module did not type-check.
let prepare (file: string) (source: string) (result: CompileResult) (offset: int) : Result<Target, string> option =
    let source = source.Replace("\r\n", "\n").Replace("\r", "\n")
    symbolAt file result offset
    |> Option.map (fun (symbol, span) ->
        match refusal file symbol, ownPart source symbol.Name span with
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

/// Where each name in `result` refers to: the start of the name, and the file and start of the declaration of what it
/// refers to, with each in `file` moved by `move`.
let private resolutions (file: string) (move: int -> int) (result: CompileResult) =
    match result.Typed with
    | Some typed ->
        Definition.referenceSites result
        |> List.choose (fun (span, id) ->
            typed.Symbols
            |> Map.tryFind id
            |> Option.map (fun symbol ->
                let declaration = symbol.Declaration
                move span.Start, (declaration.File, if declaration.File = file then move declaration.Start else declaration.Start)))
        |> Set.ofList
    | None -> Set.empty

let private errorCount (result: CompileResult) =
    result.Diagnostics |> List.filter (fun diagnostic -> diagnostic.Severity = Severity.Error) |> List.length

/// The edits that rename the name at `offset` in `source`, the text of the root module of `result` compiled as `file`,
/// and every use of it in the module, to `newName`, or why it can't be. `recompile` compiles a text of the module as
/// `result` was compiled: the renamed text is compiled, and the rename refused unless every name in it refers to what
/// it did before, so that a rename that would hide a name, or be hidden by one, at any use is refused with the use
/// named. An attached definition keeps its type's part (`string.` in `string.shout`).
let edits
    (file: string)
    (source: string)
    (result: CompileResult)
    (offset: int)
    (newName: string)
    (recompile: string -> CompileResult)
    : Result<TextEdit list, string> =
    let source = source.Replace("\r\n", "\n").Replace("\r", "\n")
    match prepare file source result offset, symbolAt file result offset with
    | None, _
    | _, None -> Error "There is no name here to rename"
    | Some (Error reason), _ -> Error reason
    | Some (Ok target), Some _ when target.Name = newName -> Ok []
    | Some (Ok target), Some (symbol, _) ->
        match invalidName symbol.Kind newName with
        | Some reason -> Error reason
        | None ->
            let spans =
                symbol.Declaration :: Definition.referencesTo result symbol
                |> List.choose (ownPart source symbol.Name)
                |> List.distinctBy (fun span -> span.Start)
                |> List.sortBy (fun span -> span.Start)
            let edits = spans |> List.map (fun span -> { Span = span; NewText = newName })
            let renamed =
                spans
                |> List.rev
                |> List.fold (fun (text: string) span -> text.Substring(0, span.Start) + newName + text.Substring(span.End)) source
            // Where an offset of the old text is in the new one.
            let grows = newName.Length - target.Name.Length
            let move (offset: int) = offset + grows * (spans |> List.filter (fun span -> span.Start < offset) |> List.length)
            let after = recompile renamed
            let before = resolutions file move result
            let now = resolutions file id after
            if after.Typed.IsNone || errorCount after > errorCount result then
                let reason =
                    after.Diagnostics
                    |> List.tryFind (fun diagnostic -> diagnostic.Severity = Severity.Error && not (result.Diagnostics |> List.exists (fun old -> old.Message = diagnostic.Message)))
                    |> Option.map (fun diagnostic -> $": {diagnostic.Message}")
                    |> Option.defaultValue ""
                Error $"The renamed module would not compile{reason}"
            else
                match Set.difference now before |> Set.toList with
                | [] -> Ok edits
                | (start, _) :: _ ->
                    let line = renamed.Substring(0, start) |> Seq.filter ((=) '\n') |> Seq.length
                    let length = renamed.Substring(start) |> Seq.takeWhile (fun c -> System.Char.IsLetterOrDigit c || c = '_' || c = '.') |> Seq.length
                    Error $"`{renamed.Substring(start, length)}` on line {line + 1} would refer to something else"
