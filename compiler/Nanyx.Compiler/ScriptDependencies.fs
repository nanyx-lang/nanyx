namespace NyxCompiler

open System.IO

/// The packages from the index a script depends on. A script has no lockfile: each `::install` that names a package
/// from the index writes the exact version it uses, `::install nanyx.json 0.1.0`, and that version is its pin. The
/// packages its pins depend on are chosen as a package's are (see `Resolution`). `nanyx run` and `nanyx pull` on a
/// script download them into the cache (`pull`), and a build reads them from the cache alone (`cached`), as a
/// package's build reads what its lockfile locks.
module ScriptDependencies =
    /// Each of `pins`, a package and the exact version the script `script` (its path, for messages) names, and the
    /// packages they depend on, chosen from the index of `store` and downloaded into its cache, each checked against the
    /// SHA-256 the index records: a line for each, `nanyx.json 0.1.0`, and a warning for each pin the index has
    /// unlisted, which it still downloads; or what went wrong.
    let pull (store: PackageStore) (script: string) (pins: (string * SemanticVersion) list) : Result<Pulled, string list> =
        match pins with
        | [] -> Result.Ok { Lines = []; Warnings = [] }
        | _ ->
            let name = Path.GetFileName script
            match Resolution.resolvePinned store name pins with
            | Result.Error(Conflict diagnostic) -> Result.Error [ diagnostic.Message ]
            | Result.Error(IndexUnreadable message) -> Result.Error [ message ]
            | Result.Ok resolved ->
                let packages = resolved.Lockfile.Packages
                match packages |> List.map (PackageIndex.download store) |> List.choose (function Result.Error message -> Some message | Result.Ok _ -> None) with
                | [] ->
                    Result.Ok
                        { Lines = packages |> List.map (fun locked -> $"{locked.Name} {locked.Version}")
                          Warnings = resolved.Unlisted |> List.map (fun (package, version) -> Resolution.pinnedUnlisted name package version) }
                | errors -> Result.Error errors

    /// The versions of `name` in the cache of `store`, as the solver reads them: each with the dependencies its manifest
    /// names by their version. None for a package the compiler ships.
    let private cachedVersions (store: PackageStore) (name: string) : Result<SolverVersion list option, string> =
        if ShippedPackages.isShipped name then
            Result.Ok None
        else
            let directory = Path.Combine(store.Cache, name)
            let versions =
                if Directory.Exists directory then
                    Directory.GetDirectories directory
                    |> Array.choose (fun versionDirectory ->
                        SemanticVersion.tryParse (Path.GetFileName versionDirectory)
                        |> Option.bind (fun version ->
                            Manifest.tryReadIn versionDirectory
                            |> Option.map (fun manifest ->
                                { Version = version
                                  Dependencies =
                                    manifest.Dependencies
                                    |> List.choose (fun dependency ->
                                        match dependency.Source with
                                        | FromIndex requirement -> Some(dependency.Name, requirement)
                                        | _ -> None)
                                  Unusable = None })))
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
                cachedVersions store name |> Result.map (Option.map (Resolution.onlyPinned pinned name _.Version))
            let input =
                { Root = script
                  Requirements = Resolution.pinRequirements script pins
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
