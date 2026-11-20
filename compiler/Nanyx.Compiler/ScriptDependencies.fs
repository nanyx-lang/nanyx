namespace NyxCompiler

open System.IO

/// The packages from the index a script depends on. A script has no lockfile: each `::dependency` that names a package
/// from the index writes the exact version it uses, `::dependency nanyx.json 0.1.0`, and that version is its pin. The
/// packages its pins depend on are chosen as a package's are (see `Resolution`). `nanyx run` and `nanyx pull` on a
/// script download them into the cache (`pull`), and a build reads them from the cache alone (`cached`), as a
/// package's build reads what its lockfile locks.
module ScriptDependencies =
    let private isShipped (name: string) = Directory.Exists(ShippedPackages.directoryOf name)

    /// Each of `pins`, a package and the exact version the script `script` (its path, for messages) names, and the
    /// packages they depend on, chosen from the index of `store` and downloaded into its cache, each checked against the
    /// SHA-256 the index records: a line for each, `nanyx.json 0.1.0`, or what went wrong.
    let pull (store: PackageStore) (script: string) (pins: (string * SemanticVersion) list) : Result<string list, string list> =
        match pins with
        | [] -> Result.Ok []
        | _ ->
            match Resolution.resolvePinned store (Path.GetFileName script) pins with
            | Result.Error(Conflict diagnostic) -> Result.Error [ diagnostic.Message ]
            | Result.Error(IndexUnreadable message) -> Result.Error [ message ]
            | Result.Ok lockfile ->
                match lockfile.Packages |> List.map (PackageIndex.download store) |> List.choose (function Result.Error message -> Some message | Result.Ok _ -> None) with
                | [] -> Result.Ok(lockfile.Packages |> List.map (fun locked -> $"{locked.Name} {locked.Version}"))
                | errors -> Result.Error errors

    /// The versions of `name` in the cache of `store`, as the solver reads them: each with the dependencies its manifest
    /// names by their version. None for a package the compiler ships.
    let private cachedVersions (store: PackageStore) (name: string) : Result<SolverVersion list option, string> =
        if isShipped name then
            Result.Ok None
        else
            let directory = Path.Combine(store.Cache, name)
            let versions =
                if Directory.Exists directory then
                    Directory.GetDirectories directory
                    |> Array.choose (fun versionDirectory ->
                        SemanticVersion.tryParse (Path.GetFileName versionDirectory)
                        |> Option.bind (fun version ->
                            match Manifest.manifestIn versionDirectory |> Result.bind (Manifest.read >> Result.mapError (fun _ -> "")) with
                            | Result.Ok manifest ->
                                Some
                                    { Version = version
                                      Dependencies =
                                        manifest.Dependencies
                                        |> List.choose (fun dependency ->
                                            match dependency.Source with
                                            | FromIndex requirement -> Some(dependency.Name, requirement)
                                            | _ -> None)
                                      Unusable = None }
                            | Result.Error _ -> None))
                    |> List.ofArray
                else
                    []
            Result.Ok(Some versions)

    /// The versions a build of the script `script` (its name, for messages) uses: each of `pins` at the version it gives,
    /// and the packages they depend on, chosen from those in the cache of `store`, which `pull` fills, as a lockfile
    /// records them. Or why they can't be: a pin, or a package it needs, isn't in the cache.
    let cached (store: PackageStore) (script: string) (pins: (string * SemanticVersion) list) : Result<Lockfile, string list> =
        let pullHint = $"`nanyx run` downloads it, as `nanyx pull {script}` does"
        let missing =
            pins
            |> List.filter (fun (name, version) -> not (Directory.Exists(PackageStore.directoryOf store name version)))
            |> List.map (fun (name, version) -> $"'{name}' {version} isn't in the package cache, '{store.Cache}': {pullHint}")
        match pins, missing with
        | [], _ -> Result.Ok Lockfile.empty
        | _, _ :: _ -> Result.Error missing
        | _, [] ->
            let pinned = Map.ofList pins
            let versions (name: string) =
                cachedVersions store name
                |> Result.map (Option.map (fun versions ->
                    match pinned.TryFind name with
                    | Some version -> versions |> List.filter (fun cached -> cached.Version = version)
                    | None -> versions))
            let input =
                { Root = script
                  Requirements =
                    pins
                    |> List.map (fun (name, version) -> { From = script; Package = name; Requirement = { Minimum = version; Written = string version } })
                  Versions = versions
                  Locked = pinned }
            match VersionSolver.solve input with
            | Solved chosen ->
                Result.Ok
                    { Packages =
                        chosen
                        |> List.map (fun (name, version) ->
                            { Name = name
                              Version = version
                              // A build reads only the version; the download was checked when it was pulled.
                              Sha256 = ""
                              Dependencies = [] }) }
            | NoSolution lines -> Result.Error [ String.concat "\n" lines + $"\nThe package cache may not hold every package the script needs: {pullHint}" ]
            | IndexFailed message -> Result.Error [ message ]
