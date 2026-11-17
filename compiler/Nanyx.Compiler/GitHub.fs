namespace NyxCompiler

open System
open System.Collections.Concurrent
open System.IO
open System.Text.RegularExpressions

/// A repository on GitHub: `acme/web`.
type GitHubRepository =
    { Owner: string
      Name: string }

    override this.ToString() = $"{this.Owner}/{this.Name}"

/// A manifest's reference to code in a GitHub repository, pinned to a commit: a whole package, `github = "acme/web"`,
/// or one file, `github = "acme/snippets/retry.nyx"`.
type GitHubReference =
    { Repository: GitHubRepository
      /// The file's path in the repository, `retry.nyx`; none for a whole package.
      File: string option
      /// The commit it is pinned to, a full SHA.
      Commit: string }

    override this.ToString() =
        match this.File with
        | Some file -> $"{this.Repository}/{file}"
        | None -> string this.Repository

/// Why a GitHub reference could not be fetched.
type FetchFailure =
    /// Nothing can be downloaded from GitHub with this fetcher: a test's, or one that has nothing to fetch with.
    | FetchingNotSupported
    /// It isn't in the package cache, and a build doesn't download: `nanyx pull` does.
    | NotPulled
    /// The repository, the commit or the file is not there.
    | FetchFailed of string

/// How GitHub references are fetched: resolving a tag or branch to the commit it points at now, and fetching a file, or
/// a repository's whole tree, at a commit, to disk. A build's is `GitHub.cached`, which reads what `nanyx pull` fetched
/// into the package cache with `GitHub.downloading`; tests use `GitHub.inDirectory`, or `GitHub.downloadingWith` and
/// a fake of GitHub's addresses.
type GitHubFetcher =
    { /// The commit a tag or branch of the repository points at now.
      Resolve: GitHubRepository -> string -> Result<string, FetchFailure>
      /// The full path of the file at the path in the repository, at the commit.
      FetchFile: GitHubRepository -> string -> string -> Result<string, FetchFailure>
      /// The directory that holds the repository's tree at the commit.
      FetchTree: GitHubRepository -> string -> Result<string, FetchFailure> }

