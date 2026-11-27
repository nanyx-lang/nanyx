namespace NyxCompiler

open System
open System.IO
open System.Text.RegularExpressions
open Parser.Program
open Parser.Program.DataValues

/// What a script's `::` directives say: what a package's manifest would, for a file run on its own, which has none.
type ScriptDirectives =
    { /// The linters the `::linter` directives turn on and the severities the `::lint` lines under them set, as a
      /// manifest's `linters` would. None when there is no `::linter` directive: then no linter runs, as for a package
      /// whose manifest has no `linters`.
      Linters: LinterOptions option
      /// The build the `::host` and `::install` directives make: the host the script runs under and the packages it
      /// imports from (see `Package.scriptModules`). None when there are neither, or they are wrong: then the script
      /// runs under the console host and imports only the standard library and files.
      Build: PackageModules option
      /// What is wrong with the directives, each at its line, or at the word it is about.
      Diagnostics: Diagnostic list }

/// Where the package a `::host`, `::install` or `::linter` directive names is found.
type DirectivePlace =
    /// At a version, a word that starts with a digit: `1.2.0`.
    | AtVersion of DirectiveWord
    /// In a directory, relative to the script, a word that starts with `.` or `/`: `../util`.
    | AtPath of DirectiveWord
    /// In a GitHub repository, at a commit, a word that starts with `github:`: `github:acme/web@<commit>` for a
    /// package, or `github:acme/snippets/retry.nyx@<commit>` for a file, which is a module of the script's build.
    | AtGitHub of DirectiveWord
    /// Neither: a package the compiler ships, at the compiler's version.
    | Shipped

/// What a directive says, read from its words. Each is one entry of a manifest.
type DirectiveSetting =
    /// `::host nanyx.cli.host`: the host the script runs under.
    | HostSetting of package: DirectiveWord * DirectivePlace
    /// `::install acme.util ../util`: a package the script imports from; or `::install retry
    /// github:acme/snippets/retry.nyx@<commit>`, a file from GitHub it imports as the module `retry`.
    | DependencySetting of package: DirectiveWord * DirectivePlace
    /// `::linter nanyx.lint`: a linter to run.
    | LinterSetting of package: DirectiveWord * DirectivePlace
    /// `::lint unusedBindings #error`: a rule's severity, for the linter whose package `linter` names, the nearest
    /// `::linter` above it.
    | LintSetting of linter: DirectiveWord * rule: DirectiveWord * severity: DataValue
    /// `::format indentSize 4`: a formatter setting, which the formatter reads itself.
    | FormatSetting of key: DirectiveWord * value: DataValue

