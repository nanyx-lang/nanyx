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

/// What `nanyx install` makes: the manifest's new text, and the package it added if that one has to be downloaded,
/// which can't be done yet.
type Installed = { Manifest: string; ToDownload: (string * string) option }

/// `nanyx pull` and `nanyx install`: making a package's dependencies available, and adding one to its manifest. A
/// dependency named by its version is the version `nanyx.lock` locks, which `pull` downloads into the cache; choosing a
/// version to lock is to come ([#864](https://github.com/nanyx-lang/nanyx/issues/864)), and both commands say so for
/// one the lockfile doesn't lock.
module PackageDependencies =
    let private isShipped (name: string) = Directory.Exists(ShippedPackages.directoryOf name)

    let private normalize (text: string) = text.Replace("\r\n", "\n").Replace("\r", "\n")

    /// The manifest at `manifestPath`, read as data, with its text, line breaks `\n`; or why it can't be.
    let private readData (manifestPath: string) : Result<string * DataField list, string> =
        let text = normalize (File.ReadAllText manifestPath)
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

    /// Whether `entry` names a package to download: by its version, and not one the compiler ships.
    let private needsDownload (entry: DependencyEntry) = entry.Path.IsNone && entry.Version.IsSome && not (isShipped entry.Name)

    /// What can't be had yet, for `nanyx pull` and `nanyx install`: a package named by its version that the lockfile
    /// doesn't lock.
    let private cantDownload (name: string) (version: string) =
        $"'{name}' {version} is named by its version, but it isn't locked in '{Lockfile.fileName}', and choosing a version to lock isn't supported yet: give the directory it is in, with `path`"

    /// Each package of the build, depended on directly or through others, once, in the order a build reaches them.
    let rec private reached (packages: Package list) : Package list =
        packages |> List.collect (fun package -> package :: reached package.Dependencies) |> List.distinctBy _.Manifest.Path

    /// Pulls the dependencies of the package whose manifest is at `manifestPath`, from `store`: downloads into the cache
    /// each package `nanyx.lock` locks that isn't there yet, checking each against its SHA-256, then checks that every
    /// dependency is there, and gives a line for each, as `acme.util 0.3.0 (../util)` or `acme.web 1.2.0 (downloaded)`.
    /// A package named by its version that the lockfile doesn't lock is an error, since choosing a version isn't
    /// supported yet; so is a download that fails, and anything a build would find wrong with the dependencies.
    let pull (store: PackageStore) (manifestPath: string) : Result<string list, string list> =
        readData manifestPath
        |> Result.mapError List.singleton
        |> Result.bind (fun (_, fields) ->
            Lockfile.read (Lockfile.pathIn (Path.GetDirectoryName(Path.GetFullPath manifestPath)))
            |> Result.map (Option.defaultValue Lockfile.empty)
            |> Result.bind (fun lockfile ->
                match entries fields |> List.filter (fun entry -> needsDownload entry && (Lockfile.tryFind entry.Name lockfile).IsNone) with
                | _ :: _ as unlocked -> Result.Error(unlocked |> List.map (fun entry -> cantDownload entry.Name entry.Version.Value))
                | [] ->
                    let wasCached = lockfile.Packages |> List.filter (fun locked -> Directory.Exists(PackageStore.directoryOf store locked.Name locked.Version))
                    let downloads = lockfile.Packages |> List.map (PackageIndex.download store)
                    match downloads |> List.choose (function Result.Error message -> Some message | Result.Ok _ -> None) with
                    | _ :: _ as failed -> Result.Error failed
                    | [] ->
                        Package.ofManifestFrom store manifestPath
                        |> Result.map (fun package ->
                            let root = package.Manifest.Root
                            match reached package.Dependencies with
                            | [] -> [ $"{package.Manifest.Name} has no dependencies" ]
                            | dependencies ->
                                dependencies
                                |> List.map (fun dependency ->
                                    let name, version = dependency.Manifest.Name, dependency.Manifest.Version
                                    let where =
                                        if isShipped name && dependency.Manifest.Root = ShippedPackages.directoryOf name then
                                            "shipped with the compiler"
                                        elif PackageModules.samePath dependency.Manifest.Root (PackageStore.directoryOf store name (SemanticVersion.tryParse version).Value) then
                                            if wasCached |> List.exists (fun locked -> locked.Name = name) then "in the cache" else "downloaded"
                                        else
                                            Path.GetRelativePath(root, dependency.Manifest.Root).Replace('\\', '/')
                                    $"{name} {version} ({where})"))))

    /// The name of the package whose manifest is in `directory`, or why there is none.
    let private packageIn (directory: string) : Result<string, string> =
        if not (Directory.Exists directory) then
            Result.Error $"'{directory}' is not a directory"
        else
            match Directory.GetFiles(directory, "*" + Manifest.extension) |> Array.sort with
            | [||] -> Result.Error $"'{directory}' holds no package, since it has no {Manifest.extension} manifest"
            | manifests ->
                match Manifest.read manifests[0] with
                | Result.Ok manifest -> Result.Ok manifest.Name
                | Result.Error errors -> Result.Error(errors |> List.map Manifest.describe |> String.concat "\n")

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

    /// Adds the package `name` to the dependencies of the package whose manifest is at `manifestPath`: found in
    /// `directory` (a full path), which must hold a package of that name, or at `version`. A package the compiler ships
    /// is named at the compiler's `toolVersion` when no version is given. Gives the manifest's new text; or why it can't
    /// be added: a name the manifest refuses, one already listed, or neither a directory nor a version to find it by.
    let install (toolVersion: string) (manifestPath: string) (name: string) (version: string option) (directory: string option) : Result<Installed, string> =
        if not (Manifest.namePattern.IsMatch name) then
            Result.Error $"""The package name '{name}' must be {Manifest.nameRule}"""
        else
            readData manifestPath
            |> Result.bind (fun (text, fields) ->
                match entries fields |> List.tryFind (fun entry -> entry.Name = name) with
                | Some existing ->
                    let written = text.Substring(existing.Location.Start, existing.Location.End - existing.Location.Start)
                    Result.Error $"'{name}' is already a dependency, at line {existing.Location.StartLine + 1} of '{Path.GetFileName manifestPath}': `{written}`"
                | None ->
                    let added =
                        match directory, version with
                        | Some _, Some _ -> Result.Error "`--path` and `--version` were both given: give a dependency's directory or its version, not both"
                        | Some directory, None ->
                            packageIn directory
                            |> Result.bind (fun found ->
                                if found <> name then
                                    Result.Error $"The package in '{directory}' is named '{found}', not '{name}': make the names match"
                                else
                                    let path = Path.GetRelativePath(Path.GetDirectoryName manifestPath, directory).Replace('\\', '/')
                                    Result.Ok($"(package = \"{name}\", path = \"{path}\")", None))
                        | None, Some version -> Result.Ok($"(package = \"{name}\", version = \"{version}\")", (if isShipped name then None else Some (name, version)))
                        | None, None when isShipped name -> Result.Ok($"(package = \"{name}\", version = \"{toolVersion}\")", None)
                        | None, None -> Result.Error $"Neither `--path` nor `--version` was given: give the directory '{name}' is in with `--path`, or its version with `--version`"
                    added |> Result.map (fun (entry, download) -> { Manifest = withEntry text fields entry; ToDownload = download }))

    /// What `nanyx install` says of the package `name` it added at `version`, which has to be downloaded.
    let notDownloaded (name: string, version: string) = cantDownload name version
