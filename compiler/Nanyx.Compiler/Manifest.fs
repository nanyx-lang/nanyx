namespace NyxCompiler

open System
open System.IO
open System.Text.RegularExpressions
open Parser.Program
open Parser.Program.DataValues

/// Where a package a manifest depends on is found.
type DependencySource =
    /// In a directory on disk, by its full path: `path = "../util"`.
    | AtPath of directory: string
    /// From the package index, which lists the packages the compiler ships too, at the versions the requirement accepts: `version = "1.2"`.
    | FromIndex of VersionRequirement
    /// In a GitHub repository, at a commit: `github = "acme/web", rev = "..."`.
    | OnGitHub of GitHubReference

/// A package another depends on: in a directory on disk, `(package = "acme.util", path = "../util")` in
/// `dependencies`; from the package index, at a version, `(package = "acme.web", version = "1.2")`; or in a GitHub
/// repository, `(package = "acme.web", github = "acme/web", rev = "...")`.
type Dependency =
    { /// The package's name, which its own manifest must give it too.
      Name: string
      /// Where it is found.
      Source: DependencySource
      /// Where its `package` is written.
      NameSpan: SourceSpan
      /// Where its `path`, its `version` or its `github` is written.
      SourceSpan: SourceSpan }

/// A file in a GitHub repository that a package depends on, which is a module of the package, named by its manifest:
/// `(module = "retry", github = "acme/snippets/retry.nyx", rev = "...")` in `dependencies`.
type FileReference =
    { /// The module name the file has in the package.
      Module: string
      /// The file, and the commit it is pinned to.
      Reference: GitHubReference
      /// Where the entry is written.
      Span: SourceSpan
      /// Where its `github` is written.
      GitHubSpan: SourceSpan }

/// The host an application's manifest names in its `host` field: a package the compiler ships, at its version, or one
/// found by its path, `host = (package = "acme.server", path = "../server")`.
type HostEntry =
    { /// The host's package.
      Package: string
      /// The full path of the host package's directory, for a host found by its `path`; none for one from the package
      /// index, at `Version`.
      Path: string option
      /// The versions of the host that the entry accepts, for one from the package index, `version = "0.43.0"`: those
      /// compatible with it, as a dependency's are; none for one found by its `path`.
      Version: VersionRequirement option
      /// Where the entry is written, which an error about the host it names is reported at.
      Span: SourceSpan
      /// The settings its `settings` gives the host, which must be ones the host takes (see `Host.Settings`).
      Settings: DataField list }

/// A package's `.nyxp` manifest, as far as it is read so far: what the package is called, which files are its
/// sources, what it depends on, which linters check it, and which host runs its `@entry`. Which of its modules other
/// packages may import its files say, with `export module` (see `PackageModules`). Its `formatter` field is the formatter's, which reads it itself.
type Manifest =
    { /// The manifest's full path.
      Path: string
      /// The directory the manifest is in, which its `sources` are relative to.
      Root: string
      Name: string
      Version: string
      /// Globs relative to `Root`: `**/*.nyx` unless the manifest says otherwise, or none for a test package, whose
      /// manifest gives `tests` and no `sources` (see `Manifest.isTestPackage`).
      Sources: string list
      /// Globs relative to `Root` that select the package's test files, which are part of it only in a test build (see
      /// `Package.testResult`); none unless the manifest says.
      Tests: string list
      /// The GitHub repository whose releases are the package's versions, `repository = "acme/web"`, which
      /// `nanyx publish` creates them in; none if the manifest doesn't say.
      Repository: GitHubRepository option
      /// The compilers the package builds with, `nanyx = "0.39"`, that compiler or a later one, or `nanyx = "=0.43.0"`,
      /// that compiler only, which the package index records and the resolver reads; none if the manifest doesn't say.
      /// A compiler it doesn't accept refuses the manifest (`NeedsOtherCompiler`).
      Nanyx: CompilerRequirement option
      /// Where its `package` is written, which an error about the package as a whole is reported at.
      PackageSpan: SourceSpan
      /// The packages it depends on, in the order `dependencies` lists them.
      Dependencies: Dependency list
      /// The files from GitHub it depends on, each a module of the package, in the order `dependencies` lists them.
      Files: FileReference list
      /// The linters the manifest's `linters` turns on, and the severities their `settings` set.
      Linters: LinterOptions
      /// The host `host` names, if the manifest has one: a package with none is a library, which has no `@entry`.
      Host: HostEntry option
      /// The host this package is, if its `package` declares one (see `HostPackages`).
      Declares: Host option
      /// Whether its `package` declares it a host library, whose modules may require `HostRuntime` (see
      /// `HostLibraries.field`).
      HostLibrary: bool
      /// The whole manifest as it is written, for tools that read the rest (see `toJson`).
      Value: DataValue }

