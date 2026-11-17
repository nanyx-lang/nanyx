/// `nanyx format`: which files it formats, formatting each one safely, and what it comes to. The CLI only parses the
/// arguments and prints the results.
module Nanyx.Formatter.FormatCommand

open System
open System.IO
open System.Text
open Parser.Program

type Mode =
    /// Rewrite each file that is not formatted (the default).
    | Write
    /// Write nothing; report each file that is not formatted.
    | Check

/// What formatting a file's text comes to.
type Formatting =
    | Formatted of string
    /// It does not parse, so it is not formatted.
    | NotParsed of ParseError
    /// The formatted text would not say the same thing, or would lose a comment: a bug in the formatter, so the file
    /// is left alone.
    | NotSafe of string
    /// The formatter's settings for it, in its `.nyxp` manifest, are not valid: the message names the manifest
    /// and what is wrong. No file under that manifest is formatted.
    | BadSettings of string

/// What happened to one file.
type FileResult =
    | Unchanged
    /// It was not formatted: in write mode it has been rewritten, in check mode it would be.
    | Changed
    | Failed of Formatting

/// The comments of `parsed`: in order, except for those at the end of an import's line, which move with the import
/// when the formatter sorts imports, and so are compared in sorted order. A doc comment is taken laid out as the
/// formatter lays it out, since the formatter moves its continuation lines to their own indent.
let commentsOf (parsed: SourceModule) =
    let text = parsed.Source
    let rec importEnds (items: TopLevelItem list) =
        items
        |> List.collect (fun i ->
            match i.Kind with
            | Import imports -> imports |> List.map (fun item -> item.Span.End)
            | ModuleBlock(_, items) -> importEnds items
            | _ -> [])
    let ends = importEnds parsed.Items
    let onImportLine (span: SourceSpan) =
        ends |> List.exists (fun e -> 0 <= e && e <= span.Start && text.IndexOf('\n', e, span.Start - e) < 0)
    let moved, kept =
        parsed.Trivia
        |> List.choose (function
            | DocLineComment(comment, span) -> Some(DocComments.relaidOut 0 comment, span)
            | LineComment(comment, _, span) -> Some(comment.TrimEnd(), span)
            | BlankLines _ -> None)
        |> List.partition (snd >> onImportLine)
    List.map fst kept, List.sort (List.map fst moved)

let private formatTextOnThisThread (options: Formatter.Options) (path: string) (text: string) : Formatting =
    match parseModuleWithTrivia path text with
    | Result.Error error -> NotParsed error
    | Result.Ok parsed ->
        let formatted = Formatter.format options parsed
        match parseModuleWithTrivia path formatted with
        | Result.Error error -> NotSafe $"the formatted code does not parse: {error.Message}"
        | Result.Ok again when not (Syntax.same parsed.Items again.Items) -> NotSafe "formatting would change what the code says"
        | Result.Ok again when commentsOf again <> commentsOf parsed -> NotSafe "formatting would lose or reorder a comment"
        | Result.Ok _ ->
            let firstBreak = text.IndexOf '\n'
            if firstBreak > 0 && text[firstBreak - 1] = '\r' then Formatted(formatted.Replace("\n", "\r\n")) else Formatted formatted

/// Formats `text`, the source of the file at `path`, keeping its line endings: CRLF if its first line break is one,
/// otherwise LF. The result is parsed again and compared with the original, and only returned if it says the same
/// thing with the same comments. It runs on a large stack: formatting and the comparison recurse once per level of the
/// code, and a long operator chain is thousands of levels deep.
let formatText (options: Formatter.Options) (path: string) (text: string) : Formatting =
    Nanyx.LargeStack.run (fun () -> formatTextOnThisThread options path text)