module GitHub =
    let private commitPattern = Regex "^[0-9a-f]{40}$"

    /// Whether `rev` is a full commit SHA, which is all that pins a reference: a tag or a branch can move.
    let isCommit (rev: string) = commitPattern.IsMatch rev

    let private segment = Regex "^[A-Za-z0-9_.-]+$"

    /// The repository and the path of the file in it that `text` names, `acme/snippets/retry.nyx`, or the repository
    /// alone, `acme/web`; or what it must be instead.
    let parse (text: string) : Result<GitHubRepository * string option, string> =
        let parts = text.Split '/'
        if parts.Length < 2 || parts |> Array.exists (fun part -> not (segment.IsMatch part) || part = "." || part = "..") then
            Result.Error "a repository, `owner/name`, and for a file the file's path in it, as `acme/web` or `acme/snippets/retry.nyx`"
        else
            let repository = { Owner = parts[0]; Name = parts[1] }
            match parts |> Array.skip 2 with
            | [||] -> Result.Ok(repository, None)
            | path -> Result.Ok(repository, Some(String.concat "/" path))

    /// What `failure` says, as the end of a sentence.
    let describe (failure: FetchFailure) =
        match failure with
        | FetchingNotSupported -> "downloading from GitHub isn't supported here"
        | NotPulled -> "it isn't in the package cache: run `nanyx pull` to fetch it"
        | FetchFailed message -> message

    /// The fetcher of a compiler that can't download anything: every fetch fails with `FetchingNotSupported`.
    let unsupported: GitHubFetcher =
        { Resolve = fun _ _ -> Result.Error FetchingNotSupported
          FetchFile = fun _ _ _ -> Result.Error FetchingNotSupported
          FetchTree = fun _ _ -> Result.Error FetchingNotSupported }

    /// A fetcher that reads repositories from the directory `root` in place of GitHub: the tree of `acme/web` at a
    /// commit is the directory `root/acme/web/<commit>`. `refs` gives the commit each tag or branch points at, by
    /// `acme/web@v1.2.0`.
    let inDirectory (root: string) (refs: Map<string, string>) : GitHubFetcher =
        let tree (repository: GitHubRepository) (commit: string) = Path.Combine(root, repository.Owner, repository.Name, commit)
        { Resolve =
            fun repository rev ->
                match refs |> Map.tryFind $"{repository}@{rev}" with
                | Some commit -> Result.Ok commit
                | None -> Result.Error(FetchFailed $"'{repository}' has no tag or branch '{rev}'")
          FetchFile =
            fun repository commit path ->
                let file = Path.GetFullPath(Path.Combine(tree repository commit, path))
                if File.Exists file then Result.Ok file else Result.Error(FetchFailed $"'{repository}' has no file '{path}' at {commit}")
          FetchTree =
            fun repository commit ->
                let directory = tree repository commit
                if Directory.Exists directory then Result.Ok(Path.GetFullPath directory) else Result.Error(FetchFailed $"'{repository}' has no commit {commit}") }

    /// Where the package cache `cache` keeps the tree of `repository` at `commit`.
    let treeIn (cache: string) (repository: GitHubRepository) (commit: string) =
        Path.Combine(cache, "github", repository.Owner, repository.Name, commit)

    /// Where the package cache `cache` keeps the file at `path` of `repository` at `commit`, fetched on its own.
    let fileIn (cache: string) (repository: GitHubRepository) (commit: string) (path: string) =
        Path.Combine(cache, "github-files", repository.Owner, repository.Name, commit, path)

    /// What a fetcher downloads with: the bytes at an address, sent with headers, none when there is nothing there.
    type Get = (string * string) list -> string -> Result<byte array option, string>

    /// `Downloads.get`, with the token in `GITHUB_TOKEN`, if it is set, so that GitHub's limit on requests is the
    /// account's.
    let private gitHubGet: Get =
        fun headers address ->
            let token =
                match Environment.GetEnvironmentVariable "GITHUB_TOKEN" with
                | null
                | "" -> []
                | token -> [ "Authorization", $"Bearer {token}" ]
            Downloads.get (headers @ token) address

    /// Resolving a tag or a branch to the commit it points at now, through GitHub's API with `get`: `HEAD` is the default
    /// branch. Each is asked once by the resolver this makes, since the editor asks again with every change to a
    /// manifest.
    let private resolveWith (get: Get) : GitHubRepository -> string -> Result<string, FetchFailure> =
        let resolved = ConcurrentDictionary<string, Result<string, FetchFailure>>()
        fun repository rev ->
            resolved.GetOrAdd(
                $"{repository}@{rev}",
                fun _ ->
                    match get [ "Accept", "application/vnd.github.sha" ] $"https://api.github.com/repos/{repository}/commits/{Uri.EscapeDataString rev}" with
                    | Result.Ok (Some bytes) ->
                        let commit = Text.Encoding.UTF8.GetString(bytes).Trim()
                        if isCommit commit then Result.Ok commit
                        else Result.Error(FetchFailed $"GitHub gave {commit} for '{repository}@{rev}', which is not a commit")
                    | Result.Ok None -> Result.Error(FetchFailed $"'{repository}' has no tag or branch '{rev}'")
                    | Result.Error message -> Result.Error(FetchFailed message)
            )

    /// A fetcher that reads only what is in the package cache `cache`: a file or a tree that isn't there fails with
    /// `NotPulled`, since a build never downloads. Resolving goes to GitHub with `get`.
    let cachedWith (get: Get) (cache: string) : GitHubFetcher =
        { Resolve = resolveWith get
          FetchFile =
            fun repository commit path ->
                let file = fileIn cache repository commit path
                if File.Exists file then Result.Ok file else Result.Error NotPulled
          FetchTree =
            fun repository commit ->
                let directory = treeIn cache repository commit
                if Directory.Exists directory then Result.Ok directory else Result.Error NotPulled }

    /// A fetcher that downloads from GitHub with `get` what isn't in the package cache `cache` yet, and keeps it there: a
    /// file from `raw.githubusercontent.com`, and a tree as the zip `codeload.github.com` makes of it. What is in the cache
    /// is never changed, since a commit's files can't change.
    let downloadingWith (get: Get) (cache: string) : GitHubFetcher =
        let cached = cachedWith get cache
        { cached with
            FetchFile =
                fun repository commit path ->
                    match cached.FetchFile repository commit path with
                    | Result.Ok file -> Result.Ok file
                    | Result.Error _ ->
                        match get [] $"https://raw.githubusercontent.com/{repository}/{commit}/{path}" with
                        | Result.Ok (Some bytes) ->
                            let file = fileIn cache repository commit path
                            let partial = $"{file}.partial-{Guid.NewGuid():N}"
                            Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
                            File.WriteAllBytes(partial, bytes)
                            if File.Exists file then File.Delete partial else File.Move(partial, file)
                            Result.Ok file
                        | Result.Ok None -> Result.Error(FetchFailed $"'{repository}' has no file '{path}' at {commit}")
                        | Result.Error message -> Result.Error(FetchFailed message)
            FetchTree =
                fun repository commit ->
                    match cached.FetchTree repository commit with
                    | Result.Ok directory -> Result.Ok directory
                    | Result.Error _ ->
                        match get [] $"https://codeload.github.com/{repository}/zip/{commit}" with
                        | Result.Ok (Some bytes) ->
                            let directory = treeIn cache repository commit
                            let folder (names: string list) =
                                Downloads.singleFolder names |> Option.map Result.Ok |> Option.defaultValue (Result.Error "its files are not in one folder")
                            Downloads.unzip folder bytes directory
                            |> Result.map (fun () -> directory)
                            |> Result.mapError (fun message -> FetchFailed $"the zip of '{repository}' at {commit} can't be unpacked: {message}")
                        | Result.Ok None -> Result.Error(FetchFailed $"'{repository}' has no commit {commit}")
                        | Result.Error message -> Result.Error(FetchFailed message) }

    /// Resolving through GitHub itself, shared by every fetcher of the process, so each tag or branch is asked once.
    let private gitHubResolve = lazy (resolveWith gitHubGet)

    /// `cachedWith` GitHub itself: what a build fetches with.
    let cached (cache: string) : GitHubFetcher = { cachedWith gitHubGet cache with Resolve = gitHubResolve.Value }

    /// `downloadingWith` GitHub itself: what `nanyx pull` fetches with.
    let downloading (cache: string) : GitHubFetcher = { downloadingWith gitHubGet cache with Resolve = gitHubResolve.Value }
