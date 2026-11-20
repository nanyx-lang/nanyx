/// The parser's public API: parsing a module's source, with its declared names or its trivia.
[<AutoOpen>]
module Parser.Program.Api

open Parser.Program

let internal normalizeSource (input: string) =
    input.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd()

/// Parses normalised source, returning the top-level items as written and the final state (`DescentTopLevel`), or the
/// first mistake in it.
let internal runModuleParser (filePath: string) (normalized: string) : Result<TopLevelItem list * ParseState, ParseError> =
    DescentTopLevel.tryReadModuleWithState filePath normalized

/// Parses the source of the file at `filePath` as `parseModuleFileWithDocs` does, reading past each mistake: the items
/// that read, with every syntax error found (see `DescentTopLevel.readModuleRecovering`), which may be none. An item
/// with a mistake in it is left out.
let parseModuleFileWithDocsRecovering (filePath: string) (input: string)
    : (Module * DeclaredNames * (ModuleName * SourceSpan) list) * Lazy<FileDocComments> * ParseError list =
    let normalized = normalizeSource input
    let items, state, errors = DescentTopLevel.readModuleRecovering filePath normalized
    (expandMidFileModules items, state.Declared, state.ModuleBlocks),
    lazy (DocComments.attach filePath normalized items state.Declared state.DocumentableStarts state.Comments),
    errors

/// Parses the source of the file at `filePath`, as `parseModuleFileWithNames` does, together with its doc comments,
/// each attached to what it documents, which are found the first time they are asked for.
let parseModuleFileWithDocs (filePath: string) (input: string) : Result<(Module * DeclaredNames * (ModuleName * SourceSpan) list) * Lazy<FileDocComments>, ParseError> =
    let normalized = normalizeSource input
    runModuleParser filePath normalized
    |> Result.map (fun (items, state) ->
        (expandMidFileModules items, state.Declared, state.ModuleBlocks),
        lazy (DocComments.attach filePath normalized items state.Declared state.DocumentableStarts state.Comments))

/// Parses the source of the file at `filePath`, as `parseModuleFile` does, together with the names its nodes
/// declare and the text of each `module name =` block in it.
let parseModuleFileWithNames (filePath: string) (input: string) : Result<Module * DeclaredNames * (ModuleName * SourceSpan) list, ParseError> =
    parseModuleFileWithDocs filePath input |> Result.map fst

/// Parses the source of the file at `filePath` as it is written, keeping its comments and blank lines, for tools
/// that print it back (the formatter). The items are not rewritten: see `SourceModule`.
let parseModuleWithTrivia (filePath: string) (input: string) : Result<SourceModule, ParseError> =
    let normalized = normalizeSource input
    runModuleParser filePath normalized
    |> Result.map (fun (items, state) ->
        { Items = items
          Trivia = collectTrivia filePath normalized state.Comments state.StringTexts
          Source = normalized })

/// Parses the source of the file at `filePath`, which is what the spans of the module's nodes refer to. Line
/// endings are normalised to `\n` first, so span offsets are into the normalised source (lines and columns are
/// unaffected).
let parseModuleFile (filePath: string) (input: string) : Result<Module, string> =
    parseModuleFileWithNames filePath input
    |> Result.map (fun (module', _, _) -> module')
    |> Result.mapError (fun error -> error.Message)

/// Parses source that has no file of its own.
let parseModule (input: string) : Result<Module, string> = parseModuleFile "" input

/// Parses `input` as the type in a def's annotation is written: `bool -> string`, `list(a), (a -> bool) -> list(a)`.
let parseTypeSignature (input: string) : Result<TypeExpr, string> =
    DescentTypes.readTypeSignature (normalizeSource input) |> Result.mapError _.Message