module Script =
    let private directiveList = "`::host`, `::install`, `::linter`, `::lint` and `::format`"

    let private invalid (span: SourceSpan) (message: string) = Diagnostics.at DiagnosticCodes.InvalidDirective span message

    /// What `::<name>` takes, as the error for a directive written wrong shows it.
    let private usage (name: string) =
        match name with
        | "host" -> $"`::host` takes a host and where it is found: write `::host {Hosts.consolePackage}`, or `::host acme.server ../server`"
        | "install" ->
            "`::install` takes a package and where it is found, or a module name and a file on GitHub: write `::install acme.util ../util`, `::install nanyx.cli` or `::install acme.web github:acme/web@<commit>`, or `::install retry github:acme/snippets/retry.nyx@<commit>`"
        | "linter" -> "`::linter` takes a linter: write `::linter nanyx.lint`"
        | "lint" -> "`::lint` takes a rule of the linter above it and a severity: write `::lint unusedBindings #error`"
        | _ -> "`::format` takes a setting and its value: write `::format lineWidth 100`"

    /// What a word that names a place on GitHub starts with.
    let private gitHubPrefix = "github:"

    /// The reference a `github:` word makes, `github:acme/snippets/retry.nyx@<commit>`: what a manifest's `github` would
    /// say, the commit or the tag or branch after its `@`, if it has one, and whether it is a file, which names a module.
    let internal gitHubPlace (place: DirectiveWord) : string * string option * bool =
        let reference = place.Text.Substring gitHubPrefix.Length
        let github, rev =
            match reference.LastIndexOf '@' with
            | -1 -> reference, None
            | at -> reference.Substring(0, at), Some(reference.Substring(at + 1))
        github, rev, github.Split('/').Length > 2

    /// Where the word after a package's name says it is found: a version, which starts with a digit, a directory,
    /// which starts with `.` or `/` (or is rooted, `C:\util`), or a GitHub repository, which starts with `github:`.
    let private placeOf (word: DirectiveWord) : Result<DirectivePlace, Diagnostic> =
        if word.Text.StartsWith gitHubPrefix then Result.Ok(AtGitHub word)
        elif Char.IsDigit word.Text[0] then Result.Ok(AtVersion word)
        elif word.Text[0] = '.' || word.Text[0] = '/' || word.Text[0] = '\\' || Path.IsPathRooted word.Text then Result.Ok(AtPath word)
        else Result.Error(invalid word.Span $"'{word.Text}' is neither a version, which starts with a digit, as `1.2.0`, nor a directory, which starts with `.` or `/`, as `../util`")

    /// The package a `::host`, `::install` or `::linter` directive names, and where it is found.
    let private packageOf (directive: Directive) : Result<DirectiveWord * DirectivePlace, Diagnostic> =
        match directive.Arguments with
        | [] -> Result.Error(invalid directive.Span (usage directive.Name) |> Diagnostics.about $"::{directive.Name}")
        | [ package ] -> Result.Ok(package, Shipped)
        | [ package; place ] -> placeOf place |> Result.map (fun place -> package, place)
        | _ :: _ :: extra :: _ -> Result.Error(invalid extra.Span (usage directive.Name) |> Diagnostics.about $"::{directive.Name}")

    /// A value a `::lint` or `::format` directive sets, written as a manifest writes one.
    let private valueOf (directive: Directive) (word: DirectiveWord) : Result<DataValue, Diagnostic> =
        match DataValue.ofWord word with
        | Some value -> Result.Ok value
        | None ->
            Result.Error(invalid word.Span $"'{word.Text}' is not a value: write one as a manifest does, a tag, `#error`, a number, `4`, or a string, `\"text\"`; {usage directive.Name}")

    /// A string in a directive written the old way, `"error"`, without its quotes.
    let private oldString = "\"([^\"\\\\]*)\""

    /// The lines `text`, a directive written the old way, as a manifest's dotted key with a TOML value, is written as
    /// now, if it can be read: `::host.nanyx.cli.host = "0.28.0"` is `::host nanyx.cli.host 0.28.0`. `linterAbove` says
    /// whether a linter is turned on above it already, so a rule's severity needs no `::linter` of its own.
    let private rewritten (linterAbove: bool) (text: string) : string option =
        let matching (pattern: string) = Regex.Match(text, $@"^::{pattern}\s*$")
        let linterPackage (name: string) = Linters.all |> List.tryFind (fun linter -> linter.Name = name) |> Option.map _.Package
        let place (path: string) =
            if path.StartsWith "." || path.StartsWith "/" || Path.IsPathRooted path then path else "./" + path
        let linterOn = matching @"linters\.(\w+)"
        let rule = matching $@"linters\.(\w+)\.(\w+)\s*=\s*{oldString}"
        let format = matching @"formatter\.(\w+)\s*=\s*([0-9]+)"
        let atVersion = matching $@"(host|dependencies)\.([\w.]+)\s*=\s*{oldString}"
        let atPath = matching $@"(host|dependencies)\.([\w.]+)\s*=\s*\{{\s*path\s*=\s*{oldString}\s*\}}"
        let named (table: string) = if table = "host" then "host" else "install"
        if linterOn.Success then
            linterPackage linterOn.Groups[1].Value |> Option.map (fun package -> $"::linter {package}")
        elif rule.Success then
            linterPackage rule.Groups[1].Value
            |> Option.map (fun package ->
                let lint = $"::lint {rule.Groups[2].Value} #{rule.Groups[3].Value}"
                if linterAbove then lint else $"::linter {package}\n{lint}")
        elif format.Success then
            Some $"::format {format.Groups[1].Value} {format.Groups[2].Value}"
        elif atVersion.Success then
            Some $"::{named atVersion.Groups[1].Value} {atVersion.Groups[2].Value} {atVersion.Groups[3].Value}"
        elif atPath.Success then
            Some $"::{named atPath.Groups[1].Value} {atPath.Groups[2].Value} {place atPath.Groups[3].Value}"
        else
            None

    /// The error for `directive`, written the old way, with a fix that writes it as it is written now, if it can be read.
    /// A comment after it stays.
    let private oldFormError (linterAbove: bool) (directive: Directive) : Diagnostic =
        let written = match List.tryLast directive.Words with Some last -> last.Span.End | None -> directive.Span.End
        match rewritten linterAbove (directive.Text.Substring(0, written - directive.Span.Start)) with
        | Some now ->
            { Diagnostics.at
                  DiagnosticCodes.OldFormDirective
                  directive.Span
                  $"Directives are lines of words now, not a manifest's key and a TOML value: write `{now.Replace('\n', ' ')}`" with
                Fixes = [ { Title = $"Write `{now.Replace('\n', ' ')}`"; Edits = [ { Span = { directive.Span with End = written }; NewText = now } ] } ] }
        | None ->
            Diagnostics.at
                DiagnosticCodes.OldFormDirective
                directive.Span
                $"Directives are lines of words now, not a manifest's key and a TOML value: write them as `::host {Hosts.consolePackage}`, `::install acme.util ../util`, `::linter nanyx.lint`, `::lint unusedBindings #error` and `::format lineWidth 100`"

    /// The error for a `::lint` with no `::linter` above it, with a fix that turns on the official linter above it.
    let private lintWithoutLinter (directive: Directive) : Diagnostic =
        let official = Linters.all |> List.map _.Package |> List.head
        { Diagnostics.at
              DiagnosticCodes.LintWithoutLinter
              directive.Span
              $"`::lint` sets a rule of the linter of the nearest `::linter` above it, but there is none: turn one on above it, `::linter {official}`" with
            Fixes = [ { Title = $"Add `::linter {official}` above"; Edits = [ { Span = { directive.Span with End = directive.Span.Start }; NewText = $"::linter {official}\n" } ] } ] }

    /// What each of `directives` says, in order, or what is wrong with it. A `::lint` is for the linter of the nearest
    /// `::linter` above it.
    let settings (directives: Directive list) : (Directive * Result<DirectiveSetting, Diagnostic>) list =
        directives
        |> List.mapFold
            (fun (linter: DirectiveWord option, linterAbove: bool) (directive: Directive) ->
                let setting, linter =
                    match directive.Name with
                    | _ when directive.IsOldForm -> Result.Error(oldFormError linterAbove directive), linter
                    | "host" ->
                        match packageOf directive with
                        | Result.Ok (_, AtGitHub place) -> Result.Error(invalid place.Span "A host cannot be found on GitHub yet: give the directory it is in, as in `::host acme.server ../server`"), linter
                        | setting -> setting |> Result.map HostSetting, linter
                    | "install" -> packageOf directive |> Result.map DependencySetting, linter
                    | "linter" ->
                        match packageOf directive with
                        | Result.Ok (_, AtPath path) ->
                            Result.Error(invalid path.Span "A linter cannot be found by its directory yet: name one the compiler ships, as in `::linter nanyx.lint`"), linter
                        | Result.Ok (_, AtGitHub place) ->
                            Result.Error(invalid place.Span "A linter cannot be found on GitHub: name one the compiler ships, as in `::linter nanyx.lint`"), linter
                        | Result.Ok (package, place) -> Result.Ok(LinterSetting(package, place)), Some package
                        | Result.Error error -> Result.Error error, linter
                    | "lint" ->
                        match directive.Arguments, linter with
                        | [ rule; severity ], Some linter -> valueOf directive severity |> Result.map (fun severity -> LintSetting(linter, rule, severity)), Some linter
                        | [ _; _ ], None -> Result.Error(lintWithoutLinter directive), linter
                        | _ -> Result.Error(invalid directive.Span (usage "lint") |> Diagnostics.about "::lint"), linter
                    | "format" ->
                        match directive.Arguments with
                        | [ key; value ] -> valueOf directive value |> Result.map (fun value -> FormatSetting(key, value)), linter
                        | _ -> Result.Error(invalid directive.Span (usage "format") |> Diagnostics.about "::format"), linter
                    | "" -> Result.Error(invalid directive.Span $"`::` has no directive's name after it: the directives are {directiveList}"), linter
                    | name -> Result.Error(invalid directive.Words.Head.Span $"Nanyx has no directive of this name: the directives are {directiveList}" |> Diagnostics.about $"::{name}"), linter
                let linterAbove = linterAbove || linter.IsSome || (directive.IsOldForm && directive.Name.StartsWith "linters.")
                (directive, setting), (linter, linterAbove))
            (None, false)
        |> fst

    /// The entry the package `package`, found at `place`, is in a manifest, written at `span`, with `fields` after it:
    /// `::install acme.util ../util` is `(package = "acme.util", path = "../util")`. A package the compiler ships,
    /// named with neither, is named at the compiler's version.
    let private entryOf (span: SourceSpan) (package: DirectiveWord) (place: DirectivePlace) (fields: DataField list) : DataValue =
        let field name (word: DirectiveWord) = { Name = name; NameSpan = word.Span; Value = { Data = DataString word.Text; Location = word.Span } }
        let named, where =
            match place with
            | AtVersion version -> field "package" package, [ field "version" version ]
            | AtPath path -> field "package" package, [ field "path" path ]
            | Shipped -> field "package" package, [ field "version" { Text = Toolchain.version; Span = package.Span } ]
            // `github:acme/snippets/retry.nyx@<commit>` is `github = "acme/snippets/retry.nyx", rev = "<commit>"`, and
            // names a module, since it is a file.
            | AtGitHub place ->
                let github, rev, isFile = gitHubPlace place
                field (if isFile then "module" else "package") package,
                [ field "github" { place with Text = github } ] @ (rev |> Option.map (fun rev -> field "rev" { place with Text = rev }) |> Option.toList)
        { Data = DataRecord([ named ] @ where @ fields); Location = span }

    /// The linter options the `::linter` and `::lint` directives of `settings` make, as the manifest's `linters` their
    /// entries stand for would, with what is wrong with them; none when there is no `::linter`.
    let private linters (settings: (Directive * DirectiveSetting) list) : LinterOptions option * Diagnostic list =
        let linters = settings |> List.choose (function directive, LinterSetting(package, place) -> Some(directive, package, place) | _ -> None)
        match linters with
        | [] -> None, []
        | (first, _, _) :: _ ->
            let entry (directive: Directive, package: DirectiveWord, place) =
                let rules =
                    settings
                    |> List.choose (function
                        | _, LintSetting(linter, rule, severity) when linter.Span = package.Span -> Some { Name = rule.Text; NameSpan = rule.Span; Value = severity }
                        | _ -> None)
                let record (fields: DataField list) = { Data = DataRecord fields; Location = directive.Span }
                let settingsField =
                    match rules with
                    | [] -> []
                    | _ -> [ { Name = "settings"; NameSpan = directive.Span; Value = record [ { Name = "rules"; NameSpan = directive.Span; Value = record rules } ] } ]
                entryOf directive.Span package place settingsField
            let options, errors = Manifest.linterEntries { Data = DataList(linters |> List.map entry); Location = first.Span }
            Some options, errors

    /// The newest version of each package that a new resolution could choose, as an index last gave it, and when.
    let private newestFound = Collections.Concurrent.ConcurrentDictionary<string * string, DateTime * SemanticVersion option>()

    /// The newest version of `name` in the index of `store` that a new resolution could choose, if it has one and can be
    /// read. The editor checks a script as it is typed, so what the index gives is kept for five minutes.
    let private newestIn (store: PackageStore) (name: string) : SemanticVersion option =
        match newestFound.TryGetValue((store.Index, name)) with
        | true, (found, newest) when DateTime.UtcNow - found < TimeSpan.FromMinutes 5.0 -> newest
        | _ ->
            let newest = Resolution.newest store name |> Result.toOption |> Option.flatten
            newestFound.[(store.Index, name)] <- (DateTime.UtcNow, newest)
            newest

    /// The fix that writes `version` after `package`, the name a `::install` gives with no version.
    let private useVersion (package: DirectiveWord) (version: SemanticVersion) : Fix =
        let length = package.Span.End - package.Span.Start
        let atEnd = { package.Span with Start = package.Span.End; StartColumn = package.Span.StartColumn + length }
        { Title = $"Use {package.Text} {version}"; Edits = [ { Span = atEnd; NewText = $" {version}" } ] }

    /// What is wrong with the package `package` a `::install` names, found at `place`, as a script names it: a package
    /// from the index needs the exact version the script uses, since a script has no lockfile and the version its
    /// directive writes is its pin. One with no version is the version of it the compiler ships, if it ships one (see
    /// `PackageStore.seededCopy`); one it doesn't is offered the newest version `newest` finds of it.
    let private unpinned (store: PackageStore) (newest: string -> SemanticVersion option) (package: DirectiveWord) (place: DirectivePlace) : Diagnostic list =
        match place with
        | Shipped when (PackageStore.seededCopy store package.Text).IsNone ->
            let newest = newest package.Text
            let example = newest |> Option.map string |> Option.defaultValue "1.2.0"
            let found = match newest with Some version -> $": the newest in the index is {version}" | None -> ""
            let message =
                $"`::install {package.Text}` needs the version of '{package.Text}' to use, as in `::install {package.Text} {example}`, or the directory it is in, as in `../{package.Text.Substring(package.Text.LastIndexOf '.' + 1)}`. A script has no lockfile, so the version it writes is the one it uses{found}"
            let diagnostic = invalid package.Span message |> Diagnostics.about package.Text
            match newest with
            | Some version -> [ { diagnostic with Fixes = [ useVersion package version ] } ]
            | None -> [ diagnostic ]
        // One that isn't a version at all is the manifest's error, as `version = "1.x"` would be.
        | AtVersion version when
            (SemanticVersion.tryParse version.Text).IsNone
            && (VersionRequirement.tryParse version.Text).IsSome
            ->
            [ invalid version.Span $"A script names the exact version of '{package.Text}' it uses, as in `1.2.0`, not the versions it accepts, '{version.Text}': it has no lockfile, so the version it writes is the one it uses" ]
        | _ -> []

    /// The packages from the index among `dependencies`, a script's, and its `host`, each with the exact version its
    /// directive pins.
    let private pinsOf (host: HostEntry option) (dependencies: Dependency list) : (string * SemanticVersion) list =
        (host |> Option.bind (fun host -> host.Version |> Option.map (fun version -> host.Package, version.Minimum)) |> Option.toList)
        @ (dependencies
           |> List.choose (fun dependency ->
               match dependency.Source with
               | FromIndex requirement -> Some(dependency.Name, requirement.Minimum)
               | _ -> None))

    /// The build the `::host` and `::install` directives of `settings` make, if there are any, for the script at
    /// `filePath`, whose imports by path, and the directories its directives give, are relative to its directory, with
    /// the packages they name found in `store`; or what is wrong with them. A script that names no host runs under the
    /// shipped host `defaultHost`; with no directives at all under the console host, it has no build of its own, and
    /// under any other it has one that names `defaultHost`.
    let private build (defaultHost: string) (store: PackageStore) (newest: string -> SemanticVersion option) (filePath: string) (settings: (Directive * DirectiveSetting) list) : Result<PackageModules option, Diagnostic list> =
        let root = if filePath = "" then Directory.GetCurrentDirectory() else Path.GetDirectoryName filePath
        let notShipped (package: DirectiveWord) place =
            match place with
            | Shipped when (PackageStore.seededCopy store package.Text).IsNone ->
                let last = package.Text.Substring(package.Text.LastIndexOf '.' + 1)
                [ invalid package.Span $"The compiler ships no package '{package.Text}': give the directory it is in, as in `../{last}`" ]
            | _ -> []
        let hosts = settings |> List.choose (function directive, HostSetting(package, place) -> Some (directive, package, place) | _ -> None)
        let dependencies = settings |> List.choose (function directive, DependencySetting(package, place) -> Some (directive, package, place) | _ -> None)
        let script = if filePath = "" then "the script" else Path.GetFileName filePath
        match hosts, dependencies with
        | [], [] when defaultHost = Hosts.consolePackage -> Result.Ok None
        | [], [] -> Package.scriptModules defaultHost store Lockfile.empty filePath SourceSpan.none None [] [] |> Result.map Some
        | (first, _, _) :: _, _
        | [], (first, _, _) :: _ ->
            let twice =
                hosts
                |> List.skip (min 1 hosts.Length)
                |> List.map (fun (directive, _, _) -> invalid directive.Span "A script runs under one host, which an earlier `::host` names already: remove this one")
            // A shipped package named by its old name is told its new one (see `RenamedPackages`).
            let renamed (rename: string -> string option) (package: DirectiveWord) (place: DirectivePlace) =
                match place, rename package.Text with
                | (Shipped | AtVersion _), Some message ->
                    [ Diagnostics.at DiagnosticCodes.RenamedPackage package.Span message |> Diagnostics.about package.Text ]
                | _ -> []
            let renamedErrors =
                (hosts |> List.truncate 1 |> List.collect (fun (_, package, place) -> renamed RenamedPackages.asHost package place))
                @ (dependencies |> List.collect (fun (_, package, place) -> renamed RenamedPackages.asDependency package place))
            let unshipped =
                if not renamedErrors.IsEmpty then
                    renamedErrors
                else
                    (hosts |> List.truncate 1 |> List.collect (fun (_, package, place) -> notShipped package place))
                    @ (dependencies |> List.collect (fun (_, package, place) -> unpinned store newest package place))
            let host, hostErrors =
                match hosts with
                | (directive, package, place) :: _ when unshipped.IsEmpty ->
                    match Manifest.hostEntry root (entryOf directive.Span package place []) with
                    | Result.Ok host -> Some host, []
                    | Result.Error errors -> None, errors
                | _ -> None, []
            let dependencies, files, dependencyErrors =
                if unshipped.IsEmpty then
                    Manifest.dependencyEntries root { Data = DataList(dependencies |> List.map (fun (directive, package, place) -> entryOf directive.Span package place [])); Location = first.Span }
                else
                    [], [], []
            let locked, lockErrors =
                match ScriptDependencies.cached store script (pinsOf host dependencies) with
                | Result.Ok locked -> locked, []
                | Result.Error messages -> Lockfile.empty, messages |> List.map (Diagnostics.at DiagnosticCodes.PackageNotDownloaded first.Span)
            match twice @ unshipped @ hostErrors @ dependencyErrors @ lockErrors with
            | [] -> Package.scriptModules defaultHost store locked filePath first.Span host dependencies files |> Result.map Some
            | errors -> Result.Error errors

    /// `readFromUnder`, with the newest version of a package a `::install` names with no version found by `newest`.
    let private readWith (newest: string -> SemanticVersion option) (defaultHost: string) (store: PackageStore) (filePath: string) (source: string) : ScriptDirectives =
        let filePath = if filePath = "" then "" else Path.GetFullPath filePath
        let directives = Directives.read filePath source
        let manifest =
            match filePath with
            | "" -> None
            | _ when not (Directory.Exists(Path.GetDirectoryName filePath)) -> None
            | _ -> Manifest.tryFind (Path.GetDirectoryName filePath) |> Result.defaultValue None
        match manifest with
        | Some _ when directives.IsEmpty -> { Linters = None; Build = None; Diagnostics = [] }
        | Some manifest -> { Linters = None; Build = None; Diagnostics = Package.directiveErrors manifest filePath source }
        | None when directives.IsEmpty && defaultHost = Hosts.consolePackage -> { Linters = None; Build = None; Diagnostics = [] }
        | None ->
            let results = settings directives
            let read = results |> List.choose (function directive, Result.Ok setting -> Some (directive, setting) | _, Result.Error _ -> None)
            let linters, linterErrors = linters read
            let build, buildErrors =
                match build defaultHost store newest filePath read with
                | Result.Ok build -> build, []
                | Result.Error errors -> None, errors
            { Linters = linters
              Build = build
              Diagnostics = (results |> List.choose (function _, Result.Error diagnostic -> Some diagnostic | _, Result.Ok _ -> None)) @ linterErrors @ buildErrors }

    /// The directives at the top of `source`, the text of the file at `filePath` (empty for one not on disk), with the
    /// packages they name found in `store`, for a script that runs under the shipped host `defaultHost` unless it names
    /// another. In a file of a package each is an error, since the package's manifest says these things, and they give
    /// no linters. A package a `::install` names with no version, which the compiler doesn't ship, is offered the newest
    /// version the index of `store` lists.
    let readFromUnder (defaultHost: string) (store: PackageStore) (filePath: string) (source: string) : ScriptDirectives =
        readWith (newestIn store) defaultHost store filePath source

    /// `readFromUnder` for a script that runs under the console host unless it names another, as `nanyx run` runs one.
    let readFrom (store: PackageStore) (filePath: string) (source: string) : ScriptDirectives =
        readFromUnder Hosts.consolePackage store filePath source

    /// `readFrom`, without asking the index anything: a package a `::install` names with no version is told to name one,
    /// with no newest version and no fix. The editor compiles a script as it is typed, and each new spelling of a
    /// package's name would wait for the index (#2223): it offers the fix when asked (see `newestVersionFixes`).
    let readFromWithoutIndex (store: PackageStore) (filePath: string) (source: string) : ScriptDirectives =
        readWith (fun _ -> None) Hosts.consolePackage store filePath source

    /// The fix that writes the newest version the index of `store` lists of each package a `::install` of `source`, the
    /// text of the script at `filePath`, names with no version and the compiler doesn't ship, with the span of the
    /// package's name: what `readFromWithoutIndex` leaves out, for the editor to offer when it is asked for fixes there.
    let newestVersionFixes (store: PackageStore) (filePath: string) (source: string) : (SourceSpan * Fix) list =
        let filePath = if filePath = "" then "" else Path.GetFullPath filePath
        settings (Directives.read filePath source)
        |> List.choose (function
            | _, Result.Ok(DependencySetting(package, Shipped)) when (PackageStore.seededCopy store package.Text).IsNone ->
                newestIn store package.Text |> Option.map (fun version -> package.Span, useVersion package version)
            | _ -> None)

    /// The packages from the index the script at `filePath`, whose text is `source`, pins with its `::host` and
    /// `::install` lines, each at the exact version it names. A directive written wrong is left for the build to report.
    let private directivePins (filePath: string) (source: string) : (string * SemanticVersion) list =
        settings (Directives.read filePath source)
        |> List.choose (function
            | _, Result.Ok(HostSetting(package, AtVersion version))
            | _, Result.Ok(DependencySetting(package, AtVersion version)) ->
                SemanticVersion.tryParse version.Text |> Option.map (fun version -> package.Text, version)
            | _ -> None)

    /// What `nanyx pull` does for the script at `filePath`, whose text is `source`: downloads into the cache of `store`
    /// each package from the index its `::host` and `::install` lines pin, and the packages they depend on (see
    /// `ScriptDependencies.pull`), giving a line for each and a warning for each pin the index has unlisted, or what
    /// went wrong. A directive written wrong is left for the build to report.
    let pull (store: PackageStore) (filePath: string) (source: string) : Result<Pulled, string list> =
        ScriptDependencies.pull store filePath (directivePins filePath source)

    /// What `nanyx run` does before it builds the script at `filePath`, whose text is `source`: `pull`, unless the
    /// cache of `store` holds every package the build uses, when it reads nothing from the index (see
    /// `ScriptDependencies.pullUnlessCached`).
    let pullForRun (store: PackageStore) (filePath: string) (source: string) : Result<Pulled, string list> =
        ScriptDependencies.pullUnlessCached store filePath (directivePins filePath source)

    /// The host the script at `filePath` runs under, as its `::host` names it, found in `store`; none for a script with
    /// no directives, which runs under the console host.
    let hostOf (store: PackageStore) (filePath: string) : Host option =
        (readFrom store filePath (File.ReadAllText filePath)).Build |> Option.bind _.Host

    /// `result` with the problems with `directives` before its own diagnostics. An error in a directive is not
    /// deferred: it stops a debug run too.
    let withDirectives (directives: ScriptDirectives) (result: CompileResult) : CompileResult =
        { result with
            Diagnostics = directives.Diagnostics @ result.Diagnostics
            ErrorsDeferred = result.ErrorsDeferred && not (directives.Diagnostics |> List.exists Diagnostics.isError) }

    /// Compiles `source` as the file at `filePath` with the linters `options` gives, in the build `directives` make, if
    /// they make one: under the host its `::host` names, importing from the packages its `::install` lines name. Otherwise
    /// it is compiled as `Package.compileSourceReading` compiles it. Each module's check is reported to `listener`, and
    /// each other file it needs is read by `readFile`. A source file of a package finds the packages it depends on in
    /// `store`.
    let compileSourceReading
        (store: PackageStore)
        (listener: CompileListener)
        (readFile: string -> string)
        (directives: ScriptDirectives)
        (options: LinterOptions)
        (filePath: string)
        (source: string)
        : CompileResult =
        match directives.Build with
        | Some build -> Compiler.compileSourceInReporting listener readFile build options (Path.GetFullPath filePath) source
        | None -> Package.compileSourceReading store listener readFile options filePath source

    /// Compiles `source` as the script at `filePath`, with the linters its directives turn on (none if they turn none
    /// on), under the host and with the dependencies they name, as `nanyx run` does. A source file of a package is
    /// compiled as one (see `Package.compileSourceWith`). The packages its directives name are found in `store`, and
    /// each module's check is reported to `listener`.
    let compileSourceReportingIn (store: PackageStore) (listener: CompileListener) (filePath: string) (source: string) : CompileResult =
        let directives = readFrom store filePath source
        compileSourceReading store listener File.ReadAllText directives (directives.Linters |> Option.defaultValue Linters.none) filePath source
        |> withDirectives directives

    /// `compileSourceReportingIn`, telling nobody.
    let compileSourceIn (store: PackageStore) (filePath: string) (source: string) : CompileResult =
        compileSourceReportingIn store CompileListener.none filePath source

    /// `compileSourceIn`, for a script that runs under the shipped host `defaultHost` unless its `::host` names another,
    /// with the packages its directives name found in `store`: as the compiler built for the browser compiles a page's
    /// program, under the playground host.
    let compileUnder (defaultHost: string) (store: PackageStore) (filePath: string) (source: string) : CompileResult =
        let directives = readFromUnder defaultHost store filePath source
        compileSourceReading store CompileListener.none File.ReadAllText directives (directives.Linters |> Option.defaultValue Linters.none) filePath source
        |> withDirectives directives

    /// `compileSourceReportingIn` for the file at `filePath` as it is on disk.
    let compileFileReportingIn (store: PackageStore) (listener: CompileListener) (filePath: string) : CompileResult =
        if File.Exists filePath then compileSourceReportingIn store listener filePath (File.ReadAllText filePath) else Compiler.compileFile filePath

    /// `compileFileReportingIn`, with the host the script runs under made what `rehost` makes of it: as
    /// `nanyx run --files-in-memory` builds it under its host's `Host.InMemory`.
    let compileFileRehostedReportingIn (rehost: Host -> Host) (store: PackageStore) (listener: CompileListener) (filePath: string) : CompileResult =
        if File.Exists filePath then
            let source = File.ReadAllText filePath
            let directives = readFrom store filePath source
            let directives = { directives with Build = directives.Build |> Option.map (fun build -> { build with Host = build.Host |> Option.map rehost }) }
            compileSourceReading store listener File.ReadAllText directives (directives.Linters |> Option.defaultValue Linters.none) filePath source
            |> withDirectives directives
        else Compiler.compileFile filePath

    /// `compileFileReportingIn`, telling nobody.
    let compileFileIn (store: PackageStore) (filePath: string) : CompileResult = compileFileReportingIn store CompileListener.none filePath

    /// The test build of the script at `filePath`, whose text is `source`, with the packages its directives name found
    /// in `store` and each module's check reported to `listener`: the compile of the script, under its own host, and, if
    /// it has tests, of a root beside it that imports it by its path (see
    /// `Package.testRootSource`), whose `Typed.Tests` are the script's own and whose diagnostics are the script's too. Or
    /// why the test host can't be read. With `filesInMemory`, the root is built under the test host's root that runs a
    /// `#io` test with its files in memory (see `Host.InMemory`).
    let testBuild (store: PackageStore) (listener: CompileListener) (filesInMemory: bool) (filePath: string) (source: string) : Result<CompileResult * CompileResult option, Diagnostic list> =
        let filePath = Path.GetFullPath filePath
        let directives = readFrom store filePath source
        let own = match directives.Build with Some build -> Result.Ok build | None -> Package.scriptModules Hosts.consolePackage store Lockfile.empty filePath SourceSpan.none None [] []
        own
        |> Result.bind (fun own -> Package.scriptModules Hosts.testPackage store Lockfile.empty filePath SourceSpan.none None [] [] |> Result.map (fun testHost -> own, testHost))
        |> Result.bind (fun (own, testHost) ->
            match testHost.Host with
            | Some host when filesInMemory ->
                match host.InMemory with
                | Some inMemory -> Result.Ok (own, { testHost with Host = Some inMemory })
                | None -> Result.Error [ Diagnostics.general DiagnosticCodes.InvalidManifestValue (Hosts.noFilesInMemory host) ]
            | _ -> Result.Ok (own, testHost))
        |> Result.map (fun (own, testHost) ->
            let root = Path.Combine(Path.GetDirectoryName filePath, Package.testRootFile)
            // The script keeps its own host, whose prelude it sees; only the root runs under the test host.
            let build =
                { own with
                    TestRoot = Some root
                    TestHost = Option.map2 (fun host hostPackage -> host, hostPackage) testHost.Host testHost.HostPackage }
            let options = directives.Linters |> Option.defaultValue Linters.none
            let script = Compiler.compileSourceInReporting listener File.ReadAllText build options filePath source |> withDirectives directives
            let own (tests: Test list) = tests |> List.filter (fun test -> PackageModules.samePath test.Span.File filePath)
            match script.Typed |> Option.map (_.Tests >> own) with
            | Some (_ :: _ as tests) ->
                let imported = $"\"./{Path.GetFileNameWithoutExtension filePath}\""
                let result = Compiler.compileSourceInReporting listener File.ReadAllText build Linters.none root (Package.testRootSource [ imported, tests.Length ])
                script,
                Some
                    { result with
                        // The root imports the script, so it reports the script's errors again, through that import.
                        Diagnostics = script.Diagnostics @ result.Diagnostics |> List.map Diagnostics.inItsModule |> List.distinct
                        ErrorsDeferred = script.ErrorsDeferred && result.ErrorsDeferred
                        Typed = result.Typed |> Option.map (fun typed -> { typed with Tests = own typed.Tests }) }
            | _ -> script, None)
