namespace NyxCompiler

open System
open System.IO
open System.Text.RegularExpressions
open Parser.Program
open Parser.Program.DataValues

/// A package a build uses, as `nanyx.lock` records it: its exact version, and the SHA-256 of the `.nyxpkg` it was
/// downloaded as, which the cache checks a download against. Every locked package comes from the package index.
type LockedPackage =
    { Name: string
      Version: SemanticVersion
      /// The SHA-256 of its `.nyxpkg`, in lower-case hexadecimal.
      Sha256: string
      /// The packages it depends on, by name, each of them locked too.
      Dependencies: string list }

/// `nanyx.lock`, beside a package's manifest: every package its build uses that is named by a version, with the
/// version it uses. A build has one version of each package, so a name is locked once.
type Lockfile =
    { /// Sorted by name.
      Packages: LockedPackage list }

module Lockfile =
    /// The lockfile's name, beside the manifest.
    let fileName = "nanyx.lock"

    /// The version of the lockfile's format, which its `version` field gives.
    let formatVersion = 1

    let empty = { Packages = [] }

    /// The lockfile of the package whose manifest is in `root`.
    let pathIn (root: string) = Path.Combine(root, fileName)

    /// The locked package called `name`, if there is one.
    let tryFind (name: string) (lockfile: Lockfile) = lockfile.Packages |> List.tryFind (fun locked -> locked.Name = name)

    /// `lockfile` with `locked` in it, in place of any package of the same name.
    let add (locked: LockedPackage) (lockfile: Lockfile) =
        { Packages = locked :: (lockfile.Packages |> List.filter (fun other -> other.Name <> locked.Name)) |> List.sortBy _.Name }

    let private quoted (text: string) = "\"" + text + "\""

    /// `lockfile` as it is written: a Nanyx record, one package to a line, sorted by name, so that two branches that
    /// lock different packages rarely conflict.
    let write (lockfile: Lockfile) : string =
        let entry (locked: LockedPackage) =
            let dependencies = locked.Dependencies |> List.sort |> List.map quoted |> String.concat ", "
            $"    (package = {quoted locked.Name}, version = {quoted (string locked.Version)}, sha256 = {quoted locked.Sha256}, dependencies = [{dependencies}])"
        let packages =
            match lockfile.Packages |> List.sortBy _.Name with
            | [] -> "  packages = []"
            | packages -> "  packages = [\n" + (packages |> List.map entry |> String.concat "\n") + "\n  ]"
        $"-- Written by nanyx, which keeps it up to date: don't edit it by hand.\n(\n  version = {formatVersion}\n{packages}\n)\n"

    let private sha256Pattern = Regex "^[0-9a-f]{64}$"

    /// The lockfile at `path`, whose text is `text`, or what is wrong with it, each as a line naming where.
    let check (path: string) (text: string) : Result<Lockfile, string list> =
        let at (span: SourceSpan) (message: string) = $"{path}:{span.StartLine + 1}:{span.StartColumn + 1}: {message}"
        let errors = Collections.Generic.List<string>()
        let text' name (fields: DataField list) (location: SourceSpan) =
            match DataValue.field name fields with
            | Some { DataField.Value = { DataValue.Data = DataString value } } -> Some value
            | Some field ->
                errors.Add(at field.Value.Location $"a locked package's {name} must be a string, not {DataValue.describe field.Value}")
                None
            | None ->
                errors.Add(at location $"a locked package has no {name}")
                None
        let package (value: DataValue) =
            match value.Data with
            | DataRecord fields ->
                let name = text' "package" fields value.Location
                let version =
                    text' "version" fields value.Location
                    |> Option.bind (fun written ->
                        let parsed = SemanticVersion.tryParse written
                        if parsed.IsNone then errors.Add(at value.Location $"the locked version \"{written}\" is not a semantic version")
                        parsed)
                let sha256 =
                    text' "sha256" fields value.Location
                    |> Option.filter (fun hash ->
                        let valid = sha256Pattern.IsMatch hash
                        if not valid then errors.Add(at value.Location $"the locked sha256 \"{hash}\" is not 64 hexadecimal digits in lower case")
                        valid)
                let dependencies =
                    match DataValue.field "dependencies" fields with
                    | Some { DataField.Value = { DataValue.Data = DataList items } } ->
                        items |> List.choose (fun item -> match item.Data with DataString name -> Some name | _ -> None)
                    | _ -> []
                match name, version, sha256 with
                | Some name, Some version, Some sha256 -> Some { Name = name; Version = version; Sha256 = sha256; Dependencies = dependencies }
                | _ -> None
            | _ ->
                errors.Add(at value.Location $"a locked package must be a record, not {DataValue.describe value}")
                None
        match DataValue.read path text with
        | Result.Error _ -> Result.Error [ $"{path} is not a lockfile nanyx can read: delete it, and nanyx pull writes it again" ]
        | Result.Ok { Data = DataRecord fields; Location = location } ->
            match DataValue.field "version" fields with
            | Some { DataField.Value = { DataValue.Data = DataInt version } } when version = bigint formatVersion -> ()
            | Some field -> errors.Add(at field.Value.Location $"the lockfile's format is {DataValue.describe field.Value}, but this nanyx reads format {formatVersion}")
            | None -> errors.Add(at location "the lockfile has no version")
            let packages =
                match DataValue.field "packages" fields with
                | Some { DataField.Value = { DataValue.Data = DataList items } } -> items |> List.choose package
                | _ -> []
            for name, count in packages |> List.countBy _.Name do
                if count > 1 then errors.Add $"{path}: {name} is locked {count} times, but a build has one version of each package"
            if errors.Count = 0 then Result.Ok { Packages = packages |> List.sortBy _.Name } else Result.Error(List.ofSeq errors)
        | Result.Ok value -> Result.Error [ at value.Location $"a lockfile is a record, not {DataValue.describe value}" ]

    /// The lockfile at `path`, if there is one, or what is wrong with it.
    let read (path: string) : Result<Lockfile option, string list> =
        if File.Exists path then check path (File.ReadAllText path) |> Result.map Some else Result.Ok None
