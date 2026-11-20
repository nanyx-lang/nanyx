namespace NyxCompiler

open System.Collections.Generic
open System.IO

/// Why a resolution found no versions.
type ResolutionError =
    /// No choice of versions satisfies every dependency: `NYX0047`, whose message explains why.
    | Conflict of Diagnostic
    /// The index can't be read.
    | IndexUnreadable of string

/// Choosing the version of each package a build needs from the package index (#864), with `VersionSolver`: the newest
/// that satisfies every dependency, keeping each version a lockfile already locks while it still does. What it gives is
/// the lockfile that records them.
module Resolution =
    let private isShipped (name: string) = Directory.Exists(ShippedPackages.directoryOf name)

    /// The dependencies a resolution for the package `manifest` describes has to satisfy: those it names by their
    /// version, and those of each package it finds on disk or on GitHub, which share its lockfile. A package found
    /// that way that can't be read is left out, for the build to report.
    let requirements (store: PackageStore) (manifest: Manifest) : RootRequirement list =
        let visited = HashSet<string>()
        let rec from (manifest: Manifest) : RootRequirement list =
            if not (visited.Add manifest.Path) then
                []
            else
                manifest.Dependencies
                |> List.collect (fun dependency ->
                    let onDisk (directory: string) =
                        match Manifest.manifestIn directory |> Result.bind (Manifest.read >> Result.mapError (fun _ -> "")) with
                        | Result.Ok found -> from found
                        | Result.Error _ -> []
                    match dependency.Source with
                    | FromIndex requirement -> [ { From = manifest.Name; Package = dependency.Name; Requirement = requirement } ]
                    | AtPath directory -> onDisk directory
                    | OnGitHub reference ->
                        match store.GitHub.FetchTree reference.Repository reference.Commit with
                        | Result.Ok directory -> onDisk directory
                        | Result.Error _ -> []
                    | Shipped -> [])
        from manifest

    /// The requirement the index writes as `written`: as a manifest writes it, `2.1`, or with a caret, `^2.1`, which
    /// means the same.
    let private requirementOf (written: string) =
        VersionRequirement.tryParse (if written.StartsWith "^" then written.Substring 1 else written)

    /// This compiler's version, if it is a semantic version.
    let private running = SemanticVersion.tryParse Toolchain.version

    /// `indexed` as the solver reads it: why it can't be chosen, if it can't. A yanked version can be, when `locked` is
    /// what the lockfile locks of its package, since a build that already locks it keeps it.
    let private candidate (locked: SemanticVersion option) (indexed: IndexedVersion) : SolverVersion =
        let dependencies =
            indexed.Dependencies
            |> List.filter (fst >> isShipped >> not)
            |> List.map (fun (name, written) -> name, requirementOf written)
        let needsNewer =
            match indexed.Nanyx |> Option.bind VersionRequirement.tryParse, running with
            | Some oldest, Some running when oldest.Minimum > running -> Some(NeedsCompiler(indexed.Nanyx.Value, Toolchain.version))
            | _ -> None
        let unusable =
            if not indexed.Available then Some Unavailable
            elif needsNewer.IsSome then needsNewer
            elif dependencies |> List.exists (snd >> Option.isNone) then Some UnreadableDependencies
            elif indexed.Yanked && locked <> Some indexed.Version then Some Yanked
            else None
        { Version = indexed.Version
          Dependencies = dependencies |> List.choose (fun (name, requirement) -> requirement |> Option.map (fun requirement -> name, requirement))
          Unusable = unusable }

    /// Resolves the dependencies of the package `manifest` describes against the index of `store`, keeping each version
    /// `lockfile` locks while it is still allowed: the lockfile that records the versions chosen, or why there are none.
    let resolve (store: PackageStore) (manifest: Manifest) (lockfile: Lockfile) : Result<Lockfile, ResolutionError> =
        let listings = Dictionary<string, IndexedVersion list>()
        let locked = lockfile.Packages |> List.map (fun package -> package.Name, package.Version) |> Map.ofList
        let versions (name: string) =
            if isShipped name then
                Result.Ok None
            else
                PackageIndex.versions store.Index name
                |> Result.map (fun (_, listed) ->
                    listings.[name] <- listed
                    Some(listed |> List.map (candidate (locked.TryFind name))))
        let input =
            { Root = manifest.Name
              Requirements = requirements store manifest
              Versions = versions
              Locked = locked }
        match VersionSolver.solve input with
        | Solved chosen ->
            let names = chosen |> List.map fst |> set
            let entry (name: string, version: SemanticVersion) =
                let indexed = listings.[name] |> List.find (fun listed -> listed.Version = version)
                { Name = name
                  Version = version
                  Sha256 = indexed.Sha256
                  Dependencies = indexed.Dependencies |> List.map fst |> List.filter names.Contains |> List.sort }
            Result.Ok { Packages = chosen |> List.map entry }
        | NoSolution lines ->
            let message = String.concat "\n" lines
            Result.Error(Conflict(Diagnostics.general DiagnosticCodes.VersionConflict message |> Diagnostics.about manifest.Name))
        | IndexFailed message -> Result.Error(IndexUnreadable message)

    /// What `requirements` ask for that `lockfile` doesn't satisfy, each a line naming it: a package it doesn't lock, one
    /// it locks at a version the requirement doesn't accept, or one a locked package depends on that it doesn't lock.
    /// None when it satisfies them all, and a resolution has nothing to change.
    let unsatisfied (requirements: RootRequirement list) (lockfile: Lockfile) : string list =
        let required =
            requirements
            |> List.choose (fun requirement ->
                match Lockfile.tryFind requirement.Package lockfile with
                | None -> Some $"'{requirement.From}' depends on '{requirement.Package}' {requirement.Requirement}, which it doesn't lock"
                | Some locked when not (VersionRequirement.accepts requirement.Requirement locked.Version) ->
                    Some $"'{requirement.From}' depends on '{requirement.Package}' {requirement.Requirement}, which it locks at {locked.Version}"
                | Some _ -> None)
        let reached =
            lockfile.Packages
            |> List.collect (fun locked ->
                locked.Dependencies
                |> List.filter (fun name -> (Lockfile.tryFind name lockfile).IsNone)
                |> List.map (fun name -> $"'{locked.Name}' {locked.Version} depends on '{name}', which it doesn't lock"))
        List.distinct (required @ reached)

    /// The newest version of `name` in the index of `store` that a new resolution could choose, if there is one; or why
    /// the index can't be read.
    let newest (store: PackageStore) (name: string) : Result<SemanticVersion option, string> =
        PackageIndex.versions store.Index name
        |> Result.map (fun (_, listed) ->
            listed
            |> List.map (candidate None)
            |> List.filter (fun version -> version.Unusable.IsNone && version.Version.PreRelease.IsEmpty)
            |> List.map _.Version
            |> List.tryLast)
