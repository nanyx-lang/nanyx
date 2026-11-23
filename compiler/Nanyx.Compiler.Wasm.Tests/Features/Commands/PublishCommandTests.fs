/// `nanyx publish` and `nanyx unlist` (#865): a version checked, compared with the newest published, packed and released
/// on the package's own repository, with the pull request that registers a new package; and a version unlisted by a pull
/// request to the index. GitHub is a fake that records what is asked of it, and the index is a directory on disk.
module TranspilerWasmTests.Features.Commands.Publish

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Text.Json.Nodes
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

/// A fake GitHub, signed in as `acme`, which can push to `acme/util`: the releases made, with their assets, the files of
/// the index's repository, the branches made and files written in it or in `acme/packages`, acme's fork of it, and the
/// pull requests opened on it, with every request it was sent. Whether acme can push to the index or fork it, whether
/// opening a pull request fails, and how many looks the fork takes to appear once it is asked for, can be set.
type private FakeGitHub(indexFiles: (string * string) list) =
    let files = Collections.Generic.Dictionary<string, string>(dict indexFiles)
    let releases = ResizeArray<string * string>()
    let assets = ResizeArray<string * byte array>()
    let branches = ResizeArray<string>()
    let written = ResizeArray<string * string>()
    let pulls = ResizeArray<string * string>()
    let requests = ResizeArray<string>()
    let mutable forked = false
    let mutable looksAtFork = 0

    member val CanPushToIndex = false with get, set
    member val PullsFail = false with get, set
    member val LooksBeforeForkAppears = 0 with get, set
    member val CanFork = true with get, set
    member val CanReadReleases = true with get, set

    member _.Releases = List.ofSeq releases
    member _.Assets = List.ofSeq assets
    member _.Branches = List.ofSeq branches
    member _.Written = List.ofSeq written
    member _.Pulls = List.ofSeq pulls
    member _.Requests = List.ofSeq requests

    member this.Client: GitHubClient =
        fun request ->
            let path = request.Url.Replace("https://api.github.com", "").Replace("https://uploads.github.com", "")
            requests.Add $"{request.Method} {path}"
            let body () = JsonNode.Parse(Encoding.UTF8.GetString request.Body.Value)
            let answer status (json: string) = Result.Ok { Status = status; Body = json }
            let permissions (push: bool) = answer 200 $$"""{ "permissions": { "pull": true, "push": {{(if push then "true" else "false")}} } }"""
            let canWrite owner = owner = "nanyx-lang" && this.CanPushToIndex || owner = "acme" && forked
            let segments = (path.Split('?')[0]).TrimStart('/').Split('/') |> List.ofArray
            match request.Method, segments with
            | "GET", [ "repos"; "acme"; "util" ] -> permissions true
            | "GET", [ "repos"; "nanyx-lang"; "packages" ] -> permissions this.CanPushToIndex
            | "GET", [ "repos"; "acme"; "packages" ] ->
                looksAtFork <- looksAtFork + 1
                if forked && looksAtFork > this.LooksBeforeForkAppears then permissions true else answer 404 """{ "message": "Not Found" }"""
            | "GET", [ "repos"; _; _; "releases"; "tags"; _ ] when not this.CanReadReleases -> answer 403 """{ "message": "Resource not accessible by personal access token" }"""
            | "GET", [ "repos"; owner; repo; "releases"; "tags"; tag ] when releases.Contains($"{owner}/{repo}", tag) ->
                let listed =
                    assets
                    |> Seq.map (fun (name, bytes) -> $$"""{ "name": "{{name}}", "digest": "sha256:{{PackageArchive.sha256 bytes}}" }""")
                    |> String.concat ", "
                answer 200 $$"""{ "id": 1, "html_url": "https://github.com/{{owner}}/{{repo}}/releases/tag/{{tag}}", "assets": [{{listed}}] }"""
            | "POST", [ "repos"; owner; repo; "releases" ] ->
                let tag = string (body ()).["tag_name"]
                releases.Add($"{owner}/{repo}", tag)
                answer 201 $$"""{ "id": 1, "html_url": "https://github.com/{{owner}}/{{repo}}/releases/tag/{{tag}}" }"""
            | "POST", [ "repos"; _; _; "releases"; "1"; "assets" ] ->
                assets.Add(Uri.UnescapeDataString(request.Url.Substring(request.Url.IndexOf "name=" + 5)), request.Body.Value)
                answer 201 "{}"
            | "GET", "repos" :: "nanyx-lang" :: "packages" :: "contents" :: file ->
                match files.TryGetValue(String.concat "/" file) with
                | true, text ->
                    let content = Convert.ToBase64String(Encoding.UTF8.GetBytes text)
                    answer 200 $$"""{ "content": "{{content}}", "sha": "blob1" }"""
                | _ -> answer 404 """{ "message": "Not Found" }"""
            | "POST", [ "repos"; "nanyx-lang"; "packages"; "forks" ] when not this.CanFork -> answer 403 """{ "message": "Resource not accessible by personal access token" }"""
            | "POST", [ "repos"; "nanyx-lang"; "packages"; "forks" ] ->
                forked <- true
                answer 202 """{ "owner": { "login": "acme" }, "name": "packages" }"""
            | "GET", [ "repos"; "nanyx-lang"; "packages"; "git"; "ref"; "heads"; "main" ] -> answer 200 """{ "object": { "sha": "c0ffee" } }"""
            | "GET", [ "repos"; owner; "packages"; "git"; "ref"; "heads"; branch ] when branches.Contains $"{owner}/packages:{branch}" -> answer 200 "{}"
            | "POST", [ "repos"; owner; "packages"; "git"; "refs" ] when canWrite owner ->
                branches.Add $"""{owner}/packages:{(string (body ()).["ref"]).Replace("refs/heads/", "")}"""
                answer 201 "{}"
            | "PUT", "repos" :: owner :: "packages" :: "contents" :: file when canWrite owner ->
                written.Add(String.concat "/" file, Encoding.UTF8.GetString(Convert.FromBase64String(string (body ()).["content"])))
                answer 201 "{}"
            | "POST", [ "repos"; "nanyx-lang"; "packages"; "pulls" ] when this.PullsFail -> answer 403 """{ "message": "Resource not accessible by personal access token" }"""
            | "POST", [ "repos"; "nanyx-lang"; "packages"; "pulls" ] ->
                let pull = body ()
                pulls.Add(string pull.["title"], string pull.["head"])
                answer 201 """{ "html_url": "https://github.com/nanyx-lang/packages/pull/7" }"""
            | _ -> answer 404 """{ "message": "Not Found" }"""

