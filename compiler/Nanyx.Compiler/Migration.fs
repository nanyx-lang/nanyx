namespace NyxCompiler

open System
open System.Globalization
open System.IO
open Parser.Program
open Parser.Program.DataValues

/// What `nanyx migrate` makes of a script, or a directory of scripts: the package's manifest and each file rewritten.
type Migration =
    { /// The full path of the manifest it writes.
      ManifestPath: string
      /// The manifest's text.
      Manifest: string
      /// Each file migrated, by its full path, with its new text.
      Files: (string * string) list
      /// What the package can't do yet, to tell whoever migrated it.
      Notes: string list }

/// `nanyx migrate`: turns a script, or every script under a directory, into a package. Each `::` directive becomes the
/// manifest entry it stands for, and is removed from its file. A file with top-level statements becomes `module main`,
/// its statements, and the values that need a context and so run with them (see `TypedModule.ScriptEntryValues`), moved
/// into an `@entry` def in the order they are written; the manifest gets the host it runs under. Any other file becomes
/// a module named for the file, which a library, a package with no entry, exports. An import of one migrated file by
/// another's path becomes an import of its module.
module Migration =
    /// A file being migrated: its full path, its text with line breaks `\n`, its directives, and it as parsed.
    type private Script =
        { Path: string
          Source: string
          Directives: Directive list
          Syntax: Module
          ModuleBlocks: (ModuleName * SourceSpan) list }

    let private normalize (text: string) = text.Replace("\r\n", "\n").Replace("\r", "\n")

    /// `path`, relative to `root`, with forward slashes, as a manifest writes it.
    let private relative (root: string) (path: string) = Path.GetRelativePath(root, path).Replace('\\', '/')

    /// A directive as an error names it: its file, relative to `root`, its line and its text.
    let private located (root: string) (script: Script) (directive: Directive) =
        let text = script.Source.Substring(directive.Span.Start, directive.Span.End - directive.Span.Start)
        $"{relative root script.Path}:{directive.Span.StartLine + 1}: {text}"

    let private quoted (text: string) = "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

    /// A value a directive sets, written as the manifest writes it.
    let private dataText (value: DataValue) : string =
        match value.Data with
        | DataString text -> quoted text
        | DataInt n -> string n
        | DataFloat n -> n.ToString("R", CultureInfo.InvariantCulture)
        | DataBool flag -> if flag then "true" else "false"
        | DataTag(name, _) -> "#" + name
        | _ -> DataValue.describe value

    /// The entry the package a `::host`, `::dependency` or `::linter` directive names is in the manifest:
    /// `(package = "<name>", path = "<directory>")`, its directory, relative to the script, made relative to the package's
    /// `root`. A package the compiler ships, named with neither, is at the compiler's version, `toolVersion`. One on
    /// GitHub is `(package = "<name>", github = "<repository>", rev = "<commit>")`, and a file on GitHub the same with
    /// `module` for `package`.
    let private packageEntry (toolVersion: string) (scriptDirectory: string) (root: string) (package: DirectiveWord) (place: DirectivePlace) =
        match place with
        | AtGitHub place ->
            let github, rev, isFile = Script.gitHubPlace place
            let rev = rev |> Option.map (fun rev -> $", rev = {quoted rev}") |> Option.defaultValue ""
            $"""({(if isFile then "module" else "package")} = {quoted package.Text}, github = {quoted github}{rev})"""
        | _ ->
            let where =
                match place with
                | AtVersion version -> $"version = {quoted version.Text}"
                | AtPath path -> $"path = {quoted (relative root (Path.GetFullPath(path.Text, scriptDirectory)))}"
                | Shipped
                | AtGitHub _ -> $"version = {quoted toolVersion}"
            $"(package = {quoted package.Text}, {where})"

    /// What a directive says, as the manifest writes it: the key it sets, which two files must agree on, and the value.
    type private Setting =
        | LinterOn of linter: string
        | RuleSeverity of linter: string * rule: string * severity: string
        | FormatterSetting of key: string * value: string
        | HostEntry of entry: string
        | DependencyEntry of name: string * entry: string

    let private settingKey (setting: Setting) =
        match setting with
        | LinterOn linter -> $"linter {linter}"
        | RuleSeverity(linter, rule, _) -> $"linter {linter}: lint {rule}"
        | FormatterSetting(key, _) -> $"format {key}"
        | HostEntry _ -> "host"
        | DependencyEntry(name, _) -> $"dependency {name}"

    /// What `setting`, one of `script`'s, says in the manifest. `Script.read` has checked it.
    let private settingOf (toolVersion: string) (root: string) (script: Script) (setting: DirectiveSetting) : Setting =
        let scriptDirectory = Path.GetDirectoryName script.Path
        match setting with
        | LinterSetting(package, _) -> LinterOn package.Text
        | LintSetting(linter, rule, severity) -> RuleSeverity(linter.Text, rule.Text, dataText severity)
        | FormatSetting(key, value) -> FormatterSetting(key.Text, dataText value)
        | HostSetting(package, place) -> HostEntry(packageEntry toolVersion scriptDirectory root package place)
        | DependencySetting(package, place) -> DependencyEntry(package.Text, packageEntry toolVersion scriptDirectory root package place)

    /// Each setting the scripts' directives make, once, with the first directive that makes it; or an error for each key
    /// two directives set to different values, naming both, and for the second `::host` that names another host.
    let private settings (toolVersion: string) (root: string) (scripts: Script list) : Result<Setting list, string list> =
        let made =
            scripts
            |> List.collect (fun script ->
                Script.settings script.Directives
                |> List.choose (function
                    | directive, Result.Ok setting -> Some (settingOf toolVersion root script setting, located root script directive)
                    | _, Result.Error _ -> None))
        let byKey = made |> List.groupBy (fst >> settingKey)
        let conflicts =
            byKey
            |> List.choose (fun (_, settings) ->
                match settings |> List.distinctBy fst with
                | (_, first) :: (_, second) :: _ -> Some $"{first} and {second} disagree: a package has one manifest, so make them say the same, then migrate again"
                | _ -> None)
        match conflicts with
        | [] -> Result.Ok(byKey |> List.map (fun (_, settings) -> fst settings.Head))
        | errors -> Result.Error errors

    /// The manifest of the package `name`, whose sources are `sources`, made of `settings`. A package with an entry runs
    /// under the console host at the compiler's version, `toolVersion`, unless a `::host` names another.
    let private manifestText (toolVersion: string) (name: string) (sources: string list) (hasEntry: bool) (settings: Setting list) =
        let quotedSources = sources |> List.map (sprintf "\"%s\"") |> String.concat ", "
        let linters =
            settings
            |> List.choose (function LinterOn linter -> Some linter | RuleSeverity(linter, _, _) -> Some linter | _ -> None)
            |> List.distinct
            |> List.map (fun package ->
                let rules =
                    settings
                    |> List.choose (function RuleSeverity(linter, rule, severity) when linter = package -> Some $"{rule} = {severity}" | _ -> None)
                match rules with
                | [] -> $"(package = \"{package}\", version = \"{toolVersion}\")"
                | rules -> $"""(package = "{package}", version = "{toolVersion}", settings = (rules = ({String.concat ", " rules})))""")
        let host =
            settings
            |> List.tryPick (function HostEntry entry -> Some entry | _ -> None)
            |> Option.orElse (if hasEntry then Some $"(package = \"{Hosts.consolePackage}\", version = \"{toolVersion}\")" else None)
        let dependencies = settings |> List.choose (function DependencyEntry(_, entry) -> Some entry | _ -> None)
        let formatter = settings |> List.choose (function FormatterSetting(key, value) -> Some $"{key} = {value}" | _ -> None)
        [ yield "("
          yield $"  package = (name = \"{name}\", version = \"0.1.0\", sources = [{quotedSources}])"
          if not linters.IsEmpty then yield $"""  linters = [{String.concat ", " linters}]"""
          match host with
          | Some host -> yield $"  host = {host}"
          | None -> ()
          if not dependencies.IsEmpty then yield $"""  dependencies = [{String.concat ", " dependencies}]"""
          if not formatter.IsEmpty then yield $"""  formatter = ({String.concat ", " formatter})"""
          yield ")" ]
        |> String.concat "\n"
        |> fun text -> text + "\n"

    /// Whether `item` is a top-level statement of a script.
    let private isStatement (item: TopLevelItem) =
        match item.Kind with
        | Expr _ -> true
        | _ -> false

    /// The parts of `script` its text is cut into: each top-level item, a module block as a whole, by span, in order.
    /// The items in a block are the block's.
    let private units (script: Script) : TopLevelItem list =
        let inBlock (span: SourceSpan) =
            script.ModuleBlocks |> List.exists (fun (_, block) -> block.Start <= span.Start && span.End <= block.End && block <> span)
        let blocks = script.ModuleBlocks |> List.map (fun (name, span) -> TopLevelItem.at span (ModuleBlock(name, [])))
        (script.Syntax |> List.filter (fun item -> not (SourceSpan.isNone item.Span) && not (inBlock item.Span)))
        |> List.filter (fun item -> not (script.ModuleBlocks |> List.exists (fun (_, block) -> block.Start = item.Span.Start)))
        |> fun items -> items @ blocks
        |> List.distinctBy (fun item -> item.Span.Start, item.Span.End)
        |> List.sortBy (fun item -> item.Span.Start)

    /// `text` with each line that isn't blank indented by `indent`.
    let private indented (indent: string) (text: string) =
        text.Split '\n' |> Array.map (fun line -> if String.IsNullOrWhiteSpace line then "" else indent + line) |> String.concat "\n"

    /// `text` without the blank lines at its start and end.
    let private trimmedLines (text: string) =
        let lines = text.Split '\n' |> Array.toList
        lines
        |> List.skipWhile String.IsNullOrWhiteSpace
        |> List.rev
        |> List.skipWhile String.IsNullOrWhiteSpace
        |> List.rev
        |> String.concat "\n"

    /// The module name the migrated file at `path` declares: the one it declares already, or its file's name.
    let private moduleNameOf (script: Script) =
        match FileImports.declaration script.ModuleBlocks script.Syntax with
        | Some (name, _) -> name
        | None -> Path.GetFileNameWithoutExtension script.Path

    /// `script`'s text as a package's file: its `#!` line and directives gone, its module declared (exported in a
    /// library, `exports`), and each import of another migrated file by path (`modules`, by full path, with the module it
    /// declares) made an import of that module. An entry script (`entry`, with the values that run in its entry point and
    /// whether its last statement gives `()`) is `module main`, with its statements in an `@entry` def.
    let private rewrite (exports: bool) (modules: Map<string, string>) (entry: (Set<string> * bool) option) (script: Script) : string =
        let source = script.Source
        let declared = FileImports.declaration script.ModuleBlocks script.Syntax
        let declaration =
            match declared, entry with
            | Some _, _ -> None
            | None, Some _ -> Some "module main"
            | None, None -> Some ((if exports then "export module " else "module ") + moduleNameOf script)
        let units = units script
        let textBetween start finish = source.Substring(start, finish - start)
        // An import of a migrated file by its path names the file's module.
        let written (item: TopLevelItem) =
            let text = textBetween item.Span.Start item.Span.End
            match item.Kind with
            | Import items ->
                items
                |> List.fold (fun (text: string) (import: ImportItem) ->
                    match import.Source with
                    | PathImport path ->
                        match FileImports.resolvePath (Path.GetDirectoryName script.Path) import with
                        | Result.Ok file when modules.ContainsKey(Path.GetFullPath file) ->
                            let quoted = $"\"{path}\""
                            let at = text.IndexOf quoted
                            if at < 0 then text else text.Substring(0, at) + modules[Path.GetFullPath file] + text.Substring(at + quoted.Length)
                        | _ -> text
                    | ModuleImport _ -> text) text
            | ModuleDecl(false, _) when exports -> "export " + text
            | _ -> text
        // What comes before the first item: comments, without the `#!` line and the directives.
        let firstStart = match units with first :: _ -> first.Span.Start | [] -> source.Length
        let header =
            textBetween 0 firstStart
            |> fun text -> text.Split '\n'
            |> Array.filter (fun line -> not (line.StartsWith "::") && not (line.StartsWith "#!"))
            |> String.concat "\n"
            |> trimmedLines
        // Each unit with the comments and blank lines before it, after the item before it.
        let pieces =
            units
            |> List.mapi (fun index item ->
                let start = if index = 0 then item.Span.Start else units[index - 1].Span.End
                let leading = textBetween start item.Span.Start
                item, leading + written item)
        let trailing = match units with [] -> "" | _ -> trimmedLines (textBetween (List.last units).Span.End source.Length)
        let moves (item: TopLevelItem) =
            match entry, item.Kind with
            | Some _, Expr _ -> true
            | Some (values, _), Def (ValueDef(_, name, _, _)) -> values.Contains name
            | _ -> false
        let kept = pieces |> List.filter (fst >> moves >> not) |> List.map snd |> String.concat ""
        let body =
            match entry with
            | None -> ""
            | Some (_, endsWithUnit) ->
                let moved = pieces |> List.filter (fst >> moves)
                let lastIsStatement = moved |> List.tryLast |> Option.exists (fst >> isStatement)
                let statements = moved |> List.map snd |> String.concat "" |> trimmedLines
                let ending = if lastIsStatement && endsWithUnit then "" else "\n()"
                "\n\n@entry\ndef main = {\n" + indented "  " (statements + ending) + "\n}"
        [ Option.toList declaration |> String.concat ""; header; trimmedLines (kept + body); trailing ]
        |> List.filter (String.IsNullOrWhiteSpace >> not)
        |> String.concat "\n\n"
        |> fun text -> text + "\n"

    /// The scripts `target` names: a `.nyx` file, or every `.nyx` file under a directory, with the directory the package
    /// is made in; or why it can't be migrated.
    let private scriptsIn (target: string) : Result<string * string list, string list> =
        let full = Path.GetFullPath target
        if File.Exists full then
            Result.Ok(Path.GetDirectoryName full, [ full ])
        elif Directory.Exists full then
            match Directory.GetFiles(full, "*.nyx", SearchOption.AllDirectories) |> Array.sort |> List.ofArray with
            | [] -> Result.Error [ $"'{full}' has no .nyx files to migrate" ]
            | files -> Result.Ok(full, files)
        else
            Result.Error [ $"'{target}' is neither a .nyx file nor a directory" ]

    /// Reads and parses the script at `path`, or says why it can't be migrated: it doesn't parse, or a directive is
    /// wrong. What a directive names that can't be had yet, a package to download or a directory that isn't there, is
    /// written to the manifest all the same, to be reported when the package is built.
    let private readScript (root: string) (path: string) : Result<Script, string list> =
        let source = normalize (File.ReadAllText path)
        let refused = [ DiagnosticCodes.InvalidDirective; DiagnosticCodes.OldFormDirective; DiagnosticCodes.LintWithoutLinter ] |> List.map _.Code
        let wrong = (Script.read path source).Diagnostics |> List.filter (fun problem -> List.contains problem.Code refused)
        match wrong, parseModuleFileWithNames path source with
        | [], Result.Ok (syntax, _, moduleBlocks) ->
            Result.Ok { Path = path; Source = source; Directives = Directives.read path source; Syntax = syntax; ModuleBlocks = moduleBlocks }
        | [], Result.Error _ -> Result.Error [ $"'{relative root path}' doesn't parse: fix it, then migrate again" ]
        | problems, _ -> Result.Error(problems |> List.map (fun problem -> $"{relative root path}:{(problem.Span |> Option.map _.StartLine |> Option.defaultValue 0) + 1}: {problem.Message}"))

    /// The values of the entry script at `path` that need a context, so run in its entry point, and whether its last
    /// statement gives `()`, as checking it finds them. A script that doesn't check moves only its statements.
    let private entryOf (script: Script) : Set<string> * bool =
        let result = Script.compileSource script.Path script.Source
        match result.Typed, result.Syntax with
        | Some typed, Some syntax ->
            // The compile parsed the file itself, so its nodes are its own.
            let lastStatement = syntax |> List.filter isStatement |> List.tryLast
            let givesUnit =
                lastStatement
                |> Option.bind (function Kind (Expr expr) -> typed.ExprTypes |> Map.tryFind expr.Id | _ -> None)
                |> Option.exists (fun ty -> Unifier.unalias ty = TyPrimitive "unit")
            typed.ScriptEntryValues, givesUnit
        | _ -> Set.empty, false

    /// What migrating `target`, a script or a directory of scripts, makes, for the package called `name` (or the
    /// directory's name), with the compiler's release `toolVersion`; or why it can't: the directory is a package already,
    /// a name the manifest refuses, two scripts with top-level statements, a file that doesn't parse, a directive that is
    /// wrong, or two that disagree. Nothing is written.
    let migrate (toolVersion: string) (target: string) (name: string option) : Result<Migration, string list> =
        scriptsIn target
        |> Result.bind (fun (root, paths) ->
            let existing = Directory.GetFiles(root, "*" + Manifest.extension) |> Array.sort
            let packageName = name |> Option.defaultValue (Path.GetFileName root)
            if existing.Length > 0 then
                Result.Error [ $"'{root}' is already a package, with the manifest '{Path.GetFileName existing[0]}'" ]
            elif not (Manifest.namePattern.IsMatch packageName) then
                match name with
                | Some _ -> Result.Error [ $"""The package name '{packageName}' must be {Manifest.nameRule}""" ]
                | None ->
                    Result.Error [ $"""The directory's name '{packageName}' is not a package name, which must be {Manifest.nameRule}: give one with `--name`""" ]
            else
                let read = paths |> List.map (readScript root)
                match read |> List.collect (function Result.Error errors -> errors | Result.Ok _ -> []) with
                | _ :: _ as errors -> Result.Error errors
                | [] ->
                    let scripts = read |> List.choose (function Result.Ok script -> Some script | Result.Error _ -> None)
                    let withStatements = scripts |> List.filter (fun script -> script.Syntax |> List.exists isStatement)
                    match withStatements with
                    | _ :: _ :: _ ->
                        let names = withStatements |> List.map (fun script -> $"'{relative root script.Path}'") |> Diagnostics.listed
                        Result.Error
                            [ $"The scripts {names} each have top-level statements, but a package runs from one `@entry`: migrate them one at a time, with `nanyx migrate <file>`, or make all but one of them modules first" ]
                    | entryScripts ->
                        settings toolVersion root scripts
                        |> Result.bind (fun settings ->
                            let entryScript = List.tryHead entryScripts
                            let isEntry (script: Script) = entryScript |> Option.exists (fun entry -> entry.Path = script.Path)
                            let modules = scripts |> List.map (fun script -> script.Path, (if isEntry script then "main" else moduleNameOf script))
                            match modules |> List.groupBy snd |> List.filter (fun (_, files) -> files.Length > 1) with
                            | (moduleName, files) :: _ ->
                                let names = files |> List.map (fun (path, _) -> $"'{relative root path}'") |> Diagnostics.listed
                                Result.Error [ $"The files {names} would each be the module '{moduleName}', but a package has one module of each name: rename all but one first" ]
                            | [] ->
                                let exports = entryScript.IsNone
                                let moduleMap = Map.ofList modules
                                let files =
                                    scripts
                                    |> List.map (fun script ->
                                        let entry = if isEntry script then Some (entryOf script) else None
                                        script.Path, rewrite exports moduleMap entry script)
                                let sources = if paths.Length = 1 && File.Exists(Path.GetFullPath target) then [ relative root paths.Head ] else [ "**/*.nyx" ]
                                let downloads =
                                    settings
                                    |> List.choose (function
                                        | DependencyEntry(dependency, entry) when entry.Contains "version =" && not (Directory.Exists(ShippedPackages.directoryOf dependency)) ->
                                            Some dependency
                                        | _ -> None)
                                Result.Ok
                                    { ManifestPath = Path.Combine(root, packageName + Manifest.extension)
                                      Manifest = manifestText toolVersion packageName sources entryScript.IsSome settings
                                      Files = files
                                      Notes =
                                        downloads
                                        |> List.map (fun dependency ->
                                            $"'{dependency}' is named by its version, but packages can't be downloaded yet, so the package won't build until they can: give the directory it is in instead, with `path`") }))
