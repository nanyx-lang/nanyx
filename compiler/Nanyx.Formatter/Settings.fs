/// The formatter's settings for a file: the `formatter` field of the package's `.nyxp` manifest, the nearest one in
/// the file's directory or above it, or for a script its `::format` directives. Only that field is read; the rest of
/// the manifest is the compiler's.
module Nanyx.Formatter.Settings

open System
open System.Collections.Generic
open System.IO
open Parser.Program.DataValues

/// The manifest in `directory` or the nearest directory above it that has one, or an error if that directory has more
/// than one.
let rec manifestIn (directory: string) : Result<string option, string> =
    match Directory.GetFiles(directory, "*.nyxp") |> Array.sort with
    | [| manifest |] -> Result.Ok(Some manifest)
    | [||] ->
        match Path.GetDirectoryName directory with
        | null -> Result.Ok None
        | parent -> manifestIn parent
    | several -> Result.Error $"""'{directory}' has more than one .nyxp manifest: {several |> Array.map (Path.GetFileName >> sprintf "'%s'") |> String.concat ", "}"""

/// `path:line:column`, from 1, of where `span` starts.
let private location (span: Parser.Program.SourceSpan) = $"{span.File}:{span.StartLine + 1}:{span.StartColumn + 1}"

/// The options the `formatter` field of the manifest at `manifest` sets, `formatter = (lineWidth = 100)`, the defaults
/// for those it leaves out, or an error naming the manifest and what is wrong. The rest of the manifest is the
/// compiler's, which reports what is wrong with it; here a manifest that is not data is only an error.
let read (manifest: string) : Result<Formatter.Options, string> =
    let positive (field: DataField) =
        match field.Value.Data with
        | DataInt n when n > 0I && n <= bigint Int32.MaxValue -> Result.Ok(int n)
        | _ -> Result.Error $"{location field.Value.Location}: `formatter`'s `{field.Name}` must be a positive integer, not {DataValue.describe field.Value}"
    match DataValue.read manifest (File.ReadAllText manifest) with
    | Result.Error _ -> Result.Error $"{manifest}: This is not a manifest, since it holds code or is not Nanyx: run `nanyx check` on it to see why"
    | Result.Ok { Data = DataRecord fields } ->
        match DataValue.field "formatter" fields with
        | None -> Result.Ok Formatter.Options.defaults
        | Some { Value = { Data = DataRecord settings } } ->
            (Result.Ok Formatter.Options.defaults, settings)
            ||> List.fold (fun options setting ->
                options
                |> Result.bind (fun options ->
                    match setting.Name with
                    | "lineWidth" -> positive setting |> Result.map (fun n -> { options with LineWidth = n })
                    | "indentSize" -> positive setting |> Result.map (fun n -> { options with IndentSize = n })
                    | name -> Result.Error $"{location setting.NameSpan}: `formatter` has no setting '{name}': the settings are 'lineWidth' and 'indentSize'"))
        | Some field -> Result.Error $"{location field.Value.Location}: `formatter` must be a record, `(lineWidth = 100)`, not {DataValue.describe field.Value}"
    | Result.Ok _ -> Result.Ok Formatter.Options.defaults

/// The options for the file at `path`: those of its manifest, or the defaults if it has none.
let forFile (path: string) : Result<Formatter.Options, string> =
    manifestIn (Path.GetDirectoryName(Path.GetFullPath path))
    |> Result.bind (function
        | Some manifest -> read manifest
        | None -> Result.Ok Formatter.Options.defaults)

/// `options` with the `::format` directives at the top of `text`, the source of the file at `path`, applied:
/// `::format lineWidth 100`. They are a script's `formatter` field, so a file in a package may not have any; the
/// other directives are the compiler's. An error names the file, the line and the directive.
let withDirectives (path: string) (text: string) (options: Formatter.Options) : Result<Formatter.Options, string> =
    let manifest =
        if path = "" then Result.Ok None else manifestIn (Path.GetDirectoryName(Path.GetFullPath path))
    let directives =
        Parser.Program.Header.Directives.read path text |> List.filter (fun directive -> directive.Name = "format")
    (Result.Ok options, directives)
    ||> List.fold (fun options directive ->
        options
        |> Result.bind (fun options ->
            let error message = Result.Error $"{path}:{directive.Span.StartLine + 1}:1: {message}"
            let positive (key: string) (word: Parser.Program.Header.DirectiveWord) =
                match DataValue.ofWord word with
                | Some { Data = DataInt n } when n > 0I && n <= bigint Int32.MaxValue -> Result.Ok(int n)
                | Some value -> error $"`::format {key}` must be a positive integer, not {DataValue.describe value}"
                | None -> error $"`::format {key}` must be a positive integer, not '{word.Text}'"
            match manifest, directive.Arguments |> List.map (fun word -> word.Text, word) with
            | Result.Ok (Some manifest), _ -> error $"`::format` is for a script, and this file is in a package: set it in the `formatter` field of '{manifest}'"
            | _, [ "lineWidth" as key, _; _, value ] -> positive key value |> Result.map (fun n -> { options with LineWidth = n })
            | _, [ "indentSize" as key, _; _, value ] -> positive key value |> Result.map (fun n -> { options with IndentSize = n })
            | _, [ key, _; _ ] -> error $"Unknown setting `::format {key}`: the settings are 'lineWidth' and 'indentSize'"
            | _ -> error "`::format` takes a setting and its value: write `::format lineWidth 100`"))

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
