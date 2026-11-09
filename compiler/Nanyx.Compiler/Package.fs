namespace NyxCompiler

open System
open System.IO
open System.Text.RegularExpressions
open Microsoft.Extensions.FileSystemGlobbing
open Parser.Program
open Tomlyn
open Tomlyn.Model

/// A package's `.nyxp` manifest, as far as it is read so far: what the package is called, which files are its
/// sources, and which linters check them. The `[formatter]` table is the formatter's, which reads it itself.
type Manifest =
    { /// The manifest's full path.
      Path: string
      /// The directory the manifest is in, which its `sources` are relative to.
      Root: string
      Name: string
      Version: string
      /// Globs relative to `Root`: `**/*.nyx` unless the manifest says otherwise.
      Sources: string list
      /// The linters the manifest turns on, each with a table under `[linters]`, and the severities it sets.
      Linters: LinterOptions }

module Manifest =
    /// The manifest's file extension.
    let extension = ".nyxp"

    /// The top-level tables a manifest may have. Those not read yet are refused when they say anything, rather
    /// than ignored, so a manifest never seems to work when it does not.
    let private tables = [ "package"; "dependencies"; "project-references"; "linters"; "formatter" ]

    let private packageKeys = [ "name"; "version"; "sources"; "public-modules" ]

    /// Lower-case dotted names, as packaging.md proposes: `acme.web`, `word_counter`.
    let internal namePattern = Regex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*$")

    /// What `namePattern` asks of a name, to finish "must be ...".
    let internal nameRule = "lower case, with words separated by dots: acme.web"

    let private versionPattern = Regex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$")

    /// The severities a rule can be given, by name, `none` turning it off.
    let severities =
        [ "error", Some Severity.Error
          "warning", Some Severity.Warning
          "info", Some Severity.Info
          "hint", Some Severity.Hint
          "none", None ]

    /// The manifest in `directory` or the nearest directory above it that has one, or an error if that directory
    /// has more than one.
    let rec tryFind (directory: string) : Result<string option, string> =
        match Directory.GetFiles(directory, "*" + extension) |> Array.sort with
        | [| manifest |] -> Result.Ok(Some manifest)
        | [||] ->
            match Path.GetDirectoryName directory with
            | null -> Result.Ok None
            | parent -> tryFind parent
        | several -> Result.Error $"""{directory} has more than one {extension} manifest: {several |> Array.map Path.GetFileName |> String.concat ", "}"""

    /// A TOML value as an error message names it.
    let describe (value: obj) =
        match value with
        | :? int64 as n -> string n
        | :? string as s -> $"the string \"{s}\""
        | :? float -> "a float"
        | :? bool -> "a boolean"
        | :? TomlTable -> "a table"
        | :? TomlArray -> "an array"
        | _ -> "a date or time"

    let private quoted (names: string seq) = names |> Seq.map (fun name -> $"'{name}'") |> String.concat ", "

    /// The linter options of a `[linters]` table: one table per linter to run, of rule ids and severities.
    let private readLinters (path: string) (table: TomlTable) : Result<LinterOptions, string list> =
        let configured =
            table
            |> Seq.map (fun entry ->
                match Linters.all |> List.tryFind (fun linter -> linter.Name = entry.Key), entry.Value with
                | None, _ ->
                    Result.Error [ $"{path}: unknown linter [linters.{entry.Key}]; the linters are {quoted (Linters.all |> List.map _.Name)}" ]
                | Some linter, (:? TomlTable as rules) ->
                    let known = linter.Rules @ [ Linters.failedRule ] |> List.map _.Id
                    let settings =
                        rules
                        |> Seq.map (fun rule ->
                            let key = $"[linters.{linter.Name}] {rule.Key}"
                            if not (List.contains rule.Key known) then
                                Result.Error $"{path}: unknown rule {key}; the rules are {quoted known}"
                            else
                                match rule.Value with
                                | :? string as name when severities |> List.exists (fst >> (=) name) ->
                                    Result.Ok(Linters.code linter rule.Key, severities |> List.find (fst >> (=) name) |> snd)
                                | value -> Result.Error $"{path}: {key} must be {quoted (List.map fst severities)}, not {describe value}")
                        |> List.ofSeq
                    match settings |> List.choose (function Result.Error e -> Some e | Result.Ok _ -> None) with
                    | [] -> Result.Ok(linter, settings |> List.choose (function Result.Ok s -> Some s | Result.Error _ -> None))
                    | errors -> Result.Error errors
                | Some linter, value ->
                    Result.Error [ $"{path}: linters.{linter.Name} must be a table, [linters.{linter.Name}], not {describe value}" ])
            |> List.ofSeq
        match configured |> List.collect (function Result.Error e -> e | Result.Ok _ -> []) with
        | [] ->
            let chosen = configured |> List.choose (function Result.Ok c -> Some c | Result.Error _ -> None)
            // In the order the compiler has them, so diagnostics come out in the same order whatever the manifest's.
            Result.Ok
                { Linters = Linters.all |> List.filter (fun linter -> chosen |> List.exists (fun (a, _) -> a.Name = linter.Name))
                  Severities = chosen |> List.collect snd |> Map.ofList }
        | errors -> Result.Error errors

    /// The manifest at `path`, or every problem with it, each naming the manifest and the key.
    let read (path: string) : Result<Manifest, string list> =
        let path = Path.GetFullPath path
        let document =
            try Result.Ok(TomlSerializer.Deserialize<TomlTable>(File.ReadAllText path, TomlSerializerOptions()))
            with :? TomlException as e -> Result.Error [ $"{path}: not valid TOML: {e.Message}" ]
        match document with
        | Result.Error errors -> Result.Error errors
        | Result.Ok document ->
            let errors = Collections.Generic.List<string>()
            let table (key: string) =
                match document.TryGetValue key with
                | true, (:? TomlTable as table) -> Some table
                | true, value ->
                    errors.Add $"{path}: {key} must be a table, [{key}], not {describe value}"
                    None
                | false, _ -> None
            for entry in document do
                if not (List.contains entry.Key tables) then
                    errors.Add $"{path}: unknown table [{entry.Key}]; the tables are {quoted tables}"
            for unsupported in [ "dependencies"; "project-references" ] do
                match table unsupported with
                | Some entries when entries.Count > 0 ->
                    errors.Add $"{path}: [{unsupported}] is not supported yet: a package can only use the standard library and its own files"
                | _ -> ()
            let package = table "package"
            if package.IsNone && not (document.ContainsKey "package") then
                errors.Add $"{path}: the manifest has no [package] table, which needs at least a name and a version"
            let package = package |> Option.defaultValue (TomlTable())
            for entry in package do
                if not (List.contains entry.Key packageKeys) then
                    errors.Add $"{path}: unknown key [package] {entry.Key}; the keys are {quoted packageKeys}"
            let text (key: string) (pattern: Regex) (rule: string) =
                match package.TryGetValue key with
                | true, (:? string as value) when pattern.IsMatch value -> value
                | true, (:? string as value) ->
                    errors.Add $"{path}: [package] {key} \"{value}\" must be {rule}"
                    ""
                | true, value ->
                    errors.Add $"{path}: [package] {key} must be a string, not {describe value}"
                    ""
                | false, _ ->
                    if document.ContainsKey "package" then errors.Add $"{path}: [package] has no {key}"
                    ""
            let name = text "name" namePattern nameRule
            let version = text "version" versionPattern "a semantic version: 1.2.0"
            let strings (key: string) (defaults: string list) =
                match package.TryGetValue key with
                | true, (:? TomlArray as values) when values |> Seq.forall (fun value -> value :? string) ->
                    values |> Seq.cast<string> |> List.ofSeq
                | true, value ->
                    errors.Add $"{path}: [package] {key} must be an array of strings, not {describe value}"
                    defaults
                | false, _ -> defaults
            let sources = strings "sources" [ "**/*.nyx" ]
            strings "public-modules" [] |> ignore
            // No [linters] table means no linters: a package turns on the official one with [linters.default].
            let linters =
                match table "linters" |> Option.map (readLinters path) with
                | Some (Result.Ok options) -> options
                | Some (Result.Error problems) ->
                    errors.AddRange problems
                    { Linters = []; Severities = Map.empty }
                | None -> { Linters = []; Severities = Map.empty }
            if errors.Count > 0 then
                Result.Error(List.ofSeq errors)
            else
                Result.Ok
                    { Path = path
                      Root = Path.GetDirectoryName path
                      Name = name
                      Version = version
                      Sources = sources
                      Linters = linters }

