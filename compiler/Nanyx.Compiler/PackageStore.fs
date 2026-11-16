namespace NyxCompiler

open System
open System.IO

/// Where a build finds what it doesn't have on disk beside it: the per-user cache of downloaded packages, the package
/// index it downloads them from, and how it fetches from GitHub.
type PackageStore =
    { /// The cache's directory, which holds each downloaded package, unpacked, at `<name>/<version>/`. A package in it
      /// is never changed.
      Cache: string
      /// The package index: an https address, or a directory on disk, that holds its `config.json`.
      Index: string
      /// How references to GitHub are fetched.
      GitHub: GitHubFetcher }

module PackageStore =
    /// The index nanyx-lang publishes.
    let defaultIndex = "https://nanyx-lang.github.io/packages/"

    /// The cache's directory: `NANYX_PACKAGES`, if it is set, or else `.nanyx/packages` in the user's home directory.
    let defaultCache () =
        match Environment.GetEnvironmentVariable "NANYX_PACKAGES" with
        | null
        | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".nanyx", "packages")
        | directory -> Path.GetFullPath directory

    /// The user's store: their cache, the index `NANYX_INDEX` names, if it is set, or else nanyx-lang's, and nothing
    /// fetched from GitHub, which isn't supported yet.
    let user () : PackageStore =
        { Cache = defaultCache ()
          Index =
            match Environment.GetEnvironmentVariable "NANYX_INDEX" with
            | null
            | "" -> defaultIndex
            | index -> index
          GitHub = GitHub.unsupported }

    /// The directory the package `name` at `version` is unpacked in, in the cache.
    let directoryOf (store: PackageStore) (name: string) (version: SemanticVersion) = Path.Combine(store.Cache, name, string version)
