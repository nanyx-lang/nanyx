namespace NyxCompiler

open System.Collections.Concurrent
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
                        { Package = name; Module = root; Prelude = text host preludeField; Directory = directory; Settings = settings; Component = built })
                | _ -> None
            | _ -> None
        | _ -> None

    /// The error for naming the package `name` as a host when its manifest doesn't declare it one.
    let notAHost (name: string) =
        $"'{name}' is not a host: a host's manifest declares that it is one, with the module that exports '{Hosts.runFunction}', as `package = (..., {field} = ({rootField} = \"<module>\"))`"

    let private shipped = ConcurrentDictionary<string, Result<Host, string>>()

    /// The host the compiler ships as the package `name`, or why there is none. Its manifest is `<name>.nyxp` in its
    /// directory (see `ShippedPackages`), and never changes while the process runs, so it is read once.
    let tryShipped (name: string) : Result<Host, string> =
        shipped.GetOrAdd(name, fun name ->
            let directory = ShippedPackages.directoryOf name
            let path = Path.Combine(directory, name + ".nyxp")
            if not (File.Exists path) then
                Error $"The compiler ships no package '{name}'"
            else
                match DataValue.read path (File.ReadAllText path) with
                | Ok manifest -> ofManifest name directory manifest |> Option.map Ok |> Option.defaultValue (Error (notAHost name))
                | Error _ -> Error $"The manifest of '{name}', which the compiler ships, can't be read")

    /// The console host the compiler ships, which a script runs under.
    let console : Lazy<Host> =
        lazy
            (match tryShipped Hosts.consolePackage with
             | Ok host -> host
             | Error message -> raise (InvalidDataException $"The compiler is not installed correctly: {message}"))