/// A package on disk: its manifest, its source files, and the one that declares its `@entry`, if any.
type Package =
    { Manifest: Manifest
      /// Full paths, in order.
      Sources: string list
      Entry: string option }

/// What building a package found: each source file's compile, and every diagnostic once, though a file imported by
/// another is checked as part of both.
type PackageBuild =
    { Package: Package
      Results: (string * CompileResult) list
      Diagnostics: Diagnostic list
      /// Whether every error in the package is a type or name error deferred to run time (see
      /// `CompileResult.ErrorsDeferred`), so a debug build can run its `@entry`. True when there are none.
      ErrorsDeferred: bool }

module Package =
    /// The files the manifest's `sources` select, as full paths in order.
    let sources (manifest: Manifest) : string list =
        let matcher = Matcher(StringComparison.OrdinalIgnoreCase)
        matcher.AddIncludePatterns manifest.Sources
        matcher.GetResultsInFullPath manifest.Root |> Seq.map Path.GetFullPath |> Seq.sort |> List.ofSeq

    /// Whether the module at `path` declares an `@entry`. A file that does not parse does not; building it reports why.
    let private declaresEntry (path: string) =
        match parseModuleFile path (File.ReadAllText path) with
        | Result.Ok items -> items |> List.exists (function Kind (Attribute name) -> name = Compiler.entryAttribute | _ -> false)
        | Result.Error _ -> false

    /// The package whose manifest is at `manifestPath`, or what is wrong with it.
    let ofManifest (manifestPath: string) : Result<Package, string list> =
        Manifest.read manifestPath
        |> Result.bind (fun manifest ->
            let files = sources manifest
            let display (file: string) = Path.GetRelativePath(manifest.Root, file)
            if files.IsEmpty then
                Result.Error [ $"""{manifest.Path}: no source files match {manifest.Sources |> List.map (sprintf "'%s'") |> String.concat ", "}""" ]
            else
                match files |> List.filter declaresEntry with
                | [] -> Result.Ok { Manifest = manifest; Sources = files; Entry = None }
                | [ entry ] -> Result.Ok { Manifest = manifest; Sources = files; Entry = Some entry }
                | several ->
                    Result.Error [ $"""{manifest.Path}: a package can have one @entry, but these files each declare one: {several |> List.map display |> String.concat ", "}""" ])

    /// The package `path` names: a manifest, or a directory in or under the package. An error says why there is none.
    let find (path: string) : Result<Package, string list> =
        let full = Path.GetFullPath path
        if File.Exists full && full.EndsWith(Manifest.extension, StringComparison.OrdinalIgnoreCase) then
            ofManifest full
        elif Directory.Exists full then
            match Manifest.tryFind full with
            | Result.Ok (Some manifest) -> ofManifest manifest
            | Result.Ok None -> Result.Error [ $"No {Manifest.extension} manifest in {full} or any directory above it" ]
            | Result.Error message -> Result.Error [ message ]
        else
            Result.Error [ $"Not a directory or a {Manifest.extension} manifest: {path}" ]

    /// The linter options for a file: those of the package manifest it is under, if it has one that reads.
    let lintersFor (filePath: string) : LinterOptions option =
        match Manifest.tryFind (Path.GetDirectoryName(Path.GetFullPath filePath)) with
        | Result.Ok (Some manifest) ->
            match Manifest.read manifest with
            | Result.Ok manifest -> Some manifest.Linters
            | Result.Error _ -> None
        | _ -> None

    /// An error for each `::` directive at the top of the file at `filePath`, whose text is `source`, which is in the
    /// package whose manifest is at `manifestPath`: directives say what a manifest would, for a script, which has none.
    let directiveErrors (manifestPath: string) (filePath: string) (source: string) : Diagnostic list =
        Directives.read (Path.GetFullPath filePath) source
        |> List.map (fun directive ->
            let name = if directive.Key.IsEmpty then "a :: line" else $"::{directive.Name}"
            Diagnostics.at
                DiagnosticCodes.DirectiveInPackage
                directive.Span
                $"{name} is a directive for a script, but this file is in a package: say it in the package's manifest, {manifestPath}")

    /// Checks every source file of `package`, running the linters its manifest turns on over each.
    let build (package: Package) : PackageBuild =
        let results =
            package.Sources
            |> List.map (fun file ->
                let source = File.ReadAllText file
                let result = Compiler.compileSourceWith package.Manifest.Linters file source
                let directives = directiveErrors package.Manifest.Path file source
                file,
                { result with
                    Diagnostics = directives @ result.Diagnostics
                    ErrorsDeferred = result.ErrorsDeferred && directives.IsEmpty })
        // A file's errors are deferred when it did not type-check and each of its errors was placed; a linter rule
        // configured as an error, in a file that did type-check, is not deferred.
        let deferred (result: CompileResult) =
            not (result.Diagnostics |> List.exists Diagnostics.isError) || (not result.TypeChecked && result.ErrorsDeferred)
        { Package = package
          Results = results
          Diagnostics = results |> List.collect (fun (_, result) -> result.Diagnostics) |> List.distinct
          ErrorsDeferred = results |> List.forall (snd >> deferred) }

    /// The compile of the package's `@entry` file, if it has one.
    let entryResult (build: PackageBuild) : CompileResult option =
        build.Package.Entry |> Option.bind (fun entry -> build.Results |> List.tryFind (fst >> (=) entry) |> Option.map snd)
