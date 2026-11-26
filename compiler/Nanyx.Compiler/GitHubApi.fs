namespace NyxCompiler

open System
open System.Diagnostics
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks

/// A request to GitHub's REST API, as `nanyx publish` and `nanyx unlist` make them.
type GitHubRequest =
    { Method: string
      /// The address, `https://api.github.com/...` or, for an asset's upload, `https://uploads.github.com/...`.
      Url: string
      /// The body: JSON, or an asset's bytes.
      Body: byte array option
      ContentType: string option }

/// GitHub's answer: its status code and its body.
type GitHubResponse = { Status: int; Body: string }

/// What sends requests to GitHub, signed in as someone: GitHub itself, or a fake one in tests.
type GitHubClient = GitHubRequest -> Result<GitHubResponse, string>

/// What `nanyx publish` and `nanyx unlist` do on GitHub, through a `GitHubClient`: create a release with its asset, or
/// finish one a publish left part way, and open a pull request on the package index's repository, from a branch of it
/// for an account that can push to it, and from a fork of it otherwise.
module GitHubApi =
    /// The package index's repository, which registrations and unlistings are pull requests to.
    let indexRepository = { Owner = "nanyx-lang"; Name = "packages" }

    let private api = "https://api.github.com"

    /// The token `nanyx` signs in to GitHub with: `GITHUB_TOKEN`, if it is set, or else the one the GitHub CLI is logged in
    /// with (`gh auth token`); none if there is neither.
    let token () : string option =
        match Environment.GetEnvironmentVariable "GITHUB_TOKEN" with
        | null
        | "" ->
            try
                let start = ProcessStartInfo("gh", "auth token", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
                use gh = Process.Start start
                let output = gh.StandardOutput.ReadToEnd().Trim()
                gh.WaitForExit()
                if gh.ExitCode = 0 && output <> "" then Some output else None
            with :? ComponentModel.Win32Exception ->
                None
        | token -> Some token

    let private http =
        lazy
            (let client = new HttpClient(Timeout = TimeSpan.FromSeconds 100.0)
             client.DefaultRequestHeaders.UserAgent.ParseAdd $"nanyx/{Toolchain.version}"
             client)

    /// GitHub itself, signed in with `token`.
    let client (token: string) : GitHubClient =
        fun request ->
            try
                use message = new HttpRequestMessage(HttpMethod(request.Method), request.Url)
                message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}") |> ignore
                message.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json") |> ignore
                message.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28") |> ignore
                match request.Body with
                | Some body ->
                    message.Content <- new ByteArrayContent(body)
                    message.Content.Headers.TryAddWithoutValidation("Content-Type", defaultArg request.ContentType "application/json") |> ignore
                | None -> ()
                use response = http.Value.Send message
                use reader = new IO.StreamReader(response.Content.ReadAsStream())
                Result.Ok { Status = int response.StatusCode; Body = reader.ReadToEnd() }
            with
            | :? HttpRequestException
            | :? TaskCanceledException as error -> Result.Error $"GitHub can't be reached: {error.Message}"

    let private json (value: JsonNode) = Some(Encoding.UTF8.GetBytes(value.ToJsonString()))

    /// The JSON of `response` when its status is one of `expected`; or why not, with GitHub's own message, `what` saying
    /// what was being done.
    let private answered (what: string) (expected: int list) (response: GitHubResponse) : Result<JsonNode, string> =
        let body = if String.IsNullOrWhiteSpace response.Body then null else (try JsonNode.Parse response.Body with :? JsonException -> null)
        if List.contains response.Status expected then
            Result.Ok(if isNull body then JsonObject() :> JsonNode else body)
        else
            let message =
                match body with
                | :? JsonObject as answer when answer.ContainsKey "message" -> string answer.["message"]
                | _ -> response.Body
            Result.Error $"{what} failed: GitHub answered {response.Status}, {message}"

    /// Sends `request` with `client`, and gives the JSON GitHub answers when it answers `expected`; or why not (see
    /// `answered`).
    let private send (client: GitHubClient) (what: string) (expected: int list) (request: GitHubRequest) : Result<JsonNode, string> =
        client request |> Result.bind (answered what expected)

    let private get client what url = send client what [ 200 ] { Method = "GET"; Url = url; Body = None; ContentType = None }

    /// What `get` gives, for something that may not be there: none when GitHub answers 404.
    let private tryGet (client: GitHubClient) (what: string) (url: string) : Result<JsonNode option, string> =
        client { Method = "GET"; Url = url; Body = None; ContentType = None }
        |> Result.bind (fun response ->
            if response.Status = 404 then Result.Ok None else answered what [ 200 ] response |> Result.map Some)

    let private post client what expected url body = send client what expected { Method = "POST"; Url = url; Body = json body; ContentType = None }

    let private text (name: string) (node: JsonNode) = string node.[name]

    /// Whether the account `client` is signed in as can push to `repository`, which creating a release on it needs: none
    /// when GitHub doesn't say, as for a GitHub Actions `GITHUB_TOKEN`, whose answer has no `permissions`; or why the
    /// repository can't be read.
    let canPush (client: GitHubClient) (repository: GitHubRepository) : Result<bool option, string> =
        get client $"Reading {repository}" $"{api}/repos/{repository}"
        |> Result.map (fun found ->
            match found.["permissions"] with
            | :? JsonObject as permissions when permissions.ContainsKey "push" -> Some(permissions.["push"].GetValue<bool>())
            | _ -> None)

    /// Where a pull request to the index's repository comes from: a branch of the index's repository itself, for an
    /// account that can push to it, or else of the account's fork of it.
    type IndexRoute =
        | InIndex
        | ThroughFork of GitHubRepository

    /// How long to wait, in seconds, before each look for a fork GitHub hasn't finished making: it makes them in the
    /// background, and answers the request for one before it is there.
    let private forkWaits = [ 1; 2; 3; 5; 8; 13 ]

    /// Waits for the fork `fork` to be there, sleeping with `sleep` between looks; or says it never came.
    let private waitForFork (client: GitHubClient) (sleep: int -> unit) (fork: GitHubRepository) : Result<GitHubRepository, string> =
        let rec look waits =
            match tryGet client $"Looking for the fork {fork}" $"{api}/repos/{fork}" with
            | Result.Ok(Some _) -> Result.Ok fork
            | Result.Error error -> Result.Error error
            | Result.Ok None ->
                match waits with
                | wait :: rest ->
                    sleep wait
                    look rest
                | [] -> Result.Error $"GitHub made the fork {fork} of {indexRepository}, but it still isn't there: try again in a minute"
        look forkWaits

    /// Where the account `client` is signed in as opens its pull requests to the index's repository from (see
    /// `IndexRoute`), with its fork made, if it needs one, and there to push to; or why it can't open them. An account
    /// GitHub doesn't say can push there goes through a fork. `sleep` waits for a fork GitHub is still making, given a
    /// number of seconds.
    let indexRoute (client: GitHubClient) (sleep: int -> unit) : Result<IndexRoute, string> =
        canPush client indexRepository
        |> Result.bind (fun push ->
            if push = Some true then
                Result.Ok InIndex
            else
                post client $"Forking {indexRepository}" [ 202 ] $"{api}/repos/{indexRepository}/forks" (JsonObject())
                |> Result.bind (fun fork ->
                    match fork.["owner"], fork.["name"] with
                    | (:? JsonObject as owner), (:? JsonValue as name) when owner.ContainsKey "login" ->
                        waitForFork client sleep { Owner = text "login" owner; Name = string name }
                    | _ -> Result.Error $"Forking {indexRepository} failed: GitHub's answer names no fork")
                |> Result.map ThroughFork)

    /// What `createRelease` did.
    type Release =
        /// It created the release, at this address.
        | Released of string
        /// The release was there already with this asset, at this address: a publish that stopped after making it.
        | ReleasedAlready of string

    /// Uploads `asset`, named `assetName`, to `release`, the release of `repository` tagged `tag`.
    let private upload client (repository: GitHubRepository) (tag: string) (release: JsonNode) (assetName: string) (asset: byte array) =
        let id = text "id" release
        let url = $"https://uploads.github.com/repos/{repository}/releases/{id}/assets?name={Uri.EscapeDataString assetName}"
        send client $"Uploading {assetName} to the release {tag}" [ 201 ] { Method = "POST"; Url = url; Body = Some asset; ContentType = Some "application/octet-stream" }

    /// `existing`, the release of `repository` tagged `tag`, as a publish of `asset` finds it: one holding `asset` already
    /// (GitHub records each asset's SHA-256 as its `digest`), or none named `assetName`, which this gives it, is the
    /// release of a publish that stopped part way. Any other asset of that name is an error: a version can't change.
    let private resume client (repository: GitHubRepository) (tag: string) (assetName: string) (asset: byte array) (existing: JsonNode) : Result<Release, string> =
        let url = text "html_url" existing
        let named =
            match existing.["assets"] with
            | :? JsonArray as assets -> assets |> Seq.tryFind (fun found -> text "name" found = assetName)
            | _ -> None
        match named with
        | None -> upload client repository tag existing assetName asset |> Result.map (fun _ -> ReleasedAlready url)
        | Some found ->
            let digest = $"sha256:{PackageArchive.sha256 asset}"
            match found.["digest"] with
            | null -> Result.Error $"{repository} has a release {tag} already, whose {assetName} can't be compared with this one, since GitHub gives no SHA-256 for it: publish a new version"
            | recorded when string recorded = digest -> Result.Ok(ReleasedAlready url)
            | _ -> Result.Error $"{repository} has a release {tag} already, with a different {assetName}, and a published version can't change: publish a new version"

    /// Creates the release of `repository` tagged `tag`, named `name`, with the asset `assetName` whose bytes are `asset`.
    /// A tag that has a release already is the release of a publish that stopped part way when it holds the same asset,
    /// or none yet (see `resume`), and otherwise an error: a version can't change.
    let createRelease (client: GitHubClient) (repository: GitHubRepository) (tag: string) (name: string) (assetName: string) (asset: byte array) : Result<Release, string> =
        let release = JsonObject()
        release.["tag_name"] <- JsonValue.Create tag
        release.["name"] <- JsonValue.Create name
        match tryGet client $"Looking for a release {tag} of {repository}" $"{api}/repos/{repository}/releases/tags/{Uri.EscapeDataString tag}" with
        | Result.Error error -> Result.Error error
        | Result.Ok(Some existing) -> resume client repository tag assetName asset existing
        | Result.Ok None ->
            client { Method = "POST"; Url = $"{api}/repos/{repository}/releases"; Body = json release; ContentType = None }
            |> Result.bind (fun response ->
                answered $"Creating the release {tag} of {repository}" [ 201 ] response
                |> Result.mapError (fun error ->
                    if response.Status = 403 || response.Status = 404 then
                        $"{error}. The token can't create releases on {repository}: give it write access to the repository's contents (in a GitHub Actions workflow, `permissions: contents: write`)"
                    else
                        error))
            |> Result.bind (fun created -> upload client repository tag created assetName asset |> Result.map (fun _ -> Released(text "html_url" created)))

    /// The text of the file at `path` on the default branch of the index's repository, with its blob's SHA; none if there
    /// is no such file.
    let indexFile (client: GitHubClient) (path: string) : Result<(string * string) option, string> =
        tryGet client $"Reading {path} from {indexRepository}" $"{api}/repos/{indexRepository}/contents/{path}"
        |> Result.map (Option.map (fun found ->
            let content = (text "content" found).Replace("\n", "")
            Encoding.UTF8.GetString(Convert.FromBase64String content), text "sha" found))

    /// `branch`, or, if `repository` has a branch of that name already, the first of `branch-2`, `branch-3`, … it hasn't.
    let private freeBranch (client: GitHubClient) (repository: GitHubRepository) (branch: string) : Result<string, string> =
        let rec tryNumber number =
            let candidate = if number = 1 then branch else $"{branch}-{number}"
            tryGet client $"Looking for the branch {candidate} in {repository}" $"{api}/repos/{repository}/git/ref/heads/{candidate}"
            |> Result.bind (function
                | None -> Result.Ok candidate
                | Some _ -> tryNumber (number + 1))
        tryNumber 1

    /// Opens a pull request on the index's repository that writes `contents` to the file at `path` (replacing the blob
    /// `replacing`, for a file that is there already), from a branch named `branch`, or `branch-2` and so on if that is
    /// taken, made where `route` says. Gives the pull request's address.
    let openIndexPullRequest
        (client: GitHubClient)
        (route: IndexRoute)
        (path: string)
        (contents: string)
        (replacing: string option)
        (branch: string)
        (title: string)
        (body: string)
        : Result<string, string> =
        let repository =
            match route with
            | InIndex -> indexRepository
            | ThroughFork fork -> fork
        get client $"Reading {indexRepository}'s default branch" $"{api}/repos/{indexRepository}/git/ref/heads/main"
        |> Result.bind (fun head ->
            freeBranch client repository branch
            |> Result.bind (fun branch ->
                let reference = JsonObject()
                reference.["ref"] <- JsonValue.Create $"refs/heads/{branch}"
                reference.["sha"] <- JsonValue.Create(string head.["object"].["sha"])
                post client $"Making the branch {branch} in {repository}" [ 201 ] $"{api}/repos/{repository}/git/refs" reference
                |> Result.map (fun _ -> branch)))
        |> Result.bind (fun branch ->
            let file = JsonObject()
            file.["message"] <- JsonValue.Create title
            file.["content"] <- JsonValue.Create(Convert.ToBase64String(Encoding.UTF8.GetBytes contents))
            file.["branch"] <- JsonValue.Create branch
            replacing |> Option.iter (fun sha -> file.["sha"] <- JsonValue.Create sha)
            send client $"Writing {path} in {repository}" [ 200; 201 ] { Method = "PUT"; Url = $"{api}/repos/{repository}/contents/{path}"; Body = json file; ContentType = None }
            |> Result.map (fun _ -> branch))
        |> Result.bind (fun branch ->
            let pull = JsonObject()
            pull.["title"] <- JsonValue.Create title
            pull.["head"] <- JsonValue.Create(match route with InIndex -> branch | ThroughFork fork -> $"{fork.Owner}:{branch}")
            pull.["base"] <- JsonValue.Create "main"
            pull.["body"] <- JsonValue.Create body
            post client $"Opening the pull request on {indexRepository}" [ 201 ] $"{api}/repos/{indexRepository}/pulls" pull)
        |> Result.map (text "html_url")
