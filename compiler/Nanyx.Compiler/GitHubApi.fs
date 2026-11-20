namespace NyxCompiler

open System
open System.Diagnostics
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks

/// A request to GitHub's REST API, as `nanyx publish` and `nanyx yank` make them.
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

/// What `nanyx publish` and `nanyx yank` do on GitHub, through a `GitHubClient`: create a release with its asset, and open
/// a pull request on the package index's repository, from a fork of it in the signed-in account.
module GitHubApi =
    /// The package index's repository, which registrations and yanks are pull requests to.
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

    /// Sends `request` with `client`, and gives the JSON GitHub answers when it answers `expected`; or why not, with
    /// GitHub's own message, `what` saying what was being done.
    let private send (client: GitHubClient) (what: string) (expected: int list) (request: GitHubRequest) : Result<JsonNode, string> =
        client request
        |> Result.bind (fun response ->
            let body = if String.IsNullOrWhiteSpace response.Body then null else (try JsonNode.Parse response.Body with :? JsonException -> null)
            if List.contains response.Status expected then
                Result.Ok(if isNull body then JsonObject() :> JsonNode else body)
            else
                let message =
                    match body with
                    | :? JsonObject as answer when answer.ContainsKey "message" -> string answer.["message"]
                    | _ -> response.Body
                Result.Error $"{what} failed: GitHub answered {response.Status}, {message}")

    let private get client what url = send client what [ 200 ] { Method = "GET"; Url = url; Body = None; ContentType = None }

    let private post client what expected url body = send client what expected { Method = "POST"; Url = url; Body = json body; ContentType = None }

    let private text (name: string) (node: JsonNode) = string node.[name]

    /// The login of the account `client` is signed in as.
    let login (client: GitHubClient) : Result<string, string> =
        get client "Finding who you are signed in to GitHub as" $"{api}/user" |> Result.map (text "login")

    /// Creates the release of `repository` tagged `tag`, named `name`, with the asset `assetName` whose bytes are `asset`,
    /// and gives its address. A tag that has a release already is an error: a version can't change.
    let createRelease (client: GitHubClient) (repository: GitHubRepository) (tag: string) (name: string) (assetName: string) (asset: byte array) : Result<string, string> =
        let release = JsonObject()
        release.["tag_name"] <- JsonValue.Create tag
        release.["name"] <- JsonValue.Create name
        match get client $"Looking for a release {tag} of {repository}" $"{api}/repos/{repository}/releases/tags/{Uri.EscapeDataString tag}" with
        | Result.Ok _ -> Result.Error $"{repository} has a release {tag} already, and a published version can't change: publish a new version"
        | Result.Error _ ->
            post client $"Creating the release {tag} of {repository}" [ 201 ] $"{api}/repos/{repository}/releases" release
            |> Result.bind (fun created ->
                let id = text "id" created
                let upload = $"https://uploads.github.com/repos/{repository}/releases/{id}/assets?name={Uri.EscapeDataString assetName}"
                send client $"Uploading {assetName} to the release {tag}" [ 201 ] { Method = "POST"; Url = upload; Body = Some asset; ContentType = Some "application/octet-stream" }
                |> Result.map (fun _ -> text "html_url" created))

    /// The text of the file at `path` on the default branch of the index's repository, with its blob's SHA; none if there
    /// is no such file.
    let indexFile (client: GitHubClient) (path: string) : Result<(string * string) option, string> =
        client { Method = "GET"; Url = $"{api}/repos/{indexRepository}/contents/{path}"; Body = None; ContentType = None }
        |> Result.bind (fun response ->
            match response.Status with
            | 404 -> Result.Ok None
            | 200 ->
                let found = JsonNode.Parse response.Body
                let content = (text "content" found).Replace("\n", "")
                Result.Ok(Some(Encoding.UTF8.GetString(Convert.FromBase64String content), text "sha" found))
            | status -> Result.Error $"Reading {path} from {indexRepository} failed: GitHub answered {status}")

    /// Opens a pull request on the index's repository that writes `contents` to the file at `path` (replacing the blob
    /// `replacing`, for a file that is there already), from a branch `branch` of the signed-in account's fork of it,
    /// made if need be. Gives the pull request's address.
    let openIndexPullRequest
        (client: GitHubClient)
        (path: string)
        (contents: string)
        (replacing: string option)
        (branch: string)
        (title: string)
        (body: string)
        : Result<string, string> =
        login client
        |> Result.bind (fun user ->
            post client $"Forking {indexRepository}" [ 202 ] $"{api}/repos/{indexRepository}/forks" (JsonObject())
            |> Result.bind (fun fork ->
                let fork = text "full_name" fork
                get client $"Reading {indexRepository}'s default branch" $"{api}/repos/{indexRepository}/git/ref/heads/main"
                |> Result.bind (fun head ->
                    let reference = JsonObject()
                    reference.["ref"] <- JsonValue.Create $"refs/heads/{branch}"
                    reference.["sha"] <- JsonValue.Create(string head.["object"].["sha"])
                    post client $"Making the branch {branch} in {fork}" [ 201 ] $"{api}/repos/{fork}/git/refs" reference)
                |> Result.bind (fun _ ->
                    let file = JsonObject()
                    file.["message"] <- JsonValue.Create title
                    file.["content"] <- JsonValue.Create(Convert.ToBase64String(Encoding.UTF8.GetBytes contents))
                    file.["branch"] <- JsonValue.Create branch
                    replacing |> Option.iter (fun sha -> file.["sha"] <- JsonValue.Create sha)
                    send client $"Writing {path} in {fork}" [ 200; 201 ] { Method = "PUT"; Url = $"{api}/repos/{fork}/contents/{path}"; Body = json file; ContentType = None })
                |> Result.bind (fun _ ->
                    let pull = JsonObject()
                    pull.["title"] <- JsonValue.Create title
                    pull.["head"] <- JsonValue.Create $"{user}:{branch}"
                    pull.["base"] <- JsonValue.Create "main"
                    pull.["body"] <- JsonValue.Create body
                    post client $"Opening the pull request on {indexRepository}" [ 201 ] $"{api}/repos/{indexRepository}/pulls" pull)
                |> Result.map (text "html_url")))
