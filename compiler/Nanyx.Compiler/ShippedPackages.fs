namespace NyxCompiler

open System
open System.Collections.Concurrent
open System.IO
open Parser.Program.DataValues

/// The packages the compiler ships, so that a program needs nothing installed: source directories beside the compiler,
/// each named for its package (`packages/nanyx.console`), with its manifest and its source files under `src`, at the
/// compiler's version (see `Toolchain.version`), which a manifest names it at exactly. They come from packages/ at the
/// repository root, which every build of the compiler copies beside it, and the published `nanyx` has beside it in its
/// archive. The console host is one (see `Hosts`), and its interface package, `nanyx.console.core`, another.
module ShippedPackages =
    /// Where they are: beside the compiler.
    let directory = Path.Combine(AppContext.BaseDirectory, "packages")

    /// Where the package `name` would be if the compiler shipped it.
    let directoryOf (name: string) = Path.Combine(directory, name)

    /// The names of the packages the compiler ships, in order.
    let names () : string list =
        if Directory.Exists directory then Directory.GetDirectories directory |> Array.map Path.GetFileName |> Array.sort |> List.ofArray else []

    let private modules = ConcurrentDictionary<string, PackageModules option>()

    /// The packages the shipped package `name` depends on, as its manifest lists them: a shipped package depends only on
    /// others the compiler ships, by name. The manifest is checked as any other is (see `Manifest`) by the tests of the
    /// packages the compiler ships.
    let private dependenciesOf (name: string) : string list =
        let path = Path.Combine(directoryOf name, name + ".nyxp")
        match DataValue.read path (File.ReadAllText path) with
        | Ok { Data = DataRecord top } ->
            match DataValue.field "dependencies" top with
            | Some { DataField.Value = { Data = DataList entries } } ->
                entries
                |> List.choose (function
                    | { Data = DataRecord fields } ->
                        match DataValue.field "package" fields with
                        | Some { DataField.Value = { Data = DataString dependency } } -> Some dependency
                        | _ -> None
                    | _ -> None)
            | _ -> []
        | _ -> []

    /// The modules of the shipped package `name`, by the names its source files declare, with its public modules and the
    /// shipped packages it depends on, if the compiler ships it. They never change while the process runs, so they are
    /// read once. A shipped package has no `@entry` of its own.
    let rec tryModules (name: string) : PackageModules option =
        modules.GetOrAdd(name, fun name ->
            let sources = Path.Combine(directoryOf name, "src")
            if Directory.Exists sources then
                let files = Directory.GetFiles(sources, "*.nyx", SearchOption.AllDirectories) |> Array.sort |> List.ofArray
                Some { PackageModules.ofFiles name (directoryOf name) None files with Dependencies = dependenciesOf name |> List.choose tryModules }
            else
                None)

    /// The file at `path` as messages name it, if it is in a package the compiler ships: after the package's name, with
    /// forward slashes, as `nanyx.console/src/console.host.nyx`, wherever the compiler is installed.
    let tryDisplay (path: string) : string option =
        if String.IsNullOrEmpty path || not (Path.IsPathRooted path) then
            None
        else
            let relative = Path.GetRelativePath(directory, path)
            if relative.StartsWith ".." || Path.IsPathRooted relative then None else Some(relative.Replace('\\', '/'))
