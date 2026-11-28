namespace NyxCompiler

open System
open System.Collections.Concurrent
open System.IO
open Parser.Program.DataValues

/// The packages the compiler ships, so that a program needs nothing installed: source directories beside the compiler,
/// each named for its package (`packages/nanyx.cli.host`), with its manifest and its source files under `src`, at the
/// compiler's version (see `Toolchain.version`), which a manifest names it at exactly. They come from packages/ at the
/// repository root, which every build of the compiler copies beside it, and the published `nanyx` has beside it in its
/// archive. The console host is one (see `Hosts`), and its interface package, `nanyx.cli`, another.
module ShippedPackages =
    /// Where they are: beside the compiler, with its directory's long name, however the compiler was started (see
    /// `LongPaths`), so that a path to one of them is spelt one way.
    let directory = Path.Combine(LongPaths.ofPath AppContext.BaseDirectory, "packages")

    /// Where the package `name` would be if the compiler shipped it.
    let directoryOf (name: string) = Path.Combine(directory, name)

    /// The names of the packages the compiler ships, in order.
    let names () : string list =
        if Directory.Exists directory then Directory.GetDirectories directory |> Array.map Path.GetFileName |> Array.sort |> List.ofArray else []

    /// A fingerprint of the files of every package the compiler ships, each path and its text with LF line endings: what
    /// the packages it seeds into the package cache are packed from (see `PackageStore.shippedIndex`), so that a compiler
    /// built from other sources seeds its own. The first 16 hexadecimal digits of their SHA-256.
    let fingerprint: Lazy<string> =
        lazy
            (use hash = System.Security.Cryptography.IncrementalHash.CreateHash System.Security.Cryptography.HashAlgorithmName.SHA256
             for name in names () do
                 let root = directoryOf name
                 for file in Directory.GetFiles(root, "*", SearchOption.AllDirectories) |> Array.map (fun file -> Path.GetRelativePath(root, file).Replace('\\', '/')) |> Array.sort do
                     let text = File.ReadAllText(Path.Combine(root, file)).Replace("\r\n", "\n")
                     hash.AppendData(Text.Encoding.UTF8.GetBytes $"{name}/{file}\n{text.Length}\n{text}")
             Convert.ToHexString(hash.GetHashAndReset()).Substring(0, 16).ToLowerInvariant())

    let private modules = ConcurrentDictionary<string, PackageModules option>()

    /// The manifest of the shipped package `name`, as data.
    let private manifestOf (name: string) =
        let path = Path.Combine(directoryOf name, name + ".nyxp")
        DataValue.read path (File.ReadAllText path)

    /// The packages the shipped package `name` depends on, as its manifest lists them: a shipped package depends only on
    /// others the compiler ships, by name. The manifest is checked as any other is (see `Manifest`) by the tests of the
    /// packages the compiler ships.
    let private dependenciesOf (name: string) : string list =
        match manifestOf name with
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
                Some
                    { PackageModules.ofFiles name (directoryOf name) None files with
                        Dependencies = dependenciesOf name |> List.choose tryModules
                        HostLibrary = manifestOf name |> Result.map HostLibraries.declares |> Result.defaultValue false }
            else
                None)

    /// The file at `path` as messages name it, if it is in a package the compiler ships: after the package's name, with
    /// forward slashes, as `nanyx.cli.host/src/cli.host.nyx`, wherever the compiler is installed.
    let tryDisplay (path: string) : string option =
        if String.IsNullOrEmpty path || not (Path.IsPathRooted path) then
            None
        else
            let relative = Path.GetRelativePath(directory, path)
            if relative.StartsWith ".." || Path.IsPathRooted relative then None else Some(relative.Replace('\\', '/'))
