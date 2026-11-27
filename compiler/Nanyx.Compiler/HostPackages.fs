namespace NyxCompiler

open System.IO
open Parser.Program.DataValues

/// Packages that are hosts. A host package says so in its own manifest, in its `package`'s `host` field:
/// `host = (root = "cli.host", prelude = "cli.prelude")` names its root module, which exports `run`, and the
/// module whose exports every module of a program it runs sees without importing them, if it has one, and
/// `settings = (port = 3000)`, if it takes any, the settings a program may give it, with their defaults.
/// `Manifest.check` checks the field; this reads it. A program names its host in its own manifest's `host` (see
/// `Manifest.HostEntry`).
module HostPackages =
    /// The field of a package's `package` that declares it a host.
    let field = "host"

    /// The fields of a host's declaration: its root module, and its prelude.
    let rootField = "root"

    let preludeField = "prelude"

    /// The field of a host's declaration that names its component root and the world it is written against:
    /// `component = (root = "cli.host.wasi", world = "nanyx:console/command", library = "nanyx:console/library")`
    /// (see `HostComponent`).
    let componentField = "component"

    let worldField = "world"

    /// The field of a host's `component` that names the world a library component is built in:
    /// `library = "nanyx:console/library"`.
    let libraryField = "library"

    /// The field of a host's declaration that names the root whose `run` runs the entry point with its files in memory:
    /// `filesInMemory = "cli.host.memory"` (see `Host.InMemory`).
    let filesInMemoryField = "filesInMemory"

    /// The field of a host's declaration that gives the settings it takes, each with its default:
    /// `settings = (port = 3000)`.
    let settingsField = "settings"

    let private text (fields: DataField list) (name: string) =
        match DataValue.field name fields with
        | Some { DataField.Value = { DataValue.Data = DataString text } } -> Some text
        | _ -> None

    /// The host the package `name` in `directory`, whose manifest is `manifest`, declares it is, if it does.
    let ofManifest (name: string) (directory: string) (manifest: DataValue) : Host option =
        match manifest.Data with
        | DataRecord top ->
            match DataValue.field "package" top with
            | Some { DataField.Value = { DataValue.Data = DataRecord package } } ->
                match DataValue.field field package with
                | Some { DataField.Value = { DataValue.Data = DataRecord host } } ->
                    text host rootField
                    |> Option.map (fun root ->
                        let settings =
                            match DataValue.field settingsField host with
                            | Some { DataField.Value = { DataValue.Data = DataRecord settings } } -> settings
                            | _ -> []
                        let built =
                            match DataValue.field componentField host with
                            | Some { DataField.Value = { DataValue.Data = DataRecord built } } ->
                                Option.map2 (fun componentRoot world -> { Root = componentRoot; World = world; Library = text built libraryField; CoreRoot = root }) (text built rootField) (text built worldField)
                            | _ -> None
                        { Package = name; Module = root; Prelude = text host preludeField; Directory = directory; Settings = settings; Component = built; FilesInMemory = text host filesInMemoryField })
                | _ -> None
            | _ -> None
        | _ -> None

    /// The error for naming the package `name` as a host when its manifest doesn't declare it one.
    let notAHost (name: string) =
        $"'{name}' is not a host: a host's manifest declares that it is one, with the module that exports '{Hosts.runFunction}', as `package = (..., {field} = ({rootField} = \"<module>\"))`"

    /// The console host, as the compiler's own copy of it declares it: what a loose file, which has no package store, runs
    /// under, and what a package and a script run under when they name no host (see `Package` for the version of it a
    /// package resolves). Its manifest never changes while the process runs, so it is read once.
    let console : Lazy<Host> =
        lazy
            (let directory = ShippedPackages.directoryOf Hosts.consolePackage
             let path = Path.Combine(directory, Hosts.consolePackage + ".nyxp")
             let declared =
                 if File.Exists path then
                     match DataValue.read path (File.ReadAllText path) with
                     | Ok manifest -> ofManifest Hosts.consolePackage directory manifest
                     | Error _ -> None
                 else
                     None
             match declared with
             | Some host -> host
             | None -> raise (InvalidDataException $"The compiler is not installed correctly: its console host, '{path}', can't be read"))
