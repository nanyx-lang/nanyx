namespace NyxCompiler

open System
open System.IO
open System.Text.RegularExpressions
open Parser.Program
open Parser.Program.DataValues

/// A package another depends on, in a directory on disk: `(package = "acme.util", path = "../util")` in `dependencies`.
type Dependency =
    { /// The package's name, which its own manifest must give it too.
      Name: string
      /// The full path of the directory its manifest is in.
      Directory: string
      /// Where its `package` is written.
      NameSpan: SourceSpan
      /// Where its `path` is written.
      PathSpan: SourceSpan }

/// The host an application's manifest names in its `host` field: a package the compiler ships, at its version, or one
/// found by its path, `host = (package = "acme.server", path = "../server")`.
type HostEntry =
    { /// The host's package.
      Package: string
      /// The full path of the host package's directory, for a host found by its `path`; none for one the compiler ships.
      Path: string option
      /// Where the entry is written, which an error about the host it names is reported at.
      Span: SourceSpan }

/// A package's `.nyxp` manifest, as far as it is read so far: what the package is called, which files are its
/// sources, which of its modules other packages may import, what it depends on, which linters check it, and which host
/// runs its `@entry`. Its `formatter` field is the formatter's, which reads it itself.
type Manifest =
    { /// The manifest's full path.
      Path: string
      /// The directory the manifest is in, which its `sources` are relative to.
      Root: string
      Name: string
      Version: string
      /// Globs relative to `Root`: `**/*.nyx` unless the manifest says otherwise.
      Sources: string list
      /// The names of the modules a package that depends on this one may import, `package`'s `publicModules`, each
      /// with where it is written.
      PublicModules: (string * SourceSpan) list
      /// The packages it depends on, in the order `dependencies` lists them.
      Dependencies: Dependency list
      /// The linters the manifest's `linters` turns on, and the severities their `settings` set.
      Linters: LinterOptions
      /// The host `host` names, if the manifest has one: a package with none is a library, which has no `@entry`.
      Host: HostEntry option
      /// The host this package is, if its `package` declares one (see `HostPackages`).
      Declares: Host option
      /// The whole manifest as it is written, for tools that read the rest (see `toJson`).
      Value: DataValue }

