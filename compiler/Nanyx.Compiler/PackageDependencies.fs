namespace NyxCompiler

open System.IO
open Parser.Program
open Parser.Program.DataValues

/// A dependency as a manifest's `dependencies` writes it: its package, and its version or the directory it is in.
type DependencyEntry =
    { Name: string
      Version: string option
      Path: string option
      Location: SourceSpan }

/// A lockfile chosen anew from the index: what it locks, a line for each package it locks anew, and a warning
/// (`NYX0051`) for each version it keeps that the index has unlisted.
type Relocked =
    { Lockfile: Lockfile
      Lines: string list
      Warnings: Diagnostic list }

/// What `nanyx install` makes: the manifest's new text, and the lockfile that locks the package it added, if that one
/// is named by its version and the lockfile didn't lock it already.
type Installed =
    { Manifest: string
      Lockfile: Relocked option }

/// Why the lockfile can't be brought up to date.
type LockFailure =
    /// No versions satisfy every dependency (`NYX0047`).
    | NoVersions of Diagnostic
    /// Something else went wrong: each line says what.
    | LockErrors of string list

/// `nanyx pull` and `nanyx install`, and a build bringing `nanyx.lock` up to date: making a package's dependencies
/// available, and adding one to its manifest. A dependency named by its version is the version `nanyx.lock` locks,
/// chosen from the package index when the manifest asks for something the lockfile doesn't satisfy (`Resolution`), and
/// downloaded into the cache.
module PackageDependencies =
    /// The manifest at `manifestPath`, read as data, with its text, line breaks `\n`; or why it can't be.
    let private readData (manifestPath: string) : Result<string * DataField list, string> =
        let text = Manifest.normalized (File.ReadAllText manifestPath)
        match DataValue.read manifestPath text with
        | Result.Ok { Data = DataRecord fields } -> Result.Ok(text, fields)
        | _ -> Result.Error $"'{manifestPath}' is not a manifest: run `nanyx check` on it to see why"

    /// Each entry of the manifest's `dependencies` (`fields` are its top level), as it is written.
    let entries (fields: DataField list) : DependencyEntry list =
        let text name (fields: DataField list) =
            match DataValue.field name fields with
            | Some { DataField.Value = { Data = DataString text } } -> Some text
            | _ -> None
        match DataValue.field "dependencies" fields with
        | Some { DataField.Value = { Data = DataList items } } ->
            items
            |> List.choose (fun item ->
                match item.Data with
                | DataRecord entry ->
                    text "package" entry
                    |> Option.map (fun name -> { Name = name; Version = text "version" entry; Path = text "path" entry; Location = item.Location })
                | _ -> None)
        | _ -> []

    /// Each package of the build, depended on directly or through others, once, in the order a build reaches them.
    let rec private reached (packages: Package list) : Package list =
        packages |> List.collect (fun package -> package :: reached package.Dependencies) |> List.distinctBy _.Manifest.Path

    /// The lines that say how `after` differs from `before`: a package locked anew, at another version, or no longer. A
    /// package the compiler ships, locked anew at the version it seeded into `store`, goes without saying, so that a
    /// package's first build doesn't list the host and the packages it brings.
    let private changes (store: PackageStore) (before: Lockfile) (after: Lockfile) : string list =
        let locked =
            after.Packages
            |> List.choose (fun package ->
                match Lockfile.tryFind package.Name before with
                | Some earlier when earlier.Version = package.Version -> None
                | None when PackageStore.seededCopy store package.Name |> Option.exists (fun copy -> copy.Version = package.Version) -> None
                | Some earlier -> Some $"Locked {package.Name} {package.Version}, in place of {earlier.Version}"
                | None -> Some $"Locked {package.Name} {package.Version}")
        let unlocked =
            before.Packages
            |> List.filter (fun package -> (Lockfile.tryFind package.Name after).IsNone)
            |> List.map (fun package -> $"Unlocked {package.Name} {package.Version}, which the build no longer needs")
        locked @ unlocked

    /// The lockfile `manifest` needs, if `lockfile` doesn't satisfy what it asks for, chosen from the index of `store`;
    /// none when `lockfile` satisfies it. With `frozen`, as `nanyx build --locked` asks, a lockfile that doesn't satisfy
    /// the manifest is an error, naming what it lacks.
    let private relock (store: PackageStore) (manifest: Manifest) (lockfile: Lockfile) (frozen: bool) : Result<Relocked option, LockFailure> =
        match Resolution.unsatisfied (Resolution.requirements store manifest) lockfile with
        | [] -> Result.Ok None
        | missing when frozen ->
            Result.Error(
                LockErrors(
                    $"'{Lockfile.fileName}' doesn't satisfy what '{Path.GetFileName manifest.Path}' asks for, and `--locked` keeps it as it is: run `nanyx pull` to update it"
                    :: missing
                )
            )
        | _ ->
            match Resolution.resolve store manifest lockfile with
            | Result.Ok resolved ->
                Result.Ok(
                    Some
                        { Lockfile = resolved.Lockfile
                          Lines = changes store lockfile resolved.Lockfile
                          Warnings = (resolved.Unlisted |> List.map (fun (name, version) -> Resolution.lockedUnlisted name version)) @ resolved.Warnings }
                )
            | Result.Error(Conflict diagnostic) -> Result.Error(NoVersions diagnostic)
            | Result.Error(IndexUnreadable message) -> Result.Error(LockErrors [ message ])

    /// Downloads into the cache of `store` each package `lockfile` locks that isn't there yet, with a warning for each
    /// of those the index has unlisted.
    let private download (store: PackageStore) (lockfile: Lockfile) : Result<Diagnostic list, LockFailure> =
        let fetched = lockfile.Packages |> List.map (fun locked -> locked, PackageIndex.fetch store locked)
        match fetched |> List.choose (function _, Result.Error message -> Some message | _, Result.Ok _ -> None) with
        | [] ->
            fetched
            |> List.choose (function
                | locked, Result.Ok(_, Some indexed) when indexed.Unlisted -> Some(Resolution.lockedUnlisted locked.Name locked.Version)
                | _ -> None)
            |> Result.Ok
        | failed -> Result.Error(LockErrors failed)

    /// Brings the lockfile of the package whose manifest is at `manifestPath` up to date, as a build does before it reads
    /// the lockfile: when the manifest asks for something the lockfile doesn't satisfy (a new dependency, a changed
    /// requirement), chooses versions again from the index of `store`, keeping those still allowed, writes the lockfile and
    /// downloads what it locks. Gives a line for each package it locks anew, none when it changes nothing, and a warning
    /// for each version it keeps that the index has unlisted, and for each stale copy of a package the compiler ships that
    /// seeding the store replaced (see `PackageIndex.shippedReplaced`). With `frozen`, as `--locked` asks, a lockfile that doesn't
    /// satisfy the manifest is an error. A manifest or lockfile that doesn't read is left for the build to report.
    let update (store: PackageStore) (manifestPath: string) (frozen: bool) : Result<Pulled, LockFailure> =
        let replaced = PackageIndex.shippedReplaced store
        let unchanged = { Lines = []; Warnings = replaced }
        match Manifest.read manifestPath, Lockfile.read (Lockfile.pathIn (Path.GetDirectoryName(Path.GetFullPath manifestPath))) with
        | Result.Ok manifest, Result.Ok lockfile ->
            relock store manifest (defaultArg lockfile Lockfile.empty) frozen
            |> Result.bind (function
                | None -> Result.Ok unchanged
                | Some relocked ->
                    File.WriteAllText(Lockfile.pathIn manifest.Root, Lockfile.write relocked.Lockfile)
                    download store relocked.Lockfile
                    |> Result.map (fun warnings ->
                        { Lines = relocked.Lines
                          Warnings = List.distinct (replaced @ relocked.Warnings @ warnings) }))
        | _ -> Result.Ok unchanged

    /// Pulls the dependencies of the package whose manifest is at `manifestPath`, from `store`: brings its lockfile up to
    /// date (see `update`), downloads into the cache each package `nanyx.lock` locks that isn't there yet, checking each
    /// against its SHA-256, then checks that every dependency is there. Gives a line for each package it locks anew, then
    /// one for each dependency, as `acme.util 0.3.0 (../util)` or `acme.web 1.2.0 (downloaded)`. Or what is wrong: no
    /// versions that satisfy every dependency, a download that fails, or anything a build would find wrong with the
    /// dependencies. With them is a warning for each version it locks that the index has unlisted, when it read the index
    /// for it.
    let pull (store: PackageStore) (manifestPath: string) : Result<Pulled, LockFailure> =
        PackageIndex.reading (fun () ->
            let root = Path.GetDirectoryName(Path.GetFullPath manifestPath)
            let before = Lockfile.read (Lockfile.pathIn root) |> Result.map (Option.defaultValue Lockfile.empty) |> Result.defaultValue Lockfile.empty
            let wasCached = before.Packages |> List.filter (fun locked -> Directory.Exists(PackageStore.directoryOf store locked.Name locked.Version))
            update store manifestPath false
            |> Result.bind (fun locked ->
                Lockfile.read (Lockfile.pathIn root)
                |> Result.map (Option.defaultValue Lockfile.empty)
                |> Result.mapError LockErrors
                |> Result.bind (fun lockfile -> download store lockfile)
                |> Result.bind (fun warnings -> Package.ofManifestFrom store manifestPath |> Result.mapError LockErrors |> Result.map (fun package -> package, warnings))
                |> Result.map (fun (package, warnings) ->
                    let root = package.Manifest.Root
                    let listed =
                        match reached package.Dependencies with
                        | [] -> [ $"{package.Manifest.Name} has no dependencies" ]
                        | dependencies ->
                            dependencies
                            |> List.map (fun dependency ->
                                let name, version = dependency.Manifest.Name, dependency.Manifest.Version
                                let where =
                                    if PackageStore.seededCopy store name |> Option.exists (fun copy -> PackageModules.samePath copy.Source dependency.Manifest.Root) then
                                        "shipped with the compiler"
                                    elif PackageModules.samePath dependency.Manifest.Root (PackageStore.directoryOf store name (SemanticVersion.tryParse version).Value) then
                                        if wasCached |> List.exists (fun cached -> cached.Name = name && string cached.Version = version) then "in the cache" else "downloaded"
                                    else
                                        PackageModules.relativePath root dependency.Manifest.Root
                                $"{name} {version} ({where})")
                    { Lines = locked.Lines @ listed
                      Warnings = List.distinct (locked.Warnings @ warnings) })))

    /// The name of the package whose manifest is in `directory`, or why there is none.
    let private packageIn (directory: string) : Result<string, string> =
        Manifest.manifestIn directory
        |> Result.bind (fun path ->
            match Manifest.read path with
            | Result.Ok manifest -> Result.Ok manifest.Name
            | Result.Error errors -> Result.Error(errors |> List.map Manifest.describe |> String.concat "\n"))

    /// `text` with `entry` added to the end of the manifest's `dependencies` (`fields` are its top level, read from
    /// `text`), laid out as the list is: on a line of its own when the last entry is, and after a comma when not. A
    /// manifest without `dependencies` gets one after its `package`.
    let private withEntry (text: string) (fields: DataField list) (entry: string) =
        let insert (at: int) (inserted: string) = text.Substring(0, at) + inserted + text.Substring at
        let lineStart (offset: int) = text.LastIndexOf('\n', max 0 (offset - 1)) + 1
        let endsLine (offset: int) =
            let lineEnd = match text.IndexOf('\n', offset) with -1 -> text.Length | found -> found
            text.Substring(offset, lineEnd - offset).Trim() |> fun rest -> rest = "" || rest.StartsWith "--"
        match DataValue.field "dependencies" fields, DataValue.field "package" fields with
        | Some { DataField.Value = { Data = DataList [] } as list }, _ ->
            text.Substring(0, list.Location.Start) + $"[{entry}]" + text.Substring list.Location.End
        | Some { DataField.Value = { Data = DataList items } }, _ ->
            let last = List.last items
            if endsLine last.Location.End then
                let indent = text.Substring(lineStart last.Location.Start, last.Location.Start - lineStart last.Location.Start)
                insert last.Location.End ("\n" + indent + entry)
            else
                insert last.Location.End (", " + entry)
        | _, Some package when endsLine package.Value.Location.End ->
            let indent = text.Substring(lineStart package.NameSpan.Start, package.NameSpan.Start - lineStart package.NameSpan.Start)
            insert package.Value.Location.End ("\n" + indent + $"dependencies = [{entry}]")
        | _, Some package -> insert package.Value.Location.End $", dependencies = [{entry}]"
        | _, None -> text

    /// The lockfile the manifest `text`, the new text of the one at `manifestPath`, needs, chosen from the index of
    /// `store`; none if the lockfile beside it satisfies it already.
    let private lockFor (store: PackageStore) (manifestPath: string) (text: string) : Result<Relocked option, LockFailure> =
        match Manifest.check manifestPath text, Lockfile.read (Lockfile.pathIn (Path.GetDirectoryName manifestPath)) with
        | Result.Ok manifest, Result.Ok lockfile -> relock store manifest (defaultArg lockfile Lockfile.empty) false
        | Result.Error problems, _ -> Result.Error(LockErrors(problems |> List.map Manifest.describe))
        | _, Result.Error messages -> Result.Error(LockErrors messages)

    /// Where `nanyx install` finds the package `name`: in `directory` (a full path), which must hold a package of that
    /// name; at `version`; or else at the newest version `store` lists that can be chosen (see `Resolution.newest`),
    /// written as that version, `1.4.2`, which accepts the versions compatible with it.
    let private sourceOf (store: PackageStore) (name: string) (version: string option) (directory: string option) : Result<DependencySource, string> =
        match directory, version with
        | Some _, Some _ -> Result.Error "`--path` and `--version` were both given: give a dependency's directory or its version, not both"
        | Some directory, None ->
            packageIn directory
            |> Result.bind (fun found ->
                if found <> name then
                    Result.Error $"The package in '{directory}' is named '{found}', not '{name}': make the names match"
                else
                    Result.Ok(DependencySource.AtPath directory))
        | None, Some version ->
            match VersionRequirement.tryParse version with
            | Some requirement -> Result.Ok(FromIndex requirement)
            | None -> Result.Error $"'{version}' is not a version, which must be {VersionRequirement.rule}"
        | None, None ->
            Resolution.newest store name
            |> Result.bind (function
                | Some newest -> Result.Ok(FromIndex(VersionRequirement.at newest))
                | None -> Result.Error $"The index at '{store.Index}' has no version of '{name}' to install: check its name, or give the directory it is in with `--path`")

    /// Adds the package `name` to the dependencies of the package whose manifest is at `manifestPath`, found as
    /// `sourceOf` says, from `store`. A package the compiler ships is named at the compiler's `toolVersion`. Gives the
    /// manifest's new text and, for a package from the index, the lockfile that locks it; or why it can't be added:
    /// a name the manifest refuses, one already listed, a directory that holds another package, or a version no
    /// resolution can choose.
    let install (toolVersion: string) (store: PackageStore) (manifestPath: string) (name: string) (version: string option) (directory: string option) : Result<Installed, LockFailure> =
        if not (Manifest.namePattern.IsMatch name) then
            Result.Error(LockErrors [ $"""The package name '{name}' must be {Manifest.nameRule}""" ])
        else
            readData manifestPath
            |> Result.bind (fun (text, fields) ->
                match entries fields |> List.tryFind (fun entry -> entry.Name = name) with
                | Some existing ->
                    let written = text.Substring(existing.Location.Start, existing.Location.End - existing.Location.Start)
                    Result.Error $"'{name}' is already a dependency, at line {existing.Location.StartLine + 1} of '{Path.GetFileName manifestPath}': `{written}`"
                | None -> sourceOf store name version directory |> Result.map (fun source -> text, fields, source))
            |> Result.mapError (List.singleton >> LockErrors)
            |> Result.bind (fun (text, fields, source) ->
                let entry = Manifest.entryText (Path.GetDirectoryName manifestPath) name source
                let manifest = withEntry text fields entry
                match source with
                | FromIndex _ ->
                    lockFor store manifestPath manifest |> Result.map (fun lockfile -> { Manifest = manifest; Lockfile = lockfile })
                | _ -> Result.Ok { Manifest = manifest; Lockfile = None })
