namespace NyxCompiler

open System.Collections.Generic
open System.IO

/// Why a resolution found no versions.
type ResolutionError =
    /// No choice of versions satisfies every dependency: `NYX0047`, whose message explains why.
    | Conflict of Diagnostic
    /// The index can't be read.
    | IndexUnreadable of string

/// What a resolution chose: the lockfile that records it, the versions in it the index has unlisted, which only a
/// lockfile or a script's pin keeps, and what reading the index warned of (`PackageIndex.listing`).
type Resolved =
    { Lockfile: Lockfile
      Unlisted: (string * SemanticVersion) list
      Warnings: Diagnostic list }

/// What pulling the dependencies of a package or a script did: a line for each package, and a warning (`NYX0051`) for
/// each version it uses that the index has unlisted, which it read the index for.
type Pulled =
    { Lines: string list
      Warnings: Diagnostic list }

/// Choosing the version of each package a build needs from the package index (#864), with `VersionSolver`: the newest
/// that satisfies every dependency, keeping each version a lockfile already locks while it still does. What it gives is
/// the lockfile that records them.
module Resolution =
    /// The dependencies a resolution for the package `manifest` describes has to satisfy: those it names by their
    /// version, its host's and its linters', where it names them by their version, and those of each package it finds on disk or on GitHub,
    /// which share its lockfile. A package found that way that can't be read is left out, for the build to report.
    let requirements (store: PackageStore) (manifest: Manifest) : RootRequirement list =
        let visited = HashSet<string>()
        let rec from (manifest: Manifest) : RootRequirement list =
            if not (visited.Add manifest.Path) then
                []
            else
                let host =
                    match manifest.Host with
                    | Some { Package = host; Version = Some requirement } -> [ { From = manifest.Name; Package = host; Requirement = requirement } ]
                    | Some { Path = Some directory } ->
                        match Manifest.tryReadIn directory with
                        | Some found -> from found
                        | None -> []
                    | _ -> []
                // Its linter packages are locked as its dependencies are, though they aren't part of its program.
                let linters =
                    manifest.LinterPackages
                    |> List.collect (fun linter ->
                        match linter.Version, linter.Path with
                        | Some requirement, _ -> [ { From = manifest.Name; Package = linter.Package; Requirement = requirement } ]
                        | None, Some directory ->
                            match Manifest.tryReadIn directory with
                            | Some found -> from found
                            | None -> []
                        | None, None -> [])
                host
                @ linters
                @ (manifest.Dependencies
                |> List.collect (fun dependency ->
                    let onDisk (directory: string) =
                        match Manifest.tryReadIn directory with
                        | Some found -> from found
                        | None -> []
                    match dependency.Source with
                    | FromIndex requirement -> [ { From = manifest.Name; Package = dependency.Name; Requirement = requirement } ]
                    | AtPath directory -> onDisk directory
                    | OnGitHub reference ->
                        match store.GitHub.FetchTree reference.Repository reference.Commit with
                        | Result.Ok directory -> onDisk directory
                        | Result.Error _ -> []))
        from manifest

    /// The requirement the index writes as `written`: as a manifest writes it, `2.1`, or with a caret, `^2.1`, which
    /// means the same.
    let private requirementOf (written: string) =
        VersionRequirement.tryParse (if written.StartsWith "^" then written.Substring 1 else written)

    /// This compiler's version, if it is a semantic version.
    let private running = SemanticVersion.tryParse Toolchain.version

    /// What the version `version` of the package `name` asks of the package `dependency`, which the index writes as
    /// `written`: if `name` at `version` is the copy of a package that `store` seeded, and `dependency` is seeded too, the
    /// seeded version only. The packages the compiler ships are built and released together, and a host runs its
    /// programs against the copies it was built with, so a program under the seeded host that read a newer library from
    /// the index would meet two of its types, one the host's and one its own.
    let private requirementIn (store: PackageStore) (name: string) (version: SemanticVersion) (dependency: string) (written: string) =
        let seededAt (package: string) = PackageStore.seededCopy store package |> Option.map _.Version
        match seededAt name, seededAt dependency with
        | Some seeded, Some shipped when seeded = version -> Some(VersionRequirement.exactly shipped)
        | _ -> requirementOf written

    /// `indexed`, a version of the package `name`, as the solver reads it: why it can't be chosen, if it can't. An unlisted
    /// version can be, when `locked` is what the lockfile locks of its package, since a build that already locks it keeps
    /// it.
    let private candidate (store: PackageStore) (name: string) (locked: SemanticVersion option) (indexed: IndexedVersion) : SolverVersion =
        let dependencies =
            indexed.Dependencies |> List.map (fun (dependency, written) -> dependency, requirementIn store name indexed.Version dependency written)
        let needsNewer =
            match indexed.Nanyx |> Option.bind CompilerRequirement.tryParse, running with
            | Some requirement, Some running when not (CompilerRequirement.accepts requirement running) -> Some(NeedsCompiler(requirement, Toolchain.version))
            | _ -> None
        let unusable =
            if not indexed.Available then Some Unavailable
            elif needsNewer.IsSome then needsNewer
            elif dependencies |> List.exists (snd >> Option.isNone) then Some UnreadableDependencies
            elif indexed.Unlisted && locked <> Some indexed.Version then Some Unlisted
            else None
        { Version = indexed.Version
          Dependencies = dependencies |> List.choose (fun (name, requirement) -> requirement |> Option.map (fun requirement -> name, requirement))
          Unusable = unusable }

    /// What the script `script` (its name, for messages) asks of each of `pins`: exactly the version it gives.
    let pinRequirements (script: string) (pins: (string * SemanticVersion) list) : RootRequirement list =
        pins
        |> List.map (fun (name, version) -> { From = script; Package = name; Requirement = VersionRequirement.at version })

    /// `versions` of the package `name`, each the version `versionOf` gives: only the one `pinned` gives, if it names
    /// the package.
    let onlyPinned (pinned: Map<string, SemanticVersion>) (name: string) (versionOf: 'version -> SemanticVersion) (versions: 'version list) : 'version list =
        match pinned.TryFind name with
        | Some version -> versions |> List.filter (fun found -> versionOf found = version)
        | None -> versions

    /// Resolves `requirements`, those of `root`, against the index of `store`, keeping each version `locked` gives while it
    /// is still allowed, and choosing only the version `pinned` gives of each package it names: the lockfile that records
    /// the versions chosen, or why there are none.
    let private resolveWith
        (store: PackageStore)
        (root: string)
        (requirements: RootRequirement list)
        (locked: Map<string, SemanticVersion>)
        (pinned: Map<string, SemanticVersion>)
        : Result<Resolved, ResolutionError> =
        let listings = Dictionary<string, IndexedVersion list>()
        let warnings = ResizeArray<Diagnostic>()
        let versions (name: string) =
            PackageIndex.listing store name
            |> Result.map (fun (_, listed, listingWarnings) ->
                listings.[name] <- listed
                warnings.AddRange listingWarnings
                Some(listed |> onlyPinned pinned name _.Version |> List.map (candidate store name (locked.TryFind name))))
        let input =
            { Root = root
              Requirements = requirements
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
            let unlisted =
                chosen |> List.filter (fun (name, version) -> listings.[name] |> List.exists (fun listed -> listed.Version = version && listed.Unlisted))
            Result.Ok
                { Lockfile = { Packages = chosen |> List.map entry }
                  Unlisted = unlisted
                  Warnings = List.ofSeq warnings }
        | NoSolution lines ->
            let message = String.concat "\n" lines
            Result.Error(Conflict(Diagnostics.general DiagnosticCodes.VersionConflict message |> Diagnostics.about root))
        | IndexFailed message -> Result.Error(IndexUnreadable message)

    /// Resolves the dependencies of the package `manifest` describes against the index of `store`, keeping each version
    /// `lockfile` locks while it is still allowed: the lockfile that records the versions chosen, or why there are none.
    let resolve (store: PackageStore) (manifest: Manifest) (lockfile: Lockfile) : Result<Resolved, ResolutionError> =
        let locked = lockfile.Packages |> List.map (fun package -> package.Name, package.Version) |> Map.ofList
        resolveWith store manifest.Name (requirements store manifest) locked Map.empty

    /// Resolves the dependencies of the script `script` (its name, for messages) against the index of `store`: each of
    /// `pins` at exactly the version it gives, which may be unlisted, since the script names it, and the packages they
    /// depend on as a package's are. A script has no lockfile: the versions its directives write are its pins.
    let resolvePinned (store: PackageStore) (script: string) (pins: (string * SemanticVersion) list) : Result<Resolved, ResolutionError> =
        let pinned = Map.ofList pins
        resolveWith store script (pinRequirements script pins) pinned pinned

    /// The warning (`NYX0051`) that `name` `version`, which `namedBy` names, is unlisted, with how to move off it, `move`.
    /// The build goes on with it.
    let private unlistedWarning (name: string) (version: SemanticVersion) (namedBy: string) (move: string) : Diagnostic =
        Diagnostics.general
            DiagnosticCodes.UnlistedVersion
            $"{name} {version}, which {namedBy}, is unlisted, so no new resolution chooses it: {move}, or keep it if you need it"
        |> Diagnostics.about $"{name} {version}"

    /// The warning (`NYX0051`) that `name` `version`, which a package's lockfile locks, is unlisted.
    let lockedUnlisted (name: string) (version: SemanticVersion) : Diagnostic =
        unlistedWarning
            name
            version
            $"'{Lockfile.fileName}' locks"
            $"to move to a listed version, delete its entry from '{Lockfile.fileName}' and run `nanyx pull`, first changing the version the manifest asks for if it accepts no listed one"

    /// The warning (`NYX0051`) that `name` `version`, which the script `script` (its name) pins, is unlisted.
    let pinnedUnlisted (script: string) (name: string) (version: SemanticVersion) : Diagnostic =
        unlistedWarning name version $"'{script}' pins with `::install`" "to move to a listed version, change the version its `::install` names"

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

    /// The newest version of `name` that `store` lists (see `PackageIndex.listing`) and a new resolution could choose, if
    /// there is one; or why the index can't be read.
    let newest (store: PackageStore) (name: string) : Result<SemanticVersion option, string> =
        PackageIndex.listing store name
        |> Result.map (fun (_, listed, _) ->
            listed
            |> List.map (candidate store name None)
            |> List.filter (fun version -> version.Unusable.IsNone && version.Version.PreRelease.IsEmpty)
            |> List.map _.Version
            |> List.tryLast)
