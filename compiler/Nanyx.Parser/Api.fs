/// The parser's public API: parsing a module's source, with its declared names or its trivia.
[<AutoOpen>]
module Parser.Program.Api

open System
open FParsec
// After the libraries, so that the parser's names shadow theirs (identifier, Attribute) as they did in one file.
open Parser.Program

let internal normalizeSource (input: string) =
    input.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd()

/// Parses normalised source, returning the top-level items as written and the final state.
let internal runModuleParser (filePath: string) (normalized: string) : Result<TopLevelItem list * ParseState, ParseError> =
    let initialState = ParseState.empty ()
    // A parse fails at a point: its span is the character there, or nothing at the end of the input.
    let errorAt (position: Position) message =
        { Message = message
          Span = spanOf position (min normalized.Length (int position.Index + 1)) }
    let reportAt position messages =
        use stream = new CharStream<unit>(normalized, 0, normalized.Length)
        stream.Name <- filePath
        Result.Error (errorAt position (ParserError(position, null, messages).ToString stream))
    match runParserOnString moduleParser initialState filePath (Directives.blank normalized) with
    // An operator written without a space on each side is parsed as if it were spaced, and reported once the parse is
    // done (see `ParseState.MisspacedOperator`), so that no other reading of the text hides it.
    | _ when initialState.MisspacedOperator.Value.IsSome ->
        let position, message = initialState.MisspacedOperator.Value.Value
        reportAt position (ErrorMessageList(ErrorMessage.Message message))
    | ParserResult.Success(result, state, _) -> Result.Ok (result, state)
    // A directive after the code has started is not skipped, and fails where it starts.
    | ParserResult.Failure(_, error, _) when error.Position.Column = 1L && String.CompareOrdinal(normalized, int error.Position.Index, "::", 0, 2) = 0 ->
        Result.Error (errorAt error.Position "A directive (a `::` line) must come before the first line of code")
    | ParserResult.Failure(errorMsg, error, _) ->
        // A mistake the parser recognised is backtracked over, and FParsec reports some enclosing construct
        // instead (often the whole def), so it is reported where it happened (see `reportFocused`). So is a failure
        // inside brackets that FParsec backtracked out of (see `noteFailureInside`).
        match initialState.FocusedError.Value, initialState.FailureInBrackets.Value with
        | Some (position, message), _ -> reportAt position (ErrorMessageList(ErrorMessage.Message message))
        | None, Some (position, messages) when position.Index > error.Position.Index -> reportAt position messages
        | None, _ -> Result.Error (errorAt error.Position errorMsg)

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