/// acme.util at `version`, in `util`, released on `acme/util`, whose module `strings` is `source`.
let private util (directory: TempDirectory) (version: string) (source: string) =
    directory.Write "util/util.nyxp" $"(package = (name = \"acme.util\", version = \"{version}\", repository = \"acme/util\"))\n"
    directory.Write "util/strings.nyx" source
    Path.Combine(directory.Path, "util")

let private shout = "export module strings\n\nexport def shout = { (s: string) -> s ++ \"!\" }\n"

let private shoutAndWhisper = shout + "\nexport def whisper = { (s: string) -> s }\n"

/// A store whose index is an empty directory in `directory`, or, with `published`, lists acme.util as it is in `util`.
let private storeIn (directory: TempDirectory) (published: bool) =
    let store = { Cache = Path.Combine(directory.Path, "cache"); Index = Path.Combine(directory.Path, "index"); GitHub = GitHub.unsupported }
    directory.Write "index/config.json" """{ "format": 1, "index": "index/" }"""
    if published then publishToIndex store (Path.Combine(directory.Path, "util")) [] "" |> ignore
    store

let private registered = [ "registry/acme/acme.util.json", """{ "name": "acme.util", "repository": "https://github.com/acme/util" }""" ]

[<Fact>]
let ``A package's first version is released, and a pull request registers it`` () =
    use directory = new TempDirectory()
    let app = util directory "0.1.0" shout
    let store = storeIn directory false
    let github = FakeGitHub []
    let outcome = nanyxWithGitHub store github.Client app [ "publish" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(
        "Publishing acme.util 0.1.0 to acme/util\n"
        + "Checking that it builds\n"
        + "Checking that it is formatted\n"
        + "Comparing its API with the newest version published\n"
        + "It is the first version, so there is nothing to compare\n"
        + "Packing it\n"
        + "Checking that you can release on acme/util\n"
        + "Checking that you can open the pull request that registers it on nanyx-lang/packages\n"
        + "Creating the release v0.1.0 of acme/util\n"
        + "Released: https://github.com/acme/util/releases/tag/v0.1.0\n"
        + "Opening the pull request that registers acme.util with the package index\n"
        + "Opened https://github.com/nanyx-lang/packages/pull/7: once a maintainer merges it, the index records acme.util within the hour\n",
        outcome.StandardOutput.ReplaceLineEndings "\n"
    )
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal<(string * string) list>([ "acme/util", "v0.1.0" ], github.Releases)
    let name, asset = List.exactlyOne github.Assets
    Assert.Equal("acme.util-0.1.0.nyxpkg", name)
    use zip = new ZipArchive(new MemoryStream(asset))
    Assert.Equal<string list>([ "api.json"; "strings.nyx"; "util.nyxp" ], zip.Entries |> Seq.map _.FullName |> List.ofSeq)
    Assert.Equal<(string * string) list>(
        [ "registry/acme/acme.util.json", "{\n  \"name\": \"acme.util\",\n  \"repository\": \"https://github.com/acme/util\"\n}\n" ],
        github.Written |> List.map (fun (path, text) -> path, text.ReplaceLineEndings "\n")
    )
    Assert.Equal<(string * string) list>([ "Register acme.util", "acme:register-acme.util" ], github.Pulls)
    Assert.Equal<string list>([ "acme/packages:register-acme.util" ], github.Branches)

[<Fact>]
let ``A patch of a registered package is released, with no pull request`` () =
    use directory = new TempDirectory()
    util directory "1.0.0" shout |> ignore
    let store = storeIn directory true
    let app = util directory "1.0.1" (shout.Replace("\"!\"", "\"!!\""))
    let github = FakeGitHub registered
    let outcome = nanyxWithGitHub store github.Client app [ "publish" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Contains("1.0.1 is a big enough bump from 1.0.0\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.EndsWith("Published acme.util 1.0.1: the index records it within the hour\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal<(string * string) list>([ "acme/util", "v1.0.1" ], github.Releases)
    Assert.Empty(github.Pulls)

[<Fact>]
let ``An account that can push to the index registers from a branch of it, with a suffix when its name is taken`` () =
    use directory = new TempDirectory()
    let app = util directory "0.1.0" shout
    let store = storeIn directory false
    let github = FakeGitHub []
    github.CanPushToIndex <- true
    github.PullsFail <- true
    Assert.Equal(1, (nanyxWithGitHub store github.Client app [ "publish" ]).ExitCode)
    github.PullsFail <- false
    let outcome = nanyxWithGitHub store github.Client app [ "publish" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal<string list>([ "nanyx-lang/packages:register-acme.util"; "nanyx-lang/packages:register-acme.util-2" ], github.Branches)
    Assert.Equal<(string * string) list>([ "Register acme.util", "register-acme.util-2" ], github.Pulls)
    Assert.DoesNotContain("POST /repos/nanyx-lang/packages/forks", github.Requests)

[<Fact>]
let ``Registering waits for the fork GitHub is still making`` () =
    use directory = new TempDirectory()
    let app = util directory "0.1.0" shout
    let store = storeIn directory false
    let github = FakeGitHub []
    github.LooksBeforeForkAppears <- 1
    let outcome = nanyxWithGitHub store github.Client app [ "publish" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal(2, github.Requests |> List.filter ((=) "GET /repos/acme/packages") |> List.length)
    Assert.Equal<(string * string) list>([ "Register acme.util", "acme:register-acme.util" ], github.Pulls)

[<Fact>]
let ``A publish whose registration failed is finished by running it again, which leaves the release as it is`` () =
    use directory = new TempDirectory()
    let app = util directory "0.1.0" shout
    let store = storeIn directory false
    let github = FakeGitHub []
    github.PullsFail <- true
    let failed = nanyxWithGitHub store github.Client app [ "publish" ]
    Assert.Equal(
        "error: Opening the pull request on nanyx-lang/packages failed: GitHub answered 403, Resource not accessible by personal access token\n"
        + "error: The release is made: run `nanyx publish` again to open the pull request\n",
        failed.StandardError.ReplaceLineEndings "\n"
    )
    Assert.Equal(1, failed.ExitCode)
    github.PullsFail <- false
    let again = nanyxWithGitHub store github.Client app [ "publish" ]
    Assert.Equal("", again.StandardError)
    Assert.Contains(
        "Released already, with this .nyxpkg, by a publish that didn't finish: https://github.com/acme/util/releases/tag/v0.1.0\n",
        again.StandardOutput.ReplaceLineEndings "\n"
    )
    Assert.Equal(0, again.ExitCode)
    Assert.Equal<(string * string) list>([ "acme/util", "v0.1.0" ], github.Releases)
    Assert.Equal(1, github.Assets.Length)
    Assert.Equal<(string * string) list>([ "Register acme.util", "acme:register-acme.util-2" ], github.Pulls)

[<Fact>]
let ``Running a publish again with a changed package is refused`` () =
    use directory = new TempDirectory()
    let app = util directory "0.1.0" shout
    let store = storeIn directory false
    let github = FakeGitHub []
    github.PullsFail <- true
    Assert.Equal(1, (nanyxWithGitHub store github.Client app [ "publish" ]).ExitCode)
    github.PullsFail <- false
    util directory "0.1.0" shoutAndWhisper |> ignore
    let changed = nanyxWithGitHub store github.Client app [ "publish" ]
    Assert.Equal(
        "error: acme/util has a release v0.1.0 already, with a different acme.util-0.1.0.nyxpkg, and a published version can't change: publish a new version\n",
        changed.StandardError.ReplaceLineEndings "\n"
    )
    Assert.Equal(1, changed.ExitCode)
    Assert.Equal(1, github.Assets.Length)
    Assert.Empty(github.Pulls)

[<Fact>]
let ``A publish that can't reach the index fails before it creates the release`` () =
    use directory = new TempDirectory()
    let app = util directory "0.1.0" shout
    let store = storeIn directory false
    let github = FakeGitHub []
    github.CanFork <- false
    let outcome = nanyxWithGitHub store github.Client app [ "publish" ]
    Assert.Equal(
        "error: Forking nanyx-lang/packages failed: GitHub answered 403, Resource not accessible by personal access token\n",
        outcome.StandardError.ReplaceLineEndings "\n"
    )
    Assert.Equal(1, outcome.ExitCode)
    Assert.Empty(github.Releases)

[<Fact>]
let ``A release that can't be looked for is an error with GitHub's reason, and nothing is released`` () =
    use directory = new TempDirectory()
    let app = util directory "0.1.0" shout
    let store = storeIn directory false
    let github = FakeGitHub []
    github.CanReadReleases <- false
    let outcome = nanyxWithGitHub store github.Client app [ "publish" ]
    Assert.Equal(
        "error: Looking for a release v0.1.0 of acme/util failed: GitHub answered 403, Resource not accessible by personal access token\n",
        outcome.StandardError.ReplaceLineEndings "\n"
    )
    Assert.Equal(1, outcome.ExitCode)
    Assert.Empty(github.Releases)

[<Fact>]
let ``A bump too small is refused, listing the changes that need a bigger one, and nothing is released`` () =
    use directory = new TempDirectory()
    util directory "1.0.0" shoutAndWhisper |> ignore
    let store = storeIn directory true
    let app = util directory "1.0.1" shout
    let github = FakeGitHub registered
    let outcome = nanyxWithGitHub store github.Client app [ "publish" ]
    Assert.Equal(
        "error: acme.util 1.0.1 is too small a bump from 1.0.0 for what changed, which needs 2.0.0 at least:\n"
        + "error:   strings's def whisper was removed, or is no longer exported\n"
        + "error: Run `nanyx bump` to set the version these changes need\n",
        outcome.StandardError.ReplaceLineEndings "\n"
    )
    Assert.Equal(1, outcome.ExitCode)
    Assert.Empty(github.Requests)

[<Fact>]
let ``A version published already, a package with no repository, and one not formatted are refused`` () =
    use directory = new TempDirectory()
    let app = util directory "1.0.0" shout
    let store = storeIn directory true
    let github = FakeGitHub registered
    let again = nanyxWithGitHub store github.Client app [ "publish" ]
    Assert.Equal(
        "error: acme.util 1.0.0 is published already, and a published version can't change: run `nanyx bump` to choose the next version\n",
        again.StandardError.ReplaceLineEndings "\n"
    )
    Assert.Equal(1, again.ExitCode)
    directory.Write "util/util.nyxp" "(package = (name = \"acme.util\", version = \"1.1.0\"))\n"
    let noRepository = nanyxWithGitHub store github.Client app [ "publish" ]
    Assert.Equal(
        "error: acme.util has no `repository`, the GitHub repository whose releases are its versions: add `repository = \"owner/name\"` to the `package` in util.nyxp\n",
        noRepository.StandardError.ReplaceLineEndings "\n"
    )
    util directory "1.1.0" (shout.Replace("def shout = ", "def shout =  ")) |> ignore
    let unformatted = nanyxWithGitHub store github.Client app [ "publish" ]
    Assert.Equal("error: strings.nyx isn't formatted: run `nanyx format --write` on the package\n", unformatted.StandardError.ReplaceLineEndings "\n")
    Assert.Empty(github.Requests)

/// The index file of acme.util, listing 1.0.0 and 1.1.0, the second unlisted already when `unlistedAs` gives the field
/// that says so: `unlisted`, or `yanked`, as an older index file writes it.
let private indexFile (unlistedAs: string option) =
    let entry (version: string) (field: string option) =
        let unlisted = match field with Some name -> $", \"{name}\": true" | None -> ""
        $$"""{ "version": "{{version}}", "url": "u", "sha256": "s", "nanyx": null, "dependencies": {}{{unlisted}}, "available": true }"""
    $$"""{ "name": "acme.util", "repository": "https://github.com/acme/util", "versions": [{{entry "1.0.0" None}}, {{entry "1.1.0" unlistedAs}}] }"""

[<Fact>]
let ``Unlisting opens the pull request that marks the version unlisted in the index`` () =
    use directory = new TempDirectory()
    let store = storeIn directory false
    let github = FakeGitHub [ "index/acme/acme.util.json", indexFile None ]
    let outcome = nanyxWithGitHub store github.Client directory.Path [ "unlist"; "acme.util"; "1.1.0" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(
        "Opening the pull request that unlists acme.util 1.1.0\n"
        + "Opened https://github.com/nanyx-lang/packages/pull/7: once it is merged, new resolutions pass over acme.util 1.1.0\n",
        outcome.StandardOutput.ReplaceLineEndings "\n"
    )
    Assert.Equal(0, outcome.ExitCode)
    let path, text = List.exactlyOne github.Written
    Assert.Equal("index/acme/acme.util.json", path)
    let versions =
        JsonNode.Parse(text).["versions"].AsArray()
        |> Seq.map (fun entry -> string entry.["version"], (entry.["unlisted"] <> null && entry.["unlisted"].GetValue<bool>()))
        |> List.ofSeq
    Assert.Equal<(string * bool) list>([ "1.0.0", false; "1.1.0", true ], versions)
    Assert.Equal<(string * string) list>([ "Unlist acme.util 1.1.0", "acme:unlist-acme.util-1.1.0" ], github.Pulls)

[<Fact>]
let ``An account that can push to the index unlists from a branch of it`` () =
    use directory = new TempDirectory()
    let store = storeIn directory false
    let github = FakeGitHub [ "index/acme/acme.util.json", indexFile None ]
    github.CanPushToIndex <- true
    let outcome = nanyxWithGitHub store github.Client directory.Path [ "unlist"; "acme.util"; "1.1.0" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal<string list>([ "nanyx-lang/packages:unlist-acme.util-1.1.0" ], github.Branches)
    Assert.Equal<(string * string) list>([ "Unlist acme.util 1.1.0", "unlist-acme.util-1.1.0" ], github.Pulls)

[<Fact>]
let ``A version unlisted already, one the index doesn't have, or a package it doesn't have can't be unlisted`` () =
    use directory = new TempDirectory()
    let store = storeIn directory false
    let unlist (github: FakeGitHub) (package: string) (version: string) =
        (nanyxWithGitHub store github.Client directory.Path [ "unlist"; package; version ]).StandardError.ReplaceLineEndings "\n"
    for field in [ "unlisted"; "yanked" ] do
        Assert.Equal("error: acme.util 1.1.0 is unlisted already\n", unlist (FakeGitHub [ "index/acme/acme.util.json", indexFile (Some field) ]) "acme.util" "1.1.0")
    Assert.Equal("error: The index has no acme.util 2.0.0 to unlist\n", unlist (FakeGitHub [ "index/acme/acme.util.json", indexFile None ]) "acme.util" "2.0.0")
    Assert.Equal("error: The package index has no package 'acme.nope'\n", unlist (FakeGitHub []) "acme.nope" "1.0.0")
