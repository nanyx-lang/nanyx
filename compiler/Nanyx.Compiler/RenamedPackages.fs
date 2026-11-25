/// The packages the compiler ships that were renamed when every host came to be named `<name>.host` and the
/// library a host runs programs against took the short name (#2697), by their old names: a manifest, a script or
/// `--host` that names one is an error that gives the new name (`NYX0054`), and an import of one of its modules by
/// the old name says so too (`NYX0003`). `nanyx.http` is a name again, the library's,
/// so it is renamed only where a host is named.
module internal NyxCompiler.RenamedPackages

/// The new name of each renamed package, by its old name, and whether it is a host.
let private renamed =
    [ "nanyx.console.core", ("nanyx.cli", false)
      "nanyx.console", ("nanyx.cli.host", true)
      "nanyx.http.core", ("nanyx.http", false)
      "nanyx.playground", ("nanyx.playground.host", true) ]
    |> Map.ofList

/// What the HTTP host was named, which is now the library's name.
let private oldHttpHost = "nanyx.http"

/// The message for a host named `name`, if it is a host's or a package's old name: what it is called now.
let asHost (name: string) : string option =
    if name = oldHttpHost then
        Some $"The HTTP host is now 'nanyx.http.host': '{oldHttpHost}' is the library it runs programs against"
    else
        renamed
        |> Map.tryFind name
        |> Option.map (fun (now, isHost) ->
            if isHost then $"The host '{name}' is now '{now}'"
            else $"'{name}' is now '{now}', a library, not a host")

/// The message for a dependency on `name`, if it is a renamed package's old name: what it is called now.
let asDependency (name: string) : string option =
    renamed
    |> Map.tryFind name
    |> Option.map (fun (now, isHost) ->
        if isHost then $"'{name}' is now '{now}', a host: a program names its host in its manifest's `host`, not as a dependency"
        else $"'{name}' is now '{now}'")

/// The error for an import of the module path `path` whose package is a renamed package's old name, as
/// `nanyx.console.core/env` is, if it is one: what the package is called now, and the import that names it so.
let inImport (path: string) : string option =
    match path.IndexOf '/' with
    | -1 -> None
    | slash ->
        let package, moduleName = path.Substring(0, slash), path.Substring(slash + 1)
        renamed
        |> Map.tryFind package
        |> Option.map (fun (now, isHost) ->
            if isHost then $"'{package}' is now '{now}', a host: a program sees its host's prelude, and imports the library it runs programs against"
            else $"'{package}' is now '{now}': import `{now}/{moduleName}`")