/// Reading a manifest: one Nanyx record, written as data (see `DataValue.read`), whose shape is the manifest's own.
module Manifest =
    /// The manifest's file extension.
    let extension = ".nyxp"

    /// The fields a manifest may have. `formatter` is the formatter's, which checks what is in it.
    let private fields = [ "package"; "dependencies"; "linters"; "host"; "formatter" ]

    let private packageFields = [ "name"; "version"; "sources"; "tests"; "repository"; "nanyx"; HostPackages.field; HostLibraries.field ]

    /// Whether `manifest` is a test package's: one that selects test files and no sources, `sources = []` or none at
    /// all, to hold the tests of the packages it depends on. It has no `@entry` and exports nothing, so only a test
    /// build builds it.
    let isTestPackage (manifest: Manifest) = manifest.Sources.IsEmpty && not manifest.Tests.IsEmpty

    /// The fields of an entry of `linters`.
    let private entryFields = [ "package"; "version"; "settings" ]

    /// The fields of the `host` entry.
    let private hostFields = [ "package"; "version"; "path"; "settings" ]

    /// Lower-case dotted names, as packaging.md proposes: `acme.web`, `word_counter`.
    let internal namePattern = Regex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*$")

    /// What `namePattern` asks of a name, to finish "must be ...".
    let internal nameRule = "lower case, with words separated by dots, as `acme.web`"

    /// A GitHub repository, `owner/name`.
    let private repositoryPattern = Regex(@"^(?<owner>[A-Za-z0-9-]+)/(?<name>[A-Za-z0-9_.-]+)$")

    let private versionPattern = Regex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$")

    /// The severities a rule can be given, by name, `off` turning it off. A manifest writes them as tags, `#error`.
    let severities =
        [ "error", Some Severity.Error
          "warning", Some Severity.Warning
          "info", Some Severity.Info
          "hint", Some Severity.Hint
          "off", None ]

    /// `text` with its line breaks `\n`, as a manifest or a script is read.
    let internal normalized (text: string) = text.Replace("\r\n", "\n").Replace("\r", "\n")

    /// The manifests in `directory`, in order: a package's directory has one.
    let internal manifestsIn (directory: string) = Directory.GetFiles(directory, "*" + extension) |> Array.sort

    /// The manifest of the package in `directory`, the one `.nyxp` file there, by its full path, or what is wrong.
    let internal manifestIn (directory: string) : Result<string, string> =
        if not (Directory.Exists directory) then
            Result.Error $"'{directory}' is not a directory"
        else
            match manifestsIn directory with
            | [| manifest |] -> Result.Ok(Path.GetFullPath manifest)
            | [||] -> Result.Error $"'{directory}' has no {extension} manifest"
            | several -> Result.Error $"""'{directory}' has more than one {extension} manifest: {several |> Array.map (Path.GetFileName >> sprintf "'%s'") |> List.ofArray |> Diagnostics.listed}"""

    /// The manifest in `directory` or the nearest directory above it that has one, or an error if that directory
    /// has more than one.
    let rec tryFind (directory: string) : Result<string option, string> =
        match manifestsIn directory with
        | [| manifest |] -> Result.Ok(Some manifest)
        | [||] ->
            match Path.GetDirectoryName directory with
            | null -> Result.Ok None
            | parent -> tryFind parent
        | several -> Result.Error $"""{directory} has more than one {extension} manifest: {several |> Array.map Path.GetFileName |> String.concat ", "}"""

    let private quoted (names: string seq) = names |> Seq.map (fun name -> $"'{name}'") |> String.concat ", "

    /// Collects the problems with a manifest as its fields are read. A message may start with the `what` of the field it
    /// is about, "a dependency", and starts with a capital letter however it is written.
    type private Problems() =
        let found = Collections.Generic.List<Diagnostic>()
        let sentence (message: string) = if message = "" then message else string (Char.ToUpperInvariant message[0]) + message.Substring 1
        member _.All = List.ofSeq found
        member _.Add (code: DiagnosticCode) (span: SourceSpan) (message: string) = found.Add(Diagnostics.at code span (sentence message))
        /// `Add`, with `subject`, the field the problem is about, in the headline.
        member _.AddAbout (code: DiagnosticCode) (span: SourceSpan) (subject: string) (message: string) =
            found.Add(Diagnostics.at code span (sentence message) |> Diagnostics.about subject)
        member _.AddWithFixes (code: DiagnosticCode) (span: SourceSpan) (message: string) (fixes: Fix list) =
            found.Add { Diagnostics.at code span (sentence message) with Fixes = fixes }

        /// The fields of `value`, `what` in messages, if it is a record; each one not in `known` is an error.
        member this.Record (what: string) (known: string list) (value: DataValue) : DataField list option =
            match value.Data with
            | DataRecord fields ->
                for field in fields do
                    if not (List.contains field.Name known) then
                        this.AddAbout DiagnosticCodes.UnknownManifestField field.NameSpan field.Name $"{what} has no field '{field.Name}': its fields are {quoted known}"
                Some fields
            | _ ->
                this.Add DiagnosticCodes.InvalidManifestValue value.Location $"{what} must be a record, `(name = value, ...)`, not {DataValue.describe value}"
                None

        /// The string in the field `name` of `fields`, `what` in messages, if it has one; one that does not match
        /// `pattern` is an error that says it must be `rule`.
        member this.Text (what: string) (fields: DataField list) (name: string) (pattern: Regex) (rule: string) : string option =
            match DataValue.field name fields with
            | Some { DataField.Value = { DataValue.Data = DataString text } } when pattern.IsMatch text -> Some text
            | Some { DataField.Value = { DataValue.Data = DataString text } as value } ->
                this.AddAbout DiagnosticCodes.InvalidManifestValue value.Location name $"""{what}'s {name} "{text}" must be {rule}"""
                None
            | Some field ->
                this.AddAbout DiagnosticCodes.InvalidManifestValue field.Value.Location name $"{what}'s {name} must be a string, not {DataValue.describe field.Value}"
                None
            | None -> None

        /// `Text`, for a field that must be there: its absence is an error at `at`.
        member this.RequiredText (what: string) (at: SourceSpan) (fields: DataField list) (name: string) (pattern: Regex) (rule: string) =
            if (DataValue.field name fields).IsNone then
                this.AddAbout DiagnosticCodes.MissingManifestField at name $"{what} has no {name}, which it needs"
            this.Text what fields name pattern rule

        /// The strings of the list in the field `name` of `fields`, each with where it is written, if it has one.
        member this.Strings (what: string) (fields: DataField list) (name: string) : (string * SourceSpan) list option =
            match DataValue.field name fields with
            | Some { DataField.Value = { DataValue.Data = DataList items } } when items |> List.forall (fun item -> item.Data.IsDataString) ->
                Some(items |> List.choose (fun item -> match item.Data with DataString text -> Some(text, item.Location) | _ -> None))
            | Some field ->
                this.AddAbout DiagnosticCodes.InvalidManifestValue field.Value.Location name $"{what}'s {name} must be a list of strings, not {DataValue.describe field.Value}"
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
            let version = problems.RequiredText what value.Location fields "version" versionPattern "a semantic version, `1.2.0`"
            match problems.RequiredText what value.Location fields "package" anyText "" with
            | Some name ->
                match known name with
                | Result.Ok found ->
                    match version with
                    | Some version when version <> Toolchain.version ->
                        problems.Add
                            DiagnosticCodes.VersionNotShipped
                            (DataValue.field "version" fields).Value.Value.Location
                            $"The compiler ships '{name}' {Toolchain.version}, not {version}: name a package the compiler ships at its version exactly"
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
            | None -> Result.Error $"There is no linter '{name}': the linters are {quoted (Linters.all |> List.map _.Package)}"
        let rules (linter: Linter) (settings: DataValue) =
            let what = $"the settings of '{linter.Package}'"
            problems.Record what [ "rules" ] settings
            |> Option.bind (DataValue.field "rules")
            |> Option.bind (fun field -> problems.Record $"the rules of '{linter.Package}'" (linter.Rules @ [ Linters.failedRule ] |> List.map _.Id) field.Value)
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
                problems.Add DiagnosticCodes.InvalidManifestValue value.Location $"`linters` must be a list of linters, `[(package = ...)]`, not {DataValue.describe value}"
                []
        // In the order the compiler has them, so diagnostics come out in the same order whatever the manifest's.
        { Linters = Linters.all |> List.filter (fun linter -> chosen |> List.exists (fun (a, _) -> a.Name = linter.Name))
          Severities = chosen |> List.collect snd |> Map.ofList }

    /// The host the manifest's `host` names, if the entry is right: a package from the index, at a version, or one found
    /// by its `path`, relative to `root`, which has no version. Whether it is a host is known once it is read (see
    /// `Package`). Its `settings`, a record,
    /// are checked against those the host takes once the host is read (see `Package`).
    let private readHost (problems: Problems) (root: string) (value: DataValue) : HostEntry option =
        problems.Record "the host" hostFields value
        |> Option.bind (fun fields ->
            let settings =
                match DataValue.field "settings" fields with
                | Some { DataField.Value = { DataValue.Data = DataRecord settings } } -> settings
                | Some field ->
                    problems.Add DiagnosticCodes.InvalidManifestValue field.Value.Location $"The host's `settings` must be a record, `(port = 3000)`, not {DataValue.describe field.Value}"
                    []
                | None -> []
            let name = problems.RequiredText "the host" value.Location fields "package" namePattern nameRule
            match name, problems.Text "the host" fields "path" anyText "" with
            | Some name, Some path ->
                match DataValue.field "version" fields with
                | Some field ->
                    problems.Add DiagnosticCodes.InvalidManifestValue field.Value.Location $"The host '{name}' is found by its path, so it has no version: give one or the other"
                    None
                | None -> Some { Package = name; Path = Some(Path.GetFullPath(Path.Combine(root, path))); Version = None; Span = value.Location; Settings = settings }
            | Some name, None when (RenamedPackages.asHost name).IsSome ->
                problems.AddAbout DiagnosticCodes.RenamedPackage (DataValue.field "package" fields).Value.Value.Location name (RenamedPackages.asHost name).Value
                None
            | Some name, None ->
                problems.RequiredText "the host" value.Location fields "version" versionPattern "a semantic version, `1.2.0`"
                |> Option.bind VersionRequirement.tryParse
                |> Option.map (fun version -> { Package = name; Path = None; Version = Some version; Span = value.Location; Settings = settings })
            | None, _ -> None)

    /// Checks the `host` field of the manifest's `package`, which declares the package a host: its root module, its
    /// prelude and its component root, if it has them (see `HostPackages`).
    let private checkHostDeclaration (problems: Problems) (value: DataValue) =
        problems.Record "the package's host" [ HostPackages.rootField; HostPackages.preludeField; HostPackages.settingsField; HostPackages.componentField; HostPackages.filesInMemoryField ] value
        |> Option.iter (fun fields ->
            problems.RequiredText "the package's host" value.Location fields HostPackages.rootField anyText "" |> ignore
            problems.Text "the package's host" fields HostPackages.preludeField anyText "" |> ignore
            problems.Text "the package's host" fields HostPackages.filesInMemoryField anyText "" |> ignore
            // Its component root, which a component is built from, the world it is written against, and the world a
            // library component is built in, if it has one.
            match DataValue.field HostPackages.componentField fields with
            | Some field ->
                problems.Record "the host's component" [ HostPackages.rootField; HostPackages.worldField; HostPackages.libraryField ] field.Value
                |> Option.iter (fun built ->
                    problems.RequiredText "the host's component" field.Value.Location built HostPackages.rootField anyText "" |> ignore
                    problems.RequiredText "the host's component" field.Value.Location built HostPackages.worldField anyText "" |> ignore
                    problems.Text "the host's component" built HostPackages.libraryField anyText "" |> ignore)
            | None -> ()
            match DataValue.field HostPackages.settingsField fields with
            | Some { DataField.Value = { DataValue.Data = DataRecord _ } }
            | None -> ()
            | Some field ->
                problems.Add DiagnosticCodes.InvalidManifestValue field.Value.Location $"A host's `settings` must be a record of the settings it takes with their defaults, `(port = 3000)`, not {DataValue.describe field.Value}")

    /// Checks that the package `name`, whose manifest's `package` is `fields`, is named for a host exactly when it
    /// declares that it is one: a host's name ends in `Hosts.nameSuffix`, and a library's doesn't.
    let private checkHostName (problems: Problems) (fields: DataField list) (name: string) =
        let declaresHost = (DataValue.field HostPackages.field fields).IsSome
        if declaresHost <> name.EndsWith Hosts.nameSuffix then
            let message =
                if declaresHost then
                    $"The package '{name}' declares a `host`, so its name must end in `{Hosts.nameSuffix}`, as '{name}{Hosts.nameSuffix}' does: a host is named for the library it runs programs against, with `{Hosts.nameSuffix}` after it"
                else
                    $"The package '{name}' declares no `host`, so its name must not end in `{Hosts.nameSuffix}`, which only a host's does: rename it, or declare the host it is"
            problems.AddAbout DiagnosticCodes.HostNameConvention (DataValue.field "name" fields).Value.Value.Location name message

    /// The fields of an entry of `dependencies`.
    let private dependencyFields = [ "package"; "module"; "path"; "version"; "github"; "rev"; "commit" ]

    /// What a module name must be, as the name a manifest gives a file from GitHub.
    let private modulePattern = Regex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)*$")

    /// Finds the commit a tag or branch of a repository points at now, for the fix of an entry that is not pinned; none
    /// when it can't be found.
    type Pin = GitHubRepository -> string -> string option

    /// The commit the GitHub reference `reference`, an entry `value` with `fields` whose `github` is `github`, is pinned to:
    /// its `rev`, if that is a full commit SHA, or else its `commit`. One pinned to neither is an error, since what a tag or
    /// a branch names can move, and two machines with the same manifest must build the same program. Its fix, where `pin`
    /// finds the commit, writes that commit in.
    let private pinned (problems: Problems) (pin: Pin) (repository: GitHubRepository) (reference: string) (github: DataField) (fields: DataField list) (value: DataValue) =
        let written name =
            match DataValue.field name fields with
            | Some { DataField.Value = { DataValue.Data = DataString text } as value } -> Some(text, value.Location)
            | Some field ->
                problems.Add DiagnosticCodes.InvalidManifestValue field.Value.Location $"a GitHub reference's {name} must be a string, not {DataValue.describe field.Value}"
                None
            | None -> None
        let notPinned (span: SourceSpan) (message: string) (rev: string) (after: SourceSpan) (field: string) =
            let at = { after with Start = after.End; StartColumn = after.StartColumn + (after.End - after.Start) }
            let fixes =
                pin repository rev
                |> Option.map (fun commit -> { Title = $"Pin to {commit}"; Edits = [ { Span = at; NewText = $", {field} = \"{commit}\"" } ] })
                |> Option.toList
            problems.AddWithFixes DiagnosticCodes.GitHubReferenceNotPinned span message fixes
            None
        match written "rev", written "commit" with
        | _, Some (commit, span) when not (GitHub.isCommit commit) ->
            problems.Add DiagnosticCodes.InvalidManifestValue span $"""The commit "{commit}" of '{reference}' must be a full commit SHA, 40 hexadecimal digits in lower case"""
            None
        | Some (rev, _), Some (commit, span) when GitHub.isCommit rev && rev <> commit ->
            problems.Add DiagnosticCodes.InvalidManifestValue span $"The `rev` and the `commit` of '{reference}' are different commits: give one of them"
            None
        | _, Some (commit, _) -> Some commit
        | Some (rev, _), None when GitHub.isCommit rev -> Some rev
        | Some (rev, span), None ->
            notPinned span ($"""'{reference}' names "{rev}", which is not a full commit SHA, so what it names can move: pin it to the commit it names, with `commit = "<SHA>"`""") rev span "commit"
        | None, None -> notPinned value.Location $"'{reference}' has no `rev`, so nothing pins it to a commit: give the commit, with `rev = \"<SHA>\"`" "HEAD" github.Value.Location "rev"

    /// The entry `value`, with `fields`, of a dependency on GitHub, whose `github` is `github`: a package, which is a
    /// repository, or a module, which is a file of one, as the module `module` names; and in either case a commit.
    let private readGitHub (problems: Problems) (pin: Pin) (fields: DataField list) (github: DataField) (value: DataValue) : Choice<Dependency, FileReference> option =
        let named name = DataValue.field name fields
        for name in [ "path"; "version" ] do
            named name
            |> Option.iter (fun field ->
                problems.Add DiagnosticCodes.InvalidManifestValue field.NameSpan $"A dependency on GitHub is found by its `github` and pinned by its `rev`, so it has no `{name}`")
        match github.Value.Data with
        | DataString text ->
            match GitHub.parse text with
            | Result.Error rule ->
                problems.Add DiagnosticCodes.InvalidManifestValue github.Value.Location $"""a dependency's github "{text}" must be {rule}"""
                None
            | Result.Ok (repository, file) ->
                let commit = pinned problems pin repository text github fields value
                let reference = { Repository = repository; File = file; Commit = defaultArg commit "" }
                match named "package", named "module", file with
                | Some _, Some moduleField, _ ->
                    problems.Add
                        DiagnosticCodes.InvalidManifestValue
                        moduleField.NameSpan
                        $"A dependency is a package or a module, not both: give a package's repository, `github = \"{repository}\"`, or a module's file, `github = \"{repository}/retry.nyx\"`"
                    None
                | None, None, _ ->
                    problems.Add
                        DiagnosticCodes.MissingManifestField
                        value.Location
                        ($"""A dependency on GitHub has no `package` or `module`: name the package the repository holds, `package = "acme.web"`, or the module a file is in this package, `module = "retry"`""")
                    None
                | Some _, None, Some _ ->
                    problems.Add
                        DiagnosticCodes.InvalidManifestValue
                        github.Value.Location
                        ($"""A package on GitHub is a whole repository, `github = "{repository}"`: depend on one file of a repository as a module, `module = "retry"`""")
                    None
                | None, Some _, None ->
                    problems.Add
                        DiagnosticCodes.InvalidManifestValue
                        github.Value.Location
                        ($"""A module on GitHub is one file of a repository: give the file's path, `github = "{repository}/retry.nyx"`""")
                    None
                | None, Some _, Some path when not (path.EndsWith ".nyx") ->
                    problems.Add DiagnosticCodes.InvalidManifestValue github.Value.Location $"A module on GitHub is a Nanyx file, whose name ends `.nyx`, not '{path}'"
                    None
                | Some packageField, None, None ->
                    match problems.Text "a dependency" fields "package" namePattern nameRule, commit with
                    | Some name, Some _ ->
                        Some(Choice1Of2 { Name = name; Source = OnGitHub reference; NameSpan = packageField.Value.Location; SourceSpan = github.Value.Location })
                    | _ -> None
                | None, Some _, Some _ ->
                    match problems.Text "a dependency" fields "module" modulePattern "a module name, `retry` or `acme.retry`", commit with
                    | Some name, Some _ -> Some(Choice2Of2 { Module = name; Reference = reference; Span = value.Location; GitHubSpan = github.Value.Location })
                    | _ -> None
        | _ ->
            problems.Add
                DiagnosticCodes.InvalidManifestValue
                github.Value.Location
                $"A dependency's `github` must be a string, the repository, `acme/web`, or a file in it, `acme/snippets/retry.nyx`, not {DataValue.describe github.Value}"
            None

    /// The packages the manifest's `dependencies` lists, each in a directory relative to `root`, one the compiler ships,
    /// named at the compiler's version exactly, one from the index, at a version, or one on GitHub; and the files on
    /// GitHub it lists, each a module of the package. `pin` finds the commit for the
    /// fix of a GitHub reference that is not pinned to one.
    let private readDependencies (problems: Problems) (pin: Pin) (root: string) (value: DataValue) : Dependency list * FileReference list =
        let readPackage (fields: DataField list) (value: DataValue) : Dependency option =
            let name = problems.RequiredText "a dependency" value.Location fields "package" namePattern nameRule
            let example = $"""path = "../{name |> Option.map (fun name -> name.Substring(name.LastIndexOf '.' + 1)) |> Option.defaultValue "util"}" """.TrimEnd()
            let renamed = name |> Option.bind RenamedPackages.asDependency
            match DataValue.field "path" fields, DataValue.field "version" fields with
            | None, _ when renamed.IsSome ->
                problems.AddAbout DiagnosticCodes.RenamedPackage (DataValue.field "package" fields).Value.Value.Location name.Value renamed.Value
                None
            | Some _, Some version ->
                let named = defaultArg name "here"
                problems.Add
                    DiagnosticCodes.InvalidManifestValue
                    version.NameSpan
                    $"The dependency '{named}' is found by its path, so it has no version: give one or the other"
                None
            | None, Some version ->
                match version.Value.Data, name with
                | DataString written, Some name ->
                    match VersionRequirement.tryParse written with
                    | Some requirement ->
                        Some
                            { Name = name
                              Source = FromIndex requirement
                              NameSpan = (DataValue.field "package" fields).Value.Value.Location
                              SourceSpan = version.Value.Location }
                    | None ->
                        problems.Add DiagnosticCodes.InvalidManifestValue version.Value.Location $"""a dependency's version "{written}" must be {VersionRequirement.rule}"""
                        None
                | DataString _, None -> None
                | _ ->
                    problems.Add DiagnosticCodes.InvalidManifestValue version.Value.Location $"a dependency's version must be a string, {VersionRequirement.rule}, not {DataValue.describe version.Value}"
                    None
            | None, None ->
                problems.Add DiagnosticCodes.MissingManifestField value.Location ($"""A dependency has no `path` or `version`: give the directory the package is in, `{example}`, or the version to download, `version = "1.2"`""")
                None
            | Some path, None ->
                match name, path.Value.Data with
                | Some name, DataString directory ->
                    Some
                        { Name = name
                          Source = AtPath(Path.GetFullPath(Path.Combine(root, directory)))
                          NameSpan = (DataValue.field "package" fields).Value.Value.Location
                          SourceSpan = path.Value.Location }
                | _, DataString _ -> None
                | _ ->
                    problems.Add
                        DiagnosticCodes.InvalidManifestValue
                        path.Value.Location
                        $"A dependency's `path` must be a string, the directory the package is in, not {DataValue.describe path.Value}"
                    None
        let read (value: DataValue) =
            problems.Record "a dependency" dependencyFields value
            |> Option.bind (fun fields ->
                match DataValue.field "github" fields with
                | Some github -> readGitHub problems pin fields github value
                | None when (DataValue.field "module" fields).IsSome ->
                    problems.Add
                        DiagnosticCodes.MissingManifestField
                        value.Location
                        "A module dependency is a file on GitHub, but this one has no `github`: give the file, `github = \"acme/snippets/retry.nyx\"`"
                    None
                | None ->
                    for name in [ "rev"; "commit" ] do
                        DataValue.field name fields
                        |> Option.iter (fun field ->
                            problems.Add DiagnosticCodes.InvalidManifestValue field.NameSpan $"A dependency's `{name}` pins a reference to GitHub, but this one has no `github`")
                    readPackage fields value |> Option.map Choice1Of2)
        match value.Data with
        | DataList entries ->
            let read = entries |> List.choose read
            let dependencies = read |> List.choose (function Choice1Of2 dependency -> Some dependency | Choice2Of2 _ -> None)
            let files = read |> List.choose (function Choice2Of2 file -> Some file | Choice1Of2 _ -> None)
            problems.Repeats
                (dependencies |> List.map (fun dependency -> dependency.Name, dependency.NameSpan))
                (fun name -> $"The dependency '{name}' is listed twice, but a package lists each package it depends on once: remove one")
            problems.Repeats
                (files |> List.map (fun file -> file.Module, file.Span))
                (fun name -> $"The module '{name}' is listed twice, but a package gives each module name to one file: remove one")
            dependencies, files
        | _ ->
            problems.Add DiagnosticCodes.InvalidManifestValue value.Location $"`dependencies` must be a list, `[(package = ..., path = ...)]`, not {DataValue.describe value}"
            [], []

    /// The host `value` names, written as a manifest's `host` is, with its `path` relative to `root`, or what is wrong
    /// with it, as `check` reports it: how a script's `::host` directive is read (see `Script`).
    let internal hostEntry (root: string) (value: DataValue) : Result<HostEntry, Diagnostic list> =
        let problems = Problems()
        match readHost problems root value, problems.All with
        | Some entry, [] -> Result.Ok entry
        | _, problems -> Result.Error problems

    /// The packages and the files from GitHub `value` lists, written as a manifest's `dependencies` is, each `path`
    /// relative to `root`, with what is wrong with them, as `check` reports it: how a script's `::install` directives
    /// are read (see `Script`).
    let internal dependencyEntries (root: string) (value: DataValue) : Dependency list * FileReference list * Diagnostic list =
        let problems = Problems()
        let dependencies, files = readDependencies problems (fun _ _ -> None) root value
        dependencies, files, problems.All

    /// The linter options `value` sets, written as a manifest's `linters` is, with what is wrong with it, as `check`
    /// reports it: how a script's `::linter` and `::lint` directives are read (see `Script`).
    let internal linterEntries (value: DataValue) : LinterOptions * Diagnostic list =
        let problems = Problems()
        let options = readLinters problems value
        options, problems.All

    /// What is wrong with a file that is not one data value, as diagnostics.
    let private dataErrors (errors: DataError list) =
        errors
        |> List.map (function
            | NotParsed error -> Diagnostics.ofParseError error
            | NotData (span, message) -> Diagnostics.at DiagnosticCodes.CodeInManifest span message)

    /// A TOML table header, `[package]`, or a TOML key at the top level, `name = "shop"`.
    let private tomlLine = Regex(@"^(\[[^\]]*\]|[A-Za-z_][\w.\-""]*\s*=)")

    /// An error at the first line of `text` if it is written as a TOML manifest was, before manifests were Nanyx records:
    /// its first line that is not blank or a comment is a table header or a key, where a record starts with `(`.
    let private tomlError (path: string) (text: string) : Diagnostic option =
        // Each line keeps a `\r` before its `\n`, so the offsets are the text's own.
        let lines = text.Split '\n'
        let starts = lines |> Array.scan (fun start (line: string) -> start + line.Length + 1) 0
        lines
        |> Array.indexed
        |> Array.tryFind (fun (_, line) -> let trimmed = line.Trim() in trimmed <> "" && not (trimmed.StartsWith "#") && not (trimmed.StartsWith "--"))
        |> Option.filter (fun (_, line) -> tomlLine.IsMatch(line.TrimStart()))
        |> Option.map (fun (index, line) ->
            let column = line.Length - line.TrimStart().Length
            let span = { File = path; Start = starts.[index] + column; End = starts.[index] + line.TrimEnd().Length; StartLine = index; StartColumn = column }
            Diagnostics.at DiagnosticCodes.TomlManifest span "The manifest is a Nanyx record, `( package = (...) ... )`, not TOML")

    /// The compilers `field`, a package's `nanyx`, says the package builds with, if it says ones this compiler can read:
    /// an error at the field where it isn't a compiler requirement, or doesn't accept this compiler.
    let private readNanyx (problems: Problems) (field: DataField) : CompilerRequirement option =
        match field.Value.Data with
        | DataString written ->
            match CompilerRequirement.tryParse written, SemanticVersion.tryParse Toolchain.version with
            | Some requirement, Some running when not (CompilerRequirement.accepts requirement running) ->
                let fix =
                    if requirement.Exact then "build it with that compiler, or use a version of the package made for this one"
                    elif requirement.Oldest > running then "install a newer compiler to build it"
                    else "build it with one of those"
                problems.Add
                    DiagnosticCodes.NeedsOtherCompiler
                    field.Value.Location
                    $"The package builds with {CompilerRequirement.describe requirement}, and this is nanyx {Toolchain.version}: {fix}"
                None
            | Some requirement, _ -> Some requirement
            | None, _ ->
                problems.Add DiagnosticCodes.InvalidManifestValue field.Value.Location $"""The package's `nanyx` "{written}" must be {CompilerRequirement.rule}"""
                None
        | _ ->
            problems.Add DiagnosticCodes.InvalidManifestValue field.Value.Location $"The package's `nanyx` must be a string, {CompilerRequirement.rule}, not {DataValue.describe field.Value}"
            None

    /// The manifest at `path`, whose text is `text`, or every problem with it, each at the field it is about. One in
    /// the old TOML form has the one error that says so. A GitHub reference that is not pinned to a commit has a fix
    /// that pins it to the commit `pin` finds, if it finds one.
    let checkPinning (pin: Pin) (path: string) (text: string) : Result<Manifest, Diagnostic list> =
        let path = Path.GetFullPath path
        match DataValue.read path text with
        | Result.Error errors ->
            match tomlError path text with
            | Some error -> Result.Error [ error ]
            | None -> Result.Error(dataErrors errors)
        | Result.Ok manifest ->
            let problems = Problems()
            let top = problems.Record "the manifest" fields manifest |> Option.defaultValue []
            let field name = DataValue.field name top
            let dependencies = field "dependencies" |> Option.map (fun field -> readDependencies problems pin (Path.GetDirectoryName path) field.Value)
            match field "formatter" with
            | Some { DataField.Value = { DataValue.Data = DataRecord _ } }
            | None -> ()
            | Some { DataField.Value = value } -> problems.Record "formatter" [] value |> ignore
            let package =
                match field "package" with
                | Some field -> problems.Record "package" packageFields field.Value |> Option.map (fun fields -> fields, field.Value.Location)
                | None ->
                    if manifest.Data.IsDataRecord then
                        problems.AddAbout DiagnosticCodes.MissingManifestField manifest.Location "package" "The manifest has no `package`, which needs at least a name and a version"
                    None
            let name, version, sources, tests, repository, nanyx =
                match package with
                | Some (fields, span) ->
                    let name = problems.RequiredText "package" span fields "name" namePattern nameRule
                    name |> Option.iter (checkHostName problems fields)
                    let version = problems.RequiredText "package" span fields "version" versionPattern "a semantic version, `1.2.0`"
                    DataValue.field HostPackages.field fields |> Option.iter (fun field -> checkHostDeclaration problems field.Value)
                    match DataValue.field HostLibraries.field fields with
                    | Some { DataField.Value = { DataValue.Data = DataBool _ } }
                    | None -> ()
                    | Some field ->
                        problems.Add DiagnosticCodes.InvalidManifestValue field.Value.Location $"`{HostLibraries.field}` is `true` for a package hosts are written on, or `false`, not {DataValue.describe field.Value}"
                    let repository =
                        problems.Text "package" fields "repository" repositoryPattern "a GitHub repository, `owner/name`, as `acme/web`"
                        |> Option.map (fun text -> let found = repositoryPattern.Match text in { Owner = found.Groups["owner"].Value; Name = found.Groups["name"].Value })
                    let nanyx = DataValue.field "nanyx" fields |> Option.bind (readNanyx problems)
                    let tests = problems.Strings "package" fields "tests" |> Option.map (List.map fst)
                    name, version, problems.Strings "package" fields "sources" |> Option.map (List.map fst), tests, repository, nanyx
                | None -> None, None, None, None, None, None
            let linters = field "linters" |> Option.map (fun field -> readLinters problems field.Value)
            let host = field "host" |> Option.bind (fun field -> readHost problems (Path.GetDirectoryName path) field.Value)
            match problems.All with
            | [] ->
                Result.Ok
                    { Path = path
                      Root = Path.GetDirectoryName path
                      Name = defaultArg name ""
                      Version = defaultArg version ""
                      // A manifest that selects tests and no sources is a test package's, which has no sources.
                      Sources = defaultArg sources (if tests |> Option.exists (not << List.isEmpty) then [] else [ "**/*.nyx" ])
                      Tests = defaultArg tests []
                      Repository = repository
                      Nanyx = nanyx
                      PackageSpan = package |> Option.map snd |> Option.defaultValue manifest.Location
                      Dependencies = dependencies |> Option.map fst |> Option.defaultValue []
                      Files = dependencies |> Option.map snd |> Option.defaultValue []
                      // No `linters` means no linters: a package turns on the official one by naming nanyx.lint.
                      Linters = defaultArg linters Linters.none
                      // No `host` means a library: an application names the host it runs under.
                      Host = host
                      Declares = HostPackages.ofManifest (defaultArg name "") (Path.GetDirectoryName path) manifest
                      HostLibrary = HostLibraries.declares manifest
                      Value = manifest }
            | problems -> Result.Error problems

    /// The manifest at `path`, whose text is `text`, or every problem with it (see `checkPinning`).
    let check (path: string) (text: string) : Result<Manifest, Diagnostic list> = checkPinning (fun _ _ -> None) path text

    /// An error at each GitHub reference of `manifest`, since a package that has one can't be packed or published: a
    /// published package must keep building for as long as anything depends on it, and what is in someone else's
    /// repository can disappear. Building and running it is fine.
    let publishErrors (manifest: Manifest) : Diagnostic list =
        let error (span: SourceSpan) (what: string) =
            Diagnostics.at
                DiagnosticCodes.GitHubReferenceNotPublishable
                span
                $"{what} is on GitHub, so '{manifest.Name}' can't be packed or published, since a published package must keep building for as long as anything depends on it, and what is in someone else's repository can disappear"
        (manifest.Dependencies
         |> List.choose (fun dependency ->
             match dependency.Source with
             | OnGitHub reference -> Some(error dependency.SourceSpan $"The dependency '{dependency.Name}', '{reference}',")
             | AtPath _
             | FromIndex _ -> None))
        @ (manifest.Files |> List.map (fun file -> error file.GitHubSpan $"The module '{file.Module}', '{file.Reference}',"))

    /// `text` as a manifest writes a string: in quotes, with `\` and `"` escaped.
    let internal quotedString (text: string) = "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

    /// The entry of `dependencies`, or the `host`, that names the package `name` found at `source`, as `nanyx migrate`,
    /// `nanyx install` and `nanyx new` write it: `(package = "acme.util", path = "../util")`, its path relative to
    /// `root`, the manifest's directory; one from the index at a version, `(package = "nanyx.cli", version = "0.43.0")`;
    /// and a file on GitHub, which is a module of the package, `(module = "retry", github = "acme/snippets/retry.nyx",
    /// rev = "...")`. A reference with no commit yet is written with no `rev`, which the build's fix pins.
    let entryText (root: string) (name: string) (source: DependencySource) : string =
        match source with
        | AtPath directory -> $"(package = {quotedString name}, path = {quotedString (PackageModules.relativePath root directory)})"
        | FromIndex requirement -> $"(package = {quotedString name}, version = {quotedString (string requirement)})"
        | OnGitHub reference ->
            let key = if reference.File.IsSome then "module" else "package"
            let rev = if reference.Commit = "" then "" else $", rev = {quotedString reference.Commit}"
            $"({key} = {quotedString name}, github = {quotedString (string reference)}{rev})"

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

    /// The manifest of the package in `directory`, if it has one and it reads: what is wrong with it is left for the
    /// build to report.
    let internal tryReadIn (directory: string) : Manifest option =
        manifestIn directory |> Result.toOption |> Option.bind (read >> Result.toOption)
