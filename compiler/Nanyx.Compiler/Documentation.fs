/// The doc comments an editor shows for a name: on hover, and with a completion item. Positions are character offsets
/// into the source as the parser saw it, with line endings normalised to `\n`, as for `Hover`.
module NyxCompiler.Documentation

open System
open System.Collections.Concurrent
open System.IO
open System.Text.RegularExpressions
open Parser.Program

/// The doc comments of the source of the file at `file`, attached to what they document. A file that does not parse
/// has none.
let ofSource (file: string) (source: string) : FileDocComments = DocComments.parse file source

/// The doc comments of each file other than the document, by its name, with the time it was last written: a file on
/// disk is parsed again when it has changed, and a standard-library module, whose time is `DateTime.MinValue`, never is.
let private others = ConcurrentDictionary<string, DateTime * FileDocComments>()

/// The doc comments of the file at `file`, which is not the document: a file on disk, or a standard-library module
/// named by its import path (`nanyx/text.nyx`). `None` for any other.
let private ofOtherFile (file: string) : FileDocComments option =
    let prefix = StandardLibrary.importPrefix
    if Path.IsPathRooted file then
        if File.Exists file then
            let written = File.GetLastWriteTimeUtc file
            match others.TryGetValue file with
            | true, (stamp, docs) when stamp = written -> Some docs
            | _ ->
                let docs = ofSource file (File.ReadAllText file)
                others[file] <- (written, docs)
                Some docs
        else
            None
    elif file.StartsWith(prefix, StringComparison.Ordinal) && file.EndsWith(".nyx", StringComparison.Ordinal) then
        let moduleName = file.Substring(prefix.Length, file.Length - prefix.Length - ".nyx".Length)
        StandardLibrary.tryLoadSource moduleName
        |> Option.map (fun source -> others.GetOrAdd(file, fun _ -> DateTime.MinValue, ofSource file source) |> snd)
    else
        None

/// A name in backticks and square brackets, `` [`name`] ``, that is not already a link.
let private linkPattern = Regex(@"\[`([^`\n]+)`\](?![\(\[])", RegexOptions.Compiled)

/// The text of `comment` with each link in it, `` [`name`] ``, written as a Markdown link to where `declarationOf` finds
/// the name declared, with the target `linkTo` gives for that span, or as plain code when the name is not found or
/// `linkTo` gives `null`.
let private render (declarationOf: string -> SourceSpan option) (linkTo: Func<SourceSpan, string>) (comment: DocComment) =
    linkPattern.Replace(
        comment.Text,
        MatchEvaluator(fun found ->
            let name = found.Groups[1].Value
            match declarationOf name |> Option.map linkTo.Invoke with
            | Some target when not (isNull target) -> $"[`{name}`]({target})"
            | _ -> $"`{name}`"))

/// The doc comment of the module in `docs`, the doc comments of its file: the one at the top of the file.
let private moduleComment (docs: FileDocComments) : DocComment option =
    docs.Comments |> List.tryFind (fun comment -> comment.Target = DocumentsModule)

/// The doc comment of the module in the file `moduleFile`, which is not the document (see `ofOtherFile`), as Markdown,
/// for where there is no compile of the document to read it by: its links lead only to the names the module's own top
/// level declares, with the targets `linkTo` gives (see `Reader`). `None` when it has none.
let ofModule (moduleFile: string) (linkTo: Func<SourceSpan, string>) : string option =
    ofOtherFile moduleFile
    |> Option.bind (fun docs ->
        let declarationOf name =
            docs.TopLevel
            |> List.tryFind (fun (declared, span) -> declared = name && not (ReservedNames.isHiddenName name span))
            |> Option.map snd
        moduleComment docs |> Option.map (render declarationOf linkTo))

