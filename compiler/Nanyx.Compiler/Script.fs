namespace NyxCompiler

open System.IO
open Parser.Program
open Parser.Program.DataValues

/// What a script's `::` directives say: what a package's manifest would, for a file run on its own, which has none.
type ScriptDirectives =
    { /// The linters the `::linters` directives turn on and the severities they set, as a manifest's
      /// `linters` would. None when there is no `::linters` directive: then no linter runs, as for a package whose
      /// manifest has no `linters`.
      Linters: LinterOptions option
      /// The build the `::host` and `::dependencies` directives make: the host the script runs under and the packages
      /// it imports from (see `Package.scriptModules`). None when there are neither, or they are wrong: then the script
      /// runs under the console host and imports only the standard library and files.
      Build: PackageModules option
      /// What is wrong with the directives, each at its line.
      Diagnostics: Diagnostic list }

module Script =
    /// The manifest tables a directive can stand for. `::formatter` is the formatter's, which reads it itself.
    let private tables = [ "linters"; "formatter"; "host"; "dependencies" ]

    let private quoted (names: string seq) = names |> Seq.map (fun name -> $"'{name}'") |> String.concat ", "

    let private tableList = quoted (tables |> List.map ((+) "::"))

    /// A directive's value, a TOML value, as an error message names it.
    let private describe (value: obj) =
        match value with
        | :? int64 as n -> string n
        | :? string as s -> $"the string \"{s}\""
        | :? float -> "a float"
        | :? bool -> "a boolean"
        | :? Tomlyn.Model.TomlTable -> "a table"
        | :? Tomlyn.Model.TomlArray -> "an array"
        | _ -> "a date or time"


    /// What a `::linters` directive says: a linter to run, and the severity it sets for one of its rules, if any.
    let private linterSetting (directive: Directive) : Result<Linter * (string * Severity option) option, string> =
        let linter (name: string) =
            match Linters.all |> List.tryFind (fun linter -> linter.Name = name) with
            | Some linter -> Result.Ok linter
            | None -> Result.Error $"unknown linter '{name}'; the linters are {quoted (Linters.all |> List.map _.Name)}"
        match directive.Key, directive.Value with
        | [ _; name ], None -> linter name |> Result.map (fun linter -> linter, None)
        | [ _; name ], Some _ ->
            Result.Error $"::linters.{name} is a table, so it takes no value; set one of its rules with ::linters.{name}.<rule> = \"error\""
        | [ _; name; ruleId ], Some value ->
            linter name
            |> Result.bind (fun linter ->
                let known = linter.Rules @ [ Linters.failedRule ] |> List.map _.Id
                if not (List.contains ruleId known) then
                    Result.Error $"unknown rule '{ruleId}' of the linter '{linter.Name}'; the rules are {quoted known}"
                else
                    value
                    |> Result.bind (fun severity ->
                        match severity with
                        | :? string as name when Manifest.severities |> List.exists (fst >> (=) name) ->
                            Result.Ok(linter, Some(Linters.code linter ruleId, Manifest.severities |> List.find (fst >> (=) name) |> snd))
                        | other ->
                            Result.Error
                                $"::{directive.Name} must be {quoted (List.map fst Manifest.severities)}, not {describe other}"))
        | [ _; _; _ ], None ->
            Result.Error $"::{directive.Name} needs a severity: ::{directive.Name} = \"error\""
        | _ ->
            Result.Error "::linters takes a linter, or one of its rules and a severity: ::linters.default, or ::linters.default.unusedBindings = \"error\""

    /// A directive's TOML value as the manifest would write it, as data written at `span`: a table is a record.
    let rec private dataOf (span: SourceSpan) (value: obj) : DataValue =
        let data =
            match value with
            | :? string as s -> DataString s
            | :? int64 as n -> DataInt(bigint n)
            | :? float as n -> DataFloat n
            | :? bool as b -> DataBool b
            | :? Tomlyn.Model.TomlTable as table -> DataRecord [ for KeyValue(name, field) in table -> { Name = name; NameSpan = span; Value = dataOf span field } ]
            | :? Tomlyn.Model.TomlArray as items -> DataList [ for item in items -> dataOf span item ]
            | other -> DataString(string other)
        { Data = data; Location = span }

    /// The entry a `::host` or `::dependencies` directive stands for, as the manifest's `host`, or an entry of its
    /// `dependencies`, would write it: `::dependencies.acme.json = { path = "../json" }` is
    /// `(package = "acme.json", path = "../json")`, and `::host.nanyx.console = "0.28.0"` is
    /// `(package = "nanyx.console", version = "0.28.0")`.
    let private packageEntry (directive: Directive) (name: string list) (value: obj) : DataValue =
        let field fieldName value = { Name = fieldName; NameSpan = directive.Span; Value = value }
        let package = field "package" { Data = DataString(String.concat "." name); Location = directive.Span }
        let fields =
            match dataOf directive.Span value with
            | { Data = DataRecord fields } -> fields
            | version -> [ field "version" version ]
        { Data = DataRecord(package :: fields); Location = directive.Span }

    /// What a directive says: a linter setting, the script's host, or a package it depends on.
    type private Setting =
        | LinterSetting of Linter * (string * Severity option) option
        | HostSetting of DataValue
        | DependencySetting of DataValue

    /// The build `settings` make, if they name a host or dependencies, for the script at `filePath`, whose imports by
    /// path, and the directories its directives give, are relative to its directory; or what is wrong with them.
    let private build (filePath: string) (directives: (Directive * Setting) list) : Result<PackageModules option, Diagnostic list> =
        let root = if filePath = "" then Directory.GetCurrentDirectory() else Path.GetDirectoryName filePath
        let hosts = directives |> List.choose (function directive, HostSetting entry -> Some (directive, entry) | _ -> None)
        let dependencies = directives |> List.choose (function directive, DependencySetting entry -> Some (directive, entry) | _ -> None)
        match hosts, dependencies with
        | [], [] -> Result.Ok None
        | (first, _) :: _, _
        | [], (first, _) :: _ ->
            let twice =
                hosts
                |> List.skip (min 1 hosts.Length)
                |> List.map (fun (directive, _) -> Diagnostics.at DiagnosticCodes.InvalidDirective directive.Span "a script runs under one host, which one ::host names")
            let host, hostErrors =
                match hosts with
                | (_, entry) :: _ ->
                    match Manifest.hostEntry root entry with
                    | Result.Ok host -> Some host, []
                    | Result.Error errors -> None, errors
                | [] -> None, []
            let dependencies, dependencyErrors =
                Manifest.dependencyEntries root { Data = DataList(dependencies |> List.map snd); Location = first.Span }
            match twice @ hostErrors @ dependencyErrors with
            | [] -> Package.scriptModules filePath first.Span host dependencies |> Result.map Some
            | errors -> Result.Error errors

    /// The directives at the top of `source`, the text of the file at `filePath` (empty for one not on disk). In a
    /// file of a package each is an error, since the package's manifest says these things, and they give no linters.
    let read (filePath: string) (source: string) : ScriptDirectives =
        let filePath = if filePath = "" then "" else Path.GetFullPath filePath
        let directives = Directives.read filePath source
        let manifest =
            match filePath with
            | "" -> None
            | _ when not (Directory.Exists(Path.GetDirectoryName filePath)) -> None
            | _ -> Manifest.tryFind (Path.GetDirectoryName filePath) |> Result.defaultValue None
        match manifest with
        | _ when directives.IsEmpty -> { Linters = None; Build = None; Diagnostics = [] }
        | Some manifest -> { Linters = None; Build = None; Diagnostics = Package.directiveErrors manifest filePath source }
        | None ->
            let invalid (directive: Directive) message = Result.Error(Diagnostics.at DiagnosticCodes.InvalidDirective directive.Span message)
            let packageSetting (directive: Directive) (setting: DataValue -> Setting) (usage: string) =
                match directive.Key, directive.Value with
                | _ :: (_ :: _ as name), Some (Result.Ok value) -> Result.Ok(Some(setting (packageEntry directive name value)))
                | _ :: _ :: _, Some (Result.Error message) -> invalid directive message
                | _ -> invalid directive usage
            let read (directive: Directive) =
                match directive.Key, directive.Value with
                | "linters" :: _, _ ->
                    linterSetting directive
                    |> Result.map (LinterSetting >> Some)
                    |> Result.mapError (Diagnostics.at DiagnosticCodes.InvalidDirective directive.Span)
                | "formatter" :: _, _ -> Result.Ok None
                | "host" :: _, _ ->
                    packageSetting
                        directive
                        HostSetting
                        $"::host takes a host and its version, as a manifest's host does: ::host.{Hosts.consolePackage} = \"{Toolchain.version}\", or ::host.acme.server = {{ path = \"../server\" }}"
                | "dependencies" :: _, _ ->
                    packageSetting
                        directive
                        DependencySetting
                        $"::dependencies takes a package and where it is, as a manifest's dependencies does: ::dependencies.acme.json = {{ path = \"../json\" }}, or ::dependencies.nanyx.console.core = \"{Toolchain.version}\""
                | [], _ ->
                    invalid
                        directive
                        $"a directive is a manifest's dotted key after '::', with '= value' to set a value: ::linters.default, or ::formatter.lineWidth = 80; the tables are {tableList}"
                | table :: _, _ -> invalid directive $"unknown directive ::{table}; the tables are {tableList}"
            let results = directives |> List.map (fun directive -> directive, read directive)
            let settings = results |> List.choose (function directive, Result.Ok (Some setting) -> Some (directive, setting) | _ -> None)
            let linterSettings = settings |> List.choose (function _, LinterSetting (linter, severity) -> Some (linter, severity) | _ -> None)
            let build, buildErrors =
                match build filePath settings with
                | Result.Ok build -> build, []
                | Result.Error errors -> None, errors
            { Linters =
                if linterSettings.IsEmpty then
                    None
                else
                    // In the order the compiler has them, as for a manifest.
                    Some
                        { Linters = Linters.all |> List.filter (fun linter -> linterSettings |> List.exists (fun (a, _) -> a.Name = linter.Name))
                          Severities = linterSettings |> List.choose snd |> Map.ofList }
              Build = build
              Diagnostics = (results |> List.choose (function _, Result.Error diagnostic -> Some diagnostic | _, Result.Ok _ -> None)) @ buildErrors }

    /// `result` with the problems with `directives` before its own diagnostics. An error in a directive is not
    /// deferred: it stops a debug run too.
    let withDirectives (directives: ScriptDirectives) (result: CompileResult) : CompileResult =
        { result with
            Diagnostics = directives.Diagnostics @ result.Diagnostics
            ErrorsDeferred = result.ErrorsDeferred && not (directives.Diagnostics |> List.exists Diagnostics.isError) }

    /// Compiles `source` as the file at `filePath` with the linters `options` gives, in the build `directives` make, if
    /// they make one: under the host its `::host` names, importing from the packages its `::dependencies` list. Otherwise
    /// it is compiled as `Package.compileSourceReading` compiles it. Each module's check is reported to `listener`, and
    /// each other file it needs is read by `readFile`.
    let compileSourceReading
        (listener: CompileListener)
        (readFile: string -> string)
        (directives: ScriptDirectives)
        (options: LinterOptions)
        (filePath: string)
        (source: string)
        : CompileResult =
        match directives.Build with
        | Some build -> Compiler.compileSourceInReporting listener readFile build options (Path.GetFullPath filePath) source
        | None -> Package.compileSourceReading listener readFile options filePath source

    /// Compiles `source` as the script at `filePath`, with the linters its directives turn on (none if they turn none
    /// on), under the host and with the dependencies they name, as `nanyx run` does. A source file of a package is
    /// compiled as one (see `Package.compileSourceWith`). Each module's check is reported to `listener`.
    let compileSourceReporting (listener: CompileListener) (filePath: string) (source: string) : CompileResult =
        let directives = read filePath source
        compileSourceReading listener File.ReadAllText directives (directives.Linters |> Option.defaultValue Linters.none) filePath source
        |> withDirectives directives

    /// `compileSourceReporting`, telling nobody.
    let compileSource (filePath: string) (source: string) : CompileResult =
        compileSourceReporting CompileListener.none filePath source

    /// `compileSourceReporting` for the file at `filePath` as it is on disk.
    let compileFileReporting (listener: CompileListener) (filePath: string) : CompileResult =
        if File.Exists filePath then compileSourceReporting listener filePath (File.ReadAllText filePath) else Compiler.compileFile filePath

    /// `compileFileReporting`, telling nobody.
    let compileFile (filePath: string) : CompileResult = compileFileReporting CompileListener.none filePath
