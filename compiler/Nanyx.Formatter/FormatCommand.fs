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

let private commentsOf (parsed: SourceModule) =
    parsed.Trivia
    |> List.choose (function
        | LineComment(text, _, _) -> Some(text.TrimEnd())
        | BlankLines _ -> None)

/// Formats `text`, the source of the file at `path`, keeping its line endings: CRLF if its first line break is one,
/// otherwise LF. The result is parsed again and compared with the original, and only returned if it says the same
/// thing with the same comments.
let formatText (options: Formatter.Options) (path: string) (text: string) : Formatting =
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

/// Directories never searched for files: build output, packages and hidden directories.
let private isSkipped (directory: string) =
    let name = Path.GetFileName directory
    name = "bin" || name = "obj" || name = "node_modules" || name.StartsWith "."

let rec private filesUnder (directory: string) : string seq =
    seq {
        yield! Directory.EnumerateFiles(directory, "*.nyx") |> Seq.sort
        for subdirectory in Directory.EnumerateDirectories directory |> Seq.sort do
            if not (isSkipped subdirectory) then yield! filesUnder subdirectory
    }

/// The files `paths` stand for, relative to `currentDirectory`: a file is itself, a directory every `.nyx` file under
/// it, and no paths at all the current directory. A path that does not exist is an error naming it.
let findFiles (currentDirectory: string) (paths: string list) : Result<string list, string> =
    let paths = if List.isEmpty paths then [ "." ] else paths
    let resolve (path: string) = Path.GetFullPath(Path.Combine(currentDirectory, path))
    match paths |> List.filter (fun path -> not (File.Exists(resolve path) || Directory.Exists(resolve path))) with
    | missing :: _ -> Result.Error $"No such file or directory: {missing}"
    | [] ->
        paths
        |> List.collect (fun path ->
            let full = resolve path
            if Directory.Exists full then List.ofSeq (filesUnder full) else [ full ])
        |> List.distinct
        |> Result.Ok

let private utf8Bom = [| 0xEFuy; 0xBBuy; 0xBFuy |]

/// Formats each file with the options `settings` gives for it (see `Settings.cached`) and its `::formatter` directives
/// give (see `Settings.withDirectives`), rewriting the ones that change
/// in write mode (keeping a byte order mark if there was one), and leaving alone any whose settings are not valid, that
/// do not parse, or that the formatter cannot format safely.
let run (settings: string -> Result<Formatter.Options, string>) (mode: Mode) (files: string list) : (string * FileResult) list =
    files
    |> List.map (fun file ->
        let bytes = File.ReadAllBytes file
        let hasBom = bytes.Length >= 3 && bytes[0..2] = utf8Bom
        let text = Encoding.UTF8.GetString(bytes, (if hasBom then 3 else 0), bytes.Length - (if hasBom then 3 else 0))
        match settings file |> Result.bind (Settings.withDirectives file text) with
        | Result.Error message -> file, Failed(BadSettings message)
        | Result.Ok options ->
            match formatText options file text with
            | Formatted formatted when formatted = text -> file, Unchanged
            | Formatted formatted ->
                if mode = Write then File.WriteAllText(file, formatted, UTF8Encoding(hasBom))
                file, Changed
            | failure -> file, Failed failure)

/// 2 if any file could not be formatted, 1 in check mode if any file is not formatted, and 0 otherwise.
let exitCode (mode: Mode) (results: (string * FileResult) list) =
    if results |> List.exists (fun (_, result) -> match result with Failed _ -> true | _ -> false) then 2
    elif mode = Check && results |> List.exists (fun (_, result) -> result = Changed) then 1
    else 0
