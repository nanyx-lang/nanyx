namespace NyxCompiler

open System
open System.Text.RegularExpressions

/// A semantic version, `1.2.0` or `1.2.0-beta.1`; any build metadata after a `+` is dropped, since it doesn't order.
[<CustomEquality; CustomComparison>]
type SemanticVersion =
    { Major: int
      Minor: int
      Patch: int
      /// The pre-release identifiers after the `-`, `["beta"; "1"]`; empty for a release.
      PreRelease: string list }

    override this.ToString() =
        let core = $"{this.Major}.{this.Minor}.{this.Patch}"
        match this.PreRelease with
        | [] -> core
        | identifiers -> core + "-" + String.concat "." identifiers

    /// The order semantic versioning gives: by major, minor and patch, then a pre-release before its release, and two
    /// pre-releases by their identifiers, numbers numerically and before words.
    static member private Order(a: SemanticVersion, b: SemanticVersion) =
        let identifier (x: string) (y: string) =
            match Int64.TryParse x, Int64.TryParse y with
            | (true, m), (true, n) -> compare m n
            | (true, _), _ -> -1
            | _, (true, _) -> 1
            | _ -> String.CompareOrdinal(x, y) |> sign
        let rec identifiers (xs: string list) (ys: string list) =
            match xs, ys with
            | [], [] -> 0
            | [], _ -> -1
            | _, [] -> 1
            | x :: xs, y :: ys ->
                match identifier x y with
                | 0 -> identifiers xs ys
                | order -> order
        match compare (a.Major, a.Minor, a.Patch) (b.Major, b.Minor, b.Patch) with
        | 0 ->
            match a.PreRelease, b.PreRelease with
            | [], [] -> 0
            | [], _ -> 1
            | _, [] -> -1
            | xs, ys -> identifiers xs ys
        | order -> order

    override this.Equals(other) =
        match other with
        | :? SemanticVersion as other -> SemanticVersion.Order(this, other) = 0
        | _ -> false

    override this.GetHashCode() = hash (this.Major, this.Minor, this.Patch, this.PreRelease)

    interface IComparable with
        member this.CompareTo(other) =
            match other with
            | :? SemanticVersion as other -> SemanticVersion.Order(this, other)
            | _ -> invalidArg "other" "not a semantic version"

/// Which versions of a package a dependency accepts, as a manifest writes it: `1.2` or `1.2.0`, any version
/// compatible with that one. A compatible version is at least it, with the same major version; below 1.0.0, where
/// any release can break, with the same minor version too. A pre-release is accepted only when the requirement names
/// a pre-release of the same version.
type VersionRequirement =
    { /// The oldest version accepted.
      Minimum: SemanticVersion
      /// The requirement as it is written.
      Written: string }

    override this.ToString() = this.Written

/// The compilers a package builds with, as its manifest's `nanyx` writes them: `0.43` or `0.43.0`, that compiler or a
/// later one, or `=0.43.0`, that compiler only, as a host and the host library it is written on say, since they use the
/// compiler's internals.
type CompilerRequirement =
    { /// The oldest compiler accepted, and the only one when `Exact`.
      Oldest: SemanticVersion
      /// Whether only `Oldest` is accepted.
      Exact: bool
      /// The requirement as it is written.
      Written: string }

    override this.ToString() = this.Written

module SemanticVersion =
    let private pattern = Regex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$")

    /// The version `text` writes, if it is one: `1.2.0`, `1.2.0-beta.1`.
    let tryParse (text: string) : SemanticVersion option =
        let found = pattern.Match text
        if not found.Success then
            None
        else
            Some
                { Major = int found.Groups[1].Value
                  Minor = int found.Groups[2].Value
                  Patch = int found.Groups[3].Value
                  PreRelease = if found.Groups[4].Success then List.ofArray (found.Groups[4].Value.Split '.') else [] }

    /// `version` with its major version one more and the rest zero: the first version a major bump makes.
    let nextMajor (version: SemanticVersion) = { Major = version.Major + 1; Minor = 0; Patch = 0; PreRelease = [] }

    /// `version` with its minor version one more and its patch zero.
    let nextMinor (version: SemanticVersion) = { version with Minor = version.Minor + 1; Patch = 0; PreRelease = [] }

    /// `version` with its patch one more.
    let nextPatch (version: SemanticVersion) = { version with Patch = version.Patch + 1; PreRelease = [] }

module VersionRequirement =
    let private partial = Regex(@"^(0|[1-9]\d*)(?:\.(0|[1-9]\d*))?$")

    /// What a requirement must be, to finish "must be ...".
    let rule = "a version, `1.2.0`, or its first numbers, `1.2`"

    /// The requirement `text` writes, if it is one: a version, `1.2.0`, or its first numbers, `1` or `1.2`, which is
    /// the version with zeros for the rest.
    let tryParse (text: string) : VersionRequirement option =
        match SemanticVersion.tryParse text with
        | Some version -> Some { Minimum = version; Written = text }
        | None ->
            let found = partial.Match text
            if found.Success then
                let minor = if found.Groups[2].Success then int found.Groups[2].Value else 0
                Some { Minimum = { Major = int found.Groups[1].Value; Minor = minor; Patch = 0; PreRelease = [] }; Written = text }
            else
                None

    /// The versions compatible with `version`, written as `version` is: `1.2.0`.
    let at (version: SemanticVersion) : VersionRequirement = { Minimum = version; Written = string version }

    /// Whether `version` satisfies `requirement` (see `VersionRequirement`).
    let accepts (requirement: VersionRequirement) (version: SemanticVersion) : bool =
        let minimum = requirement.Minimum
        let compatible =
            if minimum.Major > 0 then version.Major = minimum.Major
            else version.Major = 0 && version.Minor = minimum.Minor
        let preRelease =
            version.PreRelease.IsEmpty
            || (not minimum.PreRelease.IsEmpty && (version.Major, version.Minor, version.Patch) = (minimum.Major, minimum.Minor, minimum.Patch))
        compatible && version >= minimum && preRelease

module CompilerRequirement =
    /// What a package's `nanyx` must be, to finish "must be ...".
    let rule = "a version, `0.43.0`, or its first numbers, `0.43`, for that compiler or a later one; or `=` and a whole version, `=0.43.0`, for that compiler only"

    /// The requirement `text` writes, if it is one: `=0.43.0`, that compiler only, or else a version or its first numbers
    /// (see `VersionRequirement.tryParse`), that compiler or a later one.
    let tryParse (text: string) : CompilerRequirement option =
        if text.StartsWith "=" then
            SemanticVersion.tryParse (text.Substring 1) |> Option.map (fun version -> { Oldest = version; Exact = true; Written = text })
        else
            VersionRequirement.tryParse text |> Option.map (fun requirement -> { Oldest = requirement.Minimum; Exact = false; Written = text })

    /// Whether the compiler `running` satisfies `requirement`.
    let accepts (requirement: CompilerRequirement) (running: SemanticVersion) : bool =
        if requirement.Exact then running = requirement.Oldest else running >= requirement.Oldest

    /// The compilers `requirement` accepts, as a message names them: `nanyx 0.43 or later`, or `nanyx 0.43.0 only`.
    let describe (requirement: CompilerRequirement) : string =
        if requirement.Exact then $"nanyx {requirement.Oldest} only" else $"nanyx {requirement.Written} or later"