/// Reads the doc comments of the names in one compile of a document (`result`, of the file `file`, whose doc comments
/// are `own`), for a request at `offset`. A link in a doc comment, `` [`name`] ``, is written as a Markdown link to
/// where `name` is declared, with the target `linkTo` gives for that span, or as plain code when the name is not
/// found or `linkTo` gives `null`. A name is looked up among the names in scope where the comment is (in the document)
/// or at `offset` (in another file), and among the top-level names of the comment's own file, the nearer first.
type Reader(file: string, own: FileDocComments, result: CompileResult, offset: int, linkTo: Func<SourceSpan, string>) =
    let scopes = ConcurrentDictionary<int, Map<string, SourceSpan>>()

    /// The names in scope at `at`, unqualified and qualified (`m.foo`, `string.trim`), with where they are declared.
    let inScopeAt (at: int) =
        scopes.GetOrAdd(at, fun at ->
            let qualified =
                match result.Typed with
                | Some typed ->
                    typed.ModuleNames
                    |> Map.toList
                    |> List.choose (fun (name, id) -> typed.Symbols |> Map.tryFind id |> Option.map (fun symbol -> name, symbol.Declaration))
                | None -> []
            let unqualified = Completion.namesInScope result at |> List.map (fun symbol -> symbol.Name, symbol.Declaration)
            qualified @ unqualified |> Map.ofList)

    let declarationOf (comment: DocComment) (docs: FileDocComments) (name: string) =
        let ownFile = docs.TopLevel |> List.tryFind (fun (declared, _) -> declared = name) |> Option.map snd
        let inScope () = inScopeAt (if comment.Span.File = file then comment.Span.End else offset) |> Map.tryFind name
        let found = if comment.Span.File = file then inScope () |> Option.orElse ownFile else ownFile |> Option.orElseWith inScope
        found |> Option.filter (fun declaration -> not (ReservedNames.isHiddenName name declaration))

    let render (comment: DocComment) (docs: FileDocComments) = render (declarationOf comment docs) linkTo comment

    /// The doc comments of the file `docFile`: the document's own, or another file's (see `ofOtherFile`).
    let docsOf (docFile: string) = if docFile = file then Some own else ofOtherFile docFile

    /// Where the name is declared that the link at `at` in a doc comment of the document links to, given the
    /// document's text, `source`. `None` when `at` is not in a link, or the name is not found.
    member _.LinkTargetAt(source: string, at: int) : SourceSpan option =
        let source = source.Replace("\r\n", "\n").Replace("\r", "\n")
        own.Comments
        |> List.tryFind (fun comment -> comment.Span.Start <= at && at < comment.Span.End && comment.Span.End <= source.Length)
        |> Option.bind (fun comment ->
            linkPattern.Matches(source.Substring(comment.Span.Start, comment.Span.End - comment.Span.Start))
            |> Seq.tryFind (fun found -> comment.Span.Start + found.Index <= at && at < comment.Span.Start + found.Index + found.Length)
            |> Option.bind (fun found -> declarationOf comment own found.Groups[1].Value))

    /// The doc comment of the module in the file `moduleFile`, the document or another file, as its spans name it (see
    /// `Hover.moduleAt`), as Markdown with its links written out. `None` when it has none.
    member _.ForModule(moduleFile: string) : string option =
        docsOf moduleFile |> Option.bind (fun docs -> moduleComment docs |> Option.map (fun comment -> render comment docs))

    /// The doc comment of `symbol`, as Markdown with its links written out: for an import, that of the module it
    /// brings in. `None` when it has none.
    member this.ForSymbol(symbol: Symbol) : string option =
        let declaration = symbol.Declaration
        match symbol.Kind with
        | ImportedModule ->
            result.Typed
            |> Option.bind (fun typed -> typed.ImportedFiles |> Map.tryFind symbol.Id)
            |> Option.bind (fun imported -> this.ForModule imported.File)
        | _ ->
            docsOf declaration.File
            |> Option.bind (fun docs ->
                docs.Comments
                |> List.tryFind (fun comment ->
                    match comment.Target with
                    | DocumentsDeclaration span -> span.Start = declaration.Start && span.End = declaration.End
                    | _ -> false)
                |> Option.map (fun comment -> render comment docs))
