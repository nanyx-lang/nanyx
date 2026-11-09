/// The formatter's settings for a file: the `[formatter]` table of the package's `.nyxpack` manifest, the nearest one
/// in the file's directory or above it, or for a script its `::formatter` directives. Only that table is read; the
/// rest of the manifest is the packaging workstream's, whose reader will replace this one.
module Nanyx.Formatter.Settings

open System
open System.Collections.Generic
open System.IO
open Tomlyn
open Tomlyn.Model

/// The manifest in `directory` or the nearest directory above it that has one, or an error if that directory has more
/// than one.
let rec manifestIn (directory: string) : Result<string option, string> =
    match Directory.GetFiles(directory, "*.nyxpack") |> Array.sort with
    | [| manifest |] -> Result.Ok(Some manifest)
    | [||] ->
        match Path.GetDirectoryName directory with
        | null -> Result.Ok None
        | parent -> manifestIn parent
    | several -> Result.Error $"""{directory} has more than one .nyxpack manifest: {several |> Array.map Path.GetFileName |> String.concat ", "}"""

/// The options the `[formatter]` table of the manifest at `manifest` sets, the defaults for those it leaves out, or
/// an error naming the manifest and what is wrong.
let read (manifest: string) : Result<Formatter.Options, string> =
    let describe (value: obj) =
        match value with
        | :? int64 as n -> string n
        | :? string as s -> $"the string \"{s}\""
        | :? float -> "a float"
        | :? bool -> "a boolean"
        | :? TomlTable -> "a table"
        | :? TomlArray -> "an array"
        | _ -> "a date or time"
    let positive (key: string) (value: obj) =
        match value with
        | :? int64 as n when n > 0L && n <= int64 Int32.MaxValue -> Result.Ok(int n)
        | _ -> Result.Error $"{manifest}: [formatter] {key} must be a positive integer, not {describe value}"
    let table =
        try Result.Ok(TomlSerializer.Deserialize<TomlTable>(File.ReadAllText manifest, TomlSerializerOptions()))
        with :? TomlException as e -> Result.Error $"{manifest}: not valid TOML: {e.Message}"
    match table with
    | Result.Error message -> Result.Error message
    | Result.Ok table ->
        match table.TryGetValue "formatter" with
        | false, _ -> Result.Ok Formatter.Options.defaults
        | true, (:? TomlTable as settings) ->
            (Result.Ok Formatter.Options.defaults, settings)
            ||> Seq.fold (fun options setting ->
                options
                |> Result.bind (fun options ->
                    match setting.Key with
                    | "lineWidth" -> positive setting.Key setting.Value |> Result.map (fun n -> { options with LineWidth = n })
                    | "indentSize" -> positive setting.Key setting.Value |> Result.map (fun n -> { options with IndentSize = n })
                    | key -> Result.Error $"{manifest}: unknown setting [formatter] {key}; the settings are lineWidth and indentSize"))
        | true, value -> Result.Error $"{manifest}: formatter must be a table, [formatter], not {describe value}"

/// The options for the file at `path`: those of its manifest, or the defaults if it has none.
let forFile (path: string) : Result<Formatter.Options, string> =
    manifestIn (Path.GetDirectoryName(Path.GetFullPath path))
    |> Result.bind (function
        | Some manifest -> read manifest
        | None -> Result.Ok Formatter.Options.defaults)

/// `options` with the `::formatter` directives at the top of `text`, the source of the file at `path`, applied:
/// `::formatter lineWidth 100`. They are a script's `[formatter]` table, so a file in a package may not have any; the
/// other directives are the compiler's. An error names the file, the line and the directive.
let withDirectives (path: string) (text: string) (options: Formatter.Options) : Result<Formatter.Options, string> =
    let manifest =
        if path = "" then Result.Ok None else manifestIn (Path.GetDirectoryName(Path.GetFullPath path))
    let directives = Parser.Program.Api.Directives.read path text |> List.filter (fun directive -> directive.Name = "formatter")
    (Result.Ok options, directives)
    ||> List.fold (fun options directive ->
        options
        |> Result.bind (fun options ->
            let error message = Result.Error $"{path}:{directive.Span.StartLine + 1}:1: {message}"
            let positive (key: string) (value: string) =
                match Int32.TryParse value with
                | true, n when n > 0 -> Result.Ok n
                | _ -> error $"::formatter {key} must be a positive integer, not '{value}'"
            match manifest, directive.Arguments with
            | Result.Ok (Some manifest), _ -> error $"::formatter is for a script; in a package, set it in the [formatter] table of {manifest}"
            | _, [ "lineWidth" as key; value ] -> positive key value |> Result.map (fun n -> { options with LineWidth = n })
            | _, [ "indentSize" as key; value ] -> positive key value |> Result.map (fun n -> { options with IndentSize = n })
            | _, [ key; _ ] -> error $"unknown setting ::formatter {key}; the settings are lineWidth and indentSize"
            | _ -> error "::formatter takes a setting and its value: ::formatter lineWidth 100"))

/// `forFile`, reading each manifest once, for formatting many files.
let cached () : string -> Result<Formatter.Options, string> =
    let manifests = Dictionary<string, Result<Formatter.Options, string>>()
    fun path ->
        manifestIn (Path.GetDirectoryName(Path.GetFullPath path))
        |> Result.bind (function
            | Some manifest ->
                match manifests.TryGetValue manifest with
                | true, options -> options
                | false, _ ->
                    let options = read manifest
                    manifests[manifest] <- options
                    options
            | None -> Result.Ok Formatter.Options.defaults)