/// Reading a manifest: one Nanyx record, written as data (see `DataValue.read`), whose shape is the manifest's own.
module Manifest =
    /// The manifest's file extension.
    let extension = ".nyxp"

    /// The fields a manifest may have. `formatter` is the formatter's, which checks what is in it.
    let private fields = [ "package"; "dependencies"; "linters"; "host"; "formatter" ]

    let private packageFields = [ "name"; "version"; "sources"; "publicModules"; HostPackages.field ]

    /// The fields of an entry of `linters`.
    let private entryFields = [ "package"; "version"; "settings" ]

    /// The fields of the `host` entry.
    let private hostFields = [ "package"; "version"; "path"; "settings" ]

    /// Lower-case dotted names, as packaging.md proposes: `acme.web`, `word_counter`.
    let internal namePattern = Regex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*$")

    /// What `namePattern` asks of a name, to finish "must be ...".
    let internal nameRule = "lower case, with words separated by dots: acme.web"

    let private versionPattern = Regex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$")

    /// The severities a rule can be given, by name, `off` turning it off. A manifest writes them as tags, `#error`.
    let severities =
        [ "error", Some Severity.Error
          "warning", Some Severity.Warning
          "info", Some Severity.Info
          "hint", Some Severity.Hint
          "off", None ]

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

    let private quoted (names: string seq) = names |> Seq.map (fun name -> $"'{name}'") |> String.concat ", "

    /// Collects the problems with a manifest as its fields are read.
    type private Problems() =
        let found = Collections.Generic.List<Diagnostic>()
        member _.All = List.ofSeq found
        member _.Add (code: DiagnosticCode) (span: SourceSpan) (message: string) = found.Add(Diagnostics.at code span message)

        /// The fields of `value`, `what` in messages, if it is a record; each one not in `known` is an error.
        member this.Record (what: string) (known: string list) (value: DataValue) : DataField list option =
            match value.Data with
            | DataRecord fields ->
                for field in fields do
                    if not (List.contains field.Name known) then
                        this.Add DiagnosticCodes.UnknownManifestField field.NameSpan $"{what} has no field '{field.Name}'; its fields are {quoted known}"
                Some fields
            | _ ->
                this.Add DiagnosticCodes.InvalidManifestValue value.Location $"{what} must be a record, (name = value, ...), not {DataValue.describe value}"
                None

        /// The string in the field `name` of `fields`, `what` in messages, if it has one; one that does not match
        /// `pattern` is an error that says it must be `rule`.
        member this.Text (what: string) (fields: DataField list) (name: string) (pattern: Regex) (rule: string) : string option =
            match DataValue.field name fields with
            | Some { DataField.Value = { DataValue.Data = DataString text } } when pattern.IsMatch text -> Some text
            | Some { DataField.Value = { DataValue.Data = DataString text } as value } ->
                this.Add DiagnosticCodes.InvalidManifestValue value.Location $"""{what}'s {name} "{text}" must be {rule}"""
                None
            | Some field ->
                this.Add DiagnosticCodes.InvalidManifestValue field.Value.Location $"{what}'s {name} must be a string, not {DataValue.describe field.Value}"
                None
            | None -> None

        /// `Text`, for a field that must be there: its absence is an error at `at`.
        member this.RequiredText (what: string) (at: SourceSpan) (fields: DataField list) (name: string) (pattern: Regex) (rule: string) =
            if (DataValue.field name fields).IsNone then
                this.Add DiagnosticCodes.MissingManifestField at $"{what} has no {name}, which it needs"
            this.Text what fields name pattern rule

        /// The strings of the list in the field `name` of `fields`, each with where it is written, if it has one.
        member this.Strings (what: string) (fields: DataField list) (name: string) : (string * SourceSpan) list option =
            match DataValue.field name fields with
            | Some { DataField.Value = { DataValue.Data = DataList items } } when items |> List.forall (fun item -> item.Data.IsDataString) ->
                Some(items |> List.choose (fun item -> match item.Data with DataString text -> Some(text, item.Location) | _ -> None))
            | Some field ->
                this.Add DiagnosticCodes.InvalidManifestValue field.Value.Location $"{what}'s {name} must be a list of strings, not {DataValue.describe field.Value}"
                None
            | None -> None

        /// An error at each of `entries` whose name an earlier one has already, which `message` describes given the name.
        member this.Repeats (entries: (string * SourceSpan) list) (message: string -> string) =
            let seen = Collections.Generic.HashSet<string>()
            for name, span in entries do
                if not (seen.Add name) then
                    this.Add DiagnosticCodes.InvalidManifestValue span (message name)

    let private anyText = Regex ".*"

    /// The entry `value` of `linters`, or `host`, as `what`: the package it names, which `known` finds, if the entry
    /// is right. The linters and hosts are the ones the compiler ships, so the version must be the compiler's own. Its `settings`, if any, are left to `settings`.
    let private entry (problems: Problems) (what: string) (known: string -> Result<'a, string>) (value: DataValue) : (DataField list * 'a) option =
        problems.Record what entryFields value
        |> Option.bind (fun fields ->
            let version = problems.RequiredText what value.Location fields "version" versionPattern "a semantic version: 1.2.0"
            match problems.RequiredText what value.Location fields "package" anyText "" with
            | Some name ->
                match known name with
                | Result.Ok found ->
                    match version with
                    | Some version when version <> Toolchain.version ->
                        problems.Add
                            DiagnosticCodes.VersionNotShipped
                            (DataValue.field "version" fields).Value.Value.Location
                            $"the compiler ships {name} {Toolchain.version}, not {version}: a package the compiler ships is named at its version exactly"
                    | _ -> ()
                    Some(fields, found)
                | Result.Error message ->
                    problems.Add DiagnosticCodes.InvalidManifestValue (DataValue.field "package" fields).Value.Value.Location message
                    None
            | None -> None)

    /// The severities as a manifest writes them: `#error`, `#warning`, ...
    let private severityTags = severities |> List.map (fst >> sprintf "#%s") |> String.concat ", "

    /// The linter options of the manifest's `linters`: one entry per linter to run, whose `settings` give its rules'
    /// severities, `settings = (rules = (unusedBindings = #error))`.
    let private readLinters (problems: Problems) (value: DataValue) : LinterOptions =
        let linter (name: string) =
            match Linters.all |> List.tryFind (fun linter -> linter.Package = name) with
            | Some linter -> Result.Ok linter
            | None -> Result.Error $"there is no linter '{name}'; the linters are {quoted (Linters.all |> List.map _.Package)}"
        let rules (linter: Linter) (settings: DataValue) =
            let what = $"the settings of {linter.Package}"
            problems.Record what [ "rules" ] settings
            |> Option.bind (DataValue.field "rules")
            |> Option.bind (fun field -> problems.Record $"the rules of {linter.Package}" (linter.Rules @ [ Linters.failedRule ] |> List.map _.Id) field.Value)
            |> Option.defaultValue []
            |> List.choose (fun rule ->
                match rule.Value.Data with
                | _ when not (linter.Rules @ [ Linters.failedRule ] |> List.exists (fun known -> known.Id = rule.Name)) -> None
                | DataTag(name, None) when severities |> List.exists (fst >> (=) name) ->
                    Some(Linters.code linter rule.Name, severities |> List.find (fst >> (=) name) |> snd)
                | _ ->
                    problems.Add
                        DiagnosticCodes.InvalidManifestValue
                        rule.Value.Location
                        $"the rule {rule.Name} must be {severityTags}, not {DataValue.describe rule.Value}"
                    None)
        let chosen =
            match value.Data with
            | DataList entries ->
                entries
                |> List.choose (fun value ->
                    entry problems "a linter" linter value
                    |> Option.map (fun (fields, linter) ->
                        linter, DataValue.field "settings" fields |> Option.map (fun field -> rules linter field.Value) |> Option.defaultValue []))
            | _ ->
                problems.Add DiagnosticCodes.InvalidManifestValue value.Location $"linters must be a list of linters, [(package = ...)], not {DataValue.describe value}"
                []
        // In the order the compiler has them, so diagnostics come out in the same order whatever the manifest's.
        { Linters = Linters.all |> List.filter (fun linter -> chosen |> List.exists (fun (a, _) -> a.Name = linter.Name))
          Severities = chosen |> List.collect snd |> Map.ofList }

    /// The host the manifest's `host` names, if the entry is right: a package the compiler ships, named at the compiler's
    /// version exactly, or one found by its `path`, relative to `root`, which has no version. It takes no settings yet.
    let private readHost (problems: Problems) (root: string) (value: DataValue) : HostEntry option =
        problems.Record "the host" hostFields value
        |> Option.bind (fun fields ->
            match DataValue.field "settings" fields with
            | Some field -> problems.Add DiagnosticCodes.UnsupportedManifestField field.NameSpan "a host takes no settings yet"
            | None -> ()
            let name = problems.RequiredText "the host" value.Location fields "package" namePattern nameRule
            match name, problems.Text "the host" fields "path" anyText "" with
            | Some name, Some path ->
                match DataValue.field "version" fields with
                | Some field ->
                    problems.Add DiagnosticCodes.InvalidManifestValue field.Value.Location $"the host {name} is found by its path, so it has no version: give one or the other"
                    None
                | None -> Some { Package = name; Path = Some(Path.GetFullPath(Path.Combine(root, path))); Span = value.Location }
            | Some name, None ->
                let version = problems.RequiredText "the host" value.Location fields "version" versionPattern "a semantic version: 1.2.0"
                match HostPackages.tryShipped name with
                | Result.Error message ->
                    problems.Add
                        DiagnosticCodes.InvalidManifestValue
                        (DataValue.field "package" fields).Value.Value.Location
                        $"{message}: a host the compiler doesn't ship is found by its path, as host = (package = \"{name}\", path = \"../{name}\")"
                    None
                | Result.Ok _ ->
                    match version with
                    | Some version when version <> Toolchain.version ->
                        problems.Add
                            DiagnosticCodes.VersionNotShipped
                            (DataValue.field "version" fields).Value.Value.Location
                            $"the compiler ships {name} {Toolchain.version}, not {version}: a package the compiler ships is named at its version exactly"
                        None
                    | Some _ -> Some { Package = name; Path = None; Span = value.Location }
                    | None -> None
            | None, _ -> None)

    /// Checks the `host` field of the manifest's `package`, which declares the package a host: its root module, and its
    /// prelude, if it has one (see `HostPackages`).
    let private checkHostDeclaration (problems: Problems) (value: DataValue) =
        problems.Record "the package's host" [ HostPackages.rootField; HostPackages.preludeField ] value
        |> Option.iter (fun fields ->
            problems.RequiredText "the package's host" value.Location fields HostPackages.rootField anyText "" |> ignore
            problems.Text "the package's host" fields HostPackages.preludeField anyText "" |> ignore)

    /// The fields of an entry of `dependencies`.
    let private dependencyFields = [ "package"; "path"; "version" ]

    /// The packages the manifest's `dependencies` lists, each in a directory relative to `root`, or one the compiler
    /// ships, named at the compiler's version exactly. Any other version names a package to download, which isn't
    /// supported yet.
    let private readDependencies (problems: Problems) (root: string) (value: DataValue) : Dependency list =
        let read (value: DataValue) =
            problems.Record "a dependency" dependencyFields value
            |> Option.bind (fun fields ->
                let name = problems.RequiredText "a dependency" value.Location fields "package" namePattern nameRule
                let example = $"""path = "../{name |> Option.map (fun name -> name.Substring(name.LastIndexOf '.' + 1)) |> Option.defaultValue "util"}" """.TrimEnd()
                let shipped = name |> Option.filter (fun name -> Directory.Exists(ShippedPackages.directoryOf name))
                match DataValue.field "path" fields, DataValue.field "version" fields with
                | Some _, Some version ->
                    let named = defaultArg name "here"
                    problems.Add
                        DiagnosticCodes.InvalidManifestValue
                        version.NameSpan
                        $"the dependency {named} is found by its path, so it has no version: give one or the other"
                    None
                | None, Some version when shipped.IsSome ->
                    match problems.Text "a dependency" fields "version" versionPattern "a semantic version: 1.2.0" with
                    | Some written when written <> Toolchain.version ->
                        problems.Add
                            DiagnosticCodes.VersionNotShipped
                            version.Value.Location
                            $"the compiler ships {shipped.Value} {Toolchain.version}, not {written}: a package the compiler ships is named at its version exactly"
                        None
                    | Some _ ->
                        Some
                            { Name = shipped.Value
                              Directory = ShippedPackages.directoryOf shipped.Value
                              NameSpan = (DataValue.field "package" fields).Value.Value.Location
                              PathSpan = version.Value.Location }
                    | None -> None
                | _, Some version ->
                    problems.Add
                        DiagnosticCodes.UnsupportedManifestField
                        version.NameSpan
                        $"a dependency's version names a package to download, but packages can't be downloaded yet: give the directory the package is in, {example}"
                    None
                | None, None ->
                    problems.Add DiagnosticCodes.MissingManifestField value.Location $"a dependency has no path, the directory the package is in: {example}"
                    None
                | Some path, None ->
                    match name, path.Value.Data with
                    | Some name, DataString directory ->
                        Some
                            { Name = name
                              Directory = Path.GetFullPath(Path.Combine(root, directory))
                              NameSpan = (DataValue.field "package" fields).Value.Value.Location
                              PathSpan = path.Value.Location }
                    | _, DataString _ -> None
                    | _ ->
                        problems.Add
                            DiagnosticCodes.InvalidManifestValue
                            path.Value.Location
                            $"a dependency's path must be a string, the directory the package is in, not {DataValue.describe path.Value}"
                        None)
        match value.Data with
        | DataList entries ->
            let dependencies = entries |> List.choose read
            problems.Repeats
                (dependencies |> List.map (fun dependency -> dependency.Name, dependency.NameSpan))
                (fun name -> $"the dependency {name} is listed twice: a package lists each package it depends on once")
            dependencies
        | _ ->
            problems.Add DiagnosticCodes.InvalidManifestValue value.Location $"dependencies must be a list, [(package = ..., path = ...)], not {DataValue.describe value}"
            []

    /// The host `value` names, written as a manifest's `host` is, with its `path` relative to `root`, or what is wrong
    /// with it, as `check` reports it: how a script's `::host` directive is read (see `Script`).
    let internal hostEntry (root: string) (value: DataValue) : Result<HostEntry, Diagnostic list> =
        let problems = Problems()
        match readHost problems root value, problems.All with
        | Some entry, [] -> Result.Ok entry
        | _, problems -> Result.Error problems

    /// The packages `value` lists, written as a manifest's `dependencies` is, each `path` relative to `root`, with what is
    /// wrong with them, as `check` reports it: how a script's `::dependencies` directives are read (see `Script`).
    let internal dependencyEntries (root: string) (value: DataValue) : Dependency list * Diagnostic list =
        let problems = Problems()
        let dependencies = readDependencies problems root value
        dependencies, problems.All

    /// What is wrong with a file that is not one data value, as diagnostics.
    let private dataErrors (errors: DataError list) =
        errors
        |> List.map (function
            | NotParsed error -> Diagnostics.at DiagnosticCodes.SyntaxError error.Span error.Message
            | NotData (span, message) -> Diagnostics.at DiagnosticCodes.CodeInManifest span message)

    /// The manifest at `path`, whose text is `text`, or every problem with it, each at the field it is about.
    let check (path: string) (text: string) : Result<Manifest, Diagnostic list> =
        let path = Path.GetFullPath path
        match DataValue.read path text with
        | Result.Error errors -> Result.Error(dataErrors errors)
        | Result.Ok manifest ->
            let problems = Problems()
            let top = problems.Record "the manifest" fields manifest |> Option.defaultValue []
            let field name = DataValue.field name top
            let dependencies = field "dependencies" |> Option.map (fun field -> readDependencies problems (Path.GetDirectoryName path) field.Value)
            match field "formatter" with
            | Some { DataField.Value = { DataValue.Data = DataRecord _ } }
            | None -> ()
            | Some { DataField.Value = value } -> problems.Record "formatter" [] value |> ignore
            let package =
                match field "package" with
                | Some field -> problems.Record "package" packageFields field.Value |> Option.map (fun fields -> fields, field.Value.Location)
                | None ->
                    if manifest.Data.IsDataRecord then
                        problems.Add DiagnosticCodes.MissingManifestField manifest.Location "the manifest has no package, which needs at least a name and a version"
                    None
            let name, version, sources, publicModules =
                match package with
                | Some (fields, span) ->
                    let name = problems.RequiredText "package" span fields "name" namePattern nameRule
                    let version = problems.RequiredText "package" span fields "version" versionPattern "a semantic version: 1.2.0"
                    let publicModules = problems.Strings "package" fields "publicModules"
                    publicModules
                    |> Option.iter (fun entries -> problems.Repeats entries (fun name -> $"the module '{name}' is listed twice in publicModules"))
                    DataValue.field HostPackages.field fields |> Option.iter (fun field -> checkHostDeclaration problems field.Value)
                    name, version, problems.Strings "package" fields "sources" |> Option.map (List.map fst), publicModules
                | None -> None, None, None, None
            let linters = field "linters" |> Option.map (fun field -> readLinters problems field.Value)
            let host = field "host" |> Option.bind (fun field -> readHost problems (Path.GetDirectoryName path) field.Value)
            match problems.All with
            | [] ->
                Result.Ok
                    { Path = path
                      Root = Path.GetDirectoryName path
                      Name = defaultArg name ""
                      Version = defaultArg version ""
                      Sources = defaultArg sources [ "**/*.nyx" ]
                      PublicModules = defaultArg publicModules []
                      Dependencies = defaultArg dependencies []
                      // No `linters` means no linters: a package turns on the official one by naming nanyx.lint.
                      Linters = defaultArg linters Linters.none
                      // No `host` means a library: an application names the host it runs under.
                      Host = host
                      Declares = HostPackages.ofManifest (defaultArg name "") (Path.GetDirectoryName path) manifest
                      Value = manifest }
            | problems -> Result.Error problems

    /// An error at each entry of the manifest's `publicModules` that names none of `declared`, the modules its
    /// package's source files declare, with the closest of them as a suggestion.
    let unknownPublicModules (manifest: Manifest) (declared: string seq) : Diagnostic list =
        manifest.PublicModules
        |> List.filter (fun (name, _) -> not (Seq.contains name declared))
        |> List.map (fun (name, span) ->
            let suggestions = NameSuggestions.closest declared name
            { Diagnostics.at
                  DiagnosticCodes.InvalidManifestValue
                  span
                  $"publicModules names the module '{name}', but no source file of {manifest.Name} declares it{NameSuggestions.question suggestions}" with
                Fixes = suggestions |> List.map (fun suggestion -> { Title = $"Write `\"{suggestion}\"`"; Edits = [ { Span = span; NewText = $"\"{suggestion}\"" } ] }) })

    /// `diagnostic`, one of a manifest's, as one line: `path:line:column: error NYX0021: message`.
    let describe (diagnostic: Diagnostic) =
        let severity = (string diagnostic.Severity).ToLowerInvariant()
        match diagnostic.Span with
        | Some span -> $"{span.File}:{span.StartLine + 1}:{span.StartColumn + 1}: {severity} {diagnostic.Code}: {diagnostic.Message}"
        | None -> $"{severity} {diagnostic.Code}: {diagnostic.Message}"

    /// `value` as JSON, for tools outside the compiler: a record is an object, a list or tuple an array, and a tag a string
    /// `"#name"`, or an object `{ "#name": payload }` when it has a payload.
    let toJson (value: DataValue) : string =
        use stream = new MemoryStream()
        (
            use writer = new Text.Json.Utf8JsonWriter(stream, Text.Json.JsonWriterOptions(Indented = true))
            let rec write (value: DataValue) =
                match value.Data with
                | DataRecord fields ->
                    writer.WriteStartObject()
                    for field in fields do
                        writer.WritePropertyName field.Name
                        write field.Value
                    writer.WriteEndObject()
                | DataList items
                | DataTuple items ->
                    writer.WriteStartArray()
                    List.iter write items
                    writer.WriteEndArray()
                | DataString text -> writer.WriteStringValue text
                | DataInt n -> writer.WriteRawValue(string n)
                | DataFloat n -> writer.WriteNumberValue n
                | DataBool b -> writer.WriteBooleanValue b
                | DataTag(name, None) -> writer.WriteStringValue $"#{name}"
                | DataTag(name, Some payload) ->
                    writer.WriteStartObject()
                    writer.WritePropertyName $"#{name}"
                    write payload
                    writer.WriteEndObject()
            write value
        )
        Text.Encoding.UTF8.GetString(stream.ToArray()).ReplaceLineEndings "\n"

    /// The manifest at `path`, or every problem with it (see `check`).
    let read (path: string) : Result<Manifest, Diagnostic list> = check path (File.ReadAllText path)
