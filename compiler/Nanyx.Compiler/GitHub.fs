namespace NyxCompiler

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
    /// Nothing can be downloaded from GitHub yet.
    | FetchingNotSupported
    /// The repository, the commit or the file is not there.
    | FetchFailed of string

/// How GitHub references are fetched: resolving a tag or branch to the commit it points at now, and fetching a file, or
/// a repository's whole tree, at a commit, to disk. The compiler's is `GitHub.unsupported` until there is a cache to
/// fetch into ([#863](https://github.com/nanyx-lang/nanyx/issues/863)); tests use `GitHub.inDirectory`.
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
            Result.Error "a repository, owner/name, and for a file the file's path in it: acme/web, or acme/snippets/retry.nyx"
        else
            let repository = { Owner = parts[0]; Name = parts[1] }
            match parts |> Array.skip 2 with
            | [||] -> Result.Ok(repository, None)
            | path -> Result.Ok(repository, Some(String.concat "/" path))

    /// What `failure` says, as the end of a sentence.
    let describe (failure: FetchFailure) =
        match failure with
        | FetchingNotSupported -> "downloading from GitHub isn't supported yet"
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
                | None -> Result.Error(FetchFailed $"{repository} has no tag or branch '{rev}'")
          FetchFile =
            fun repository commit path ->
                let file = Path.GetFullPath(Path.Combine(tree repository commit, path))
                if File.Exists file then Result.Ok file else Result.Error(FetchFailed $"{repository} has no file {path} at {commit}")
          FetchTree =
            fun repository commit ->
                let directory = tree repository commit
                if Directory.Exists directory then Result.Ok(Path.GetFullPath directory) else Result.Error(FetchFailed $"{repository} has no commit {commit}") }
