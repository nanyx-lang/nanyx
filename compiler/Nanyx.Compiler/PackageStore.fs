namespace NyxCompiler

open System
open System.IO

/// Where a build finds what it doesn't have on disk beside it: the per-user cache of downloaded packages, the package
/// index it downloads them from, how it fetches from GitHub, and how the packages the compiler ships are seeded into it.
type PackageStore =
    { /// The cache's directory, which holds each downloaded package, unpacked, at `<name>/<version>/`. A package in it
      /// is never changed.
      Cache: string
      /// The package index: an https address, or a directory on disk, that holds its `config.json`.
      Index: string
      /// How references to GitHub are fetched.
      GitHub: GitHubFetcher
      /// Seeds the packages the compiler ships into the store, the first time it is asked, and gives the index of them
      /// it seeded (`shippedIndex`); or why it can't (see `SeededPackages.seed`). Every store is made with one, so a
      /// build that reads the packages the compiler ships finds them seeded: seeding packs them, which needs the
      /// compiler's later modules, so it is passed in where a store is made.
      Seed: PackageStore -> Result<string, string list> }

module PackageStore =
    /// The index nanyx-lang publishes.
    let defaultIndex = "https://packages.nanyx.dev/"

    /// The cache's directory: `NANYX_PACKAGES`, if it is set, or else `.nanyx/packages` in the user's home directory.
    let defaultCache () =
        match Environment.GetEnvironmentVariable "NANYX_PACKAGES" with
        | null
        | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".nanyx", "packages")
        | directory -> Path.GetFullPath directory

    /// The user's store, as a build reads it: their cache, the index `NANYX_INDEX` names, if it is set, or else
    /// nanyx-lang's, GitHub as far as `nanyx pull` has fetched from it into the cache (see `GitHub.cached`), and the
    /// packages the compiler ships seeded by `seed` (see `SeededPackages.user`).
    let user (seed: PackageStore -> Result<string, string list>) : PackageStore =
        let cache = defaultCache ()
        { Cache = cache
          Index =
            match Environment.GetEnvironmentVariable "NANYX_INDEX" with
            | null
            | "" -> defaultIndex
            | index -> index
          GitHub = GitHub.cached cache
          Seed = seed }

    /// `store`, fetching from GitHub what isn't in its cache yet: as `nanyx pull` reads it.
    let downloading (store: PackageStore) : PackageStore = { store with GitHub = GitHub.downloading store.Cache }

    /// The directory the package `name` at `version` is unpacked in, in the cache.
    let directoryOf (store: PackageStore) (name: string) (version: SemanticVersion) = Path.Combine(store.Cache, name, string version)

    /// The index of the packages the compiler ships, which it seeds in the cache of `store` before it resolves them
    /// (see `SeededPackages`), laid out as the package index is: one for each compiler version and the sources it ships
    /// (`ShippedPackages.fingerprint`), so compilers that ship different packages under one version don't share one.
    let shippedIndex (store: PackageStore) =
        Path.Combine(store.Cache, ".shipped", Toolchain.version, ShippedPackages.fingerprint.Value)