/// The files `paths` stand for, relative to `currentDirectory`: a file is itself, a directory every `.nyx` file under
/// it (see `SourceFiles.under`), and no paths at all the current directory. A path that does not exist is an error
/// naming it.
let findFiles (currentDirectory: string) (paths: string list) : Result<string list, string> =
    let paths = if List.isEmpty paths then [ "." ] else paths
    let resolve (path: string) = Path.GetFullPath(Path.Combine(currentDirectory, path))
    match paths |> List.filter (fun path -> not (File.Exists(resolve path) || Directory.Exists(resolve path))) with
    | missing :: _ -> Result.Error $"There is no file or directory '{missing}'"
    | [] ->
        paths
        |> List.collect (fun path ->
            let full = resolve path
            if Directory.Exists full then List.ofSeq (SourceFiles.under full) else [ full ])
        |> List.distinct
        |> Result.Ok

let private utf8Bom = [| 0xEFuy; 0xBBuy; 0xBFuy |]

/// The text of a file's bytes, read as UTF-8, and whether they start with a byte order mark.
let private decode (bytes: byte array) =
    let hasBom = bytes.Length >= 3 && bytes[0..2] = utf8Bom
    Encoding.UTF8.GetString(bytes, (if hasBom then 3 else 0), bytes.Length - (if hasBom then 3 else 0)), hasBom

/// Formats `text`, the source of the file at `file`, with the options `settings` gives for it (see `Settings.cached`)
/// and its `::format` directives give (see `Settings.withDirectives`).
let private formatWith (settings: string -> Result<Formatter.Options, string>) (file: string) (text: string) : Formatting =
    match settings file |> Result.bind (Settings.withDirectives file text) with
    | Result.Error message -> BadSettings message
    | Result.Ok options -> formatText options file text

/// Formats each file with the options `settings` gives for it (see `Settings.cached`) and its `::format` directives
/// give (see `Settings.withDirectives`), rewriting the ones that change
/// in write mode (keeping a byte order mark if there was one), and leaving alone any whose settings are not valid, that
/// do not parse, or that the formatter cannot format safely.
let run (settings: string -> Result<Formatter.Options, string>) (mode: Mode) (files: string list) : (string * FileResult) list =
    files
    |> List.map (fun file ->
        let text, hasBom = decode (File.ReadAllBytes file)
        match formatWith settings file text with
        | Formatted formatted when formatted = text -> file, Unchanged
        | Formatted formatted ->
            if mode = Write then File.WriteAllText(file, formatted, UTF8Encoding(hasBom))
            file, Changed
        | failure -> file, Failed failure)

/// What messages call standard input, as if it were a file of that name in the current directory.
let inputName = "<stdin>"

/// `nanyx format -`, for an editor to format a buffer without writing it to a file: formats the text read from `input`
/// (standard input) as a file named `inputName` in `directory` (the current one), with the settings of a file there, and
/// its own `::format` directives. In write mode it writes the result to `output` (standard output): the text as it
/// was if it is formatted already, with the byte order mark it had, and nothing if it could not be formatted. Check
/// mode writes nothing.
let runInput (settings: string -> Result<Formatter.Options, string>) (mode: Mode) (directory: string) (input: Stream) (output: Stream) : string * FileResult =
    let file = Path.Combine(directory, inputName)
    use read = new MemoryStream()
    input.CopyTo read
    let text, hasBom = decode (read.ToArray())
    let write (text: string) =
        if mode = Write then
            let encoding = UTF8Encoding(hasBom)
            let bytes = Array.append (encoding.GetPreamble()) (encoding.GetBytes text)
            output.Write(bytes, 0, bytes.Length)
            output.Flush()
    match formatWith settings file text with
    | Formatted formatted when formatted = text ->
        write text
        file, Unchanged
    | Formatted formatted ->
        write formatted
        file, Changed
    | failure -> file, Failed failure

/// 2 if any file (or standard input) could not be formatted, 1 in check mode if any is not formatted, and 0 otherwise.
let exitCode (mode: Mode) (results: (string * FileResult) list) =
    if results |> List.exists (fun (_, result) -> match result with Failed _ -> true | _ -> false) then 2
    elif mode = Check && results |> List.exists (fun (_, result) -> result = Changed) then 1
    else 0
