/// `nanyx publish` and `nanyx yank` (#865): a version checked, compared with the newest published, packed and released
/// on the package's own repository, with the pull request that registers a new package; and a version yanked by a pull
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

/// A fake GitHub, signed in as `acme`: the releases made, the files of the index's repository, and the pull requests
/// opened on it, with every request it was sent.
type private FakeGitHub(indexFiles: (string * string) list) =
    let files = Collections.Generic.Dictionary<string, string>(dict indexFiles)
    let releases = ResizeArray<string * string>()
    let assets = ResizeArray<string * byte array>()
    let written = ResizeArray<string * string>()
    let pulls = ResizeArray<string * string>()
    let requests = ResizeArray<string>()

    member _.Releases = List.ofSeq releases
    member _.Assets = List.ofSeq assets
    member _.Written = List.ofSeq written
    member _.Pulls = List.ofSeq pulls
    member _.Requests = List.ofSeq requests

    member _.Client: GitHubClient =
        fun request ->
            let path = request.Url.Replace("https://api.github.com", "").Replace("https://uploads.github.com", "")
            requests.Add $"{request.Method} {path}"
            let body () = JsonNode.Parse(Encoding.UTF8.GetString request.Body.Value)
            let answer status (json: string) = Result.Ok { Status = status; Body = json }
            let segments = (path.Split('?')[0]).TrimStart('/').Split('/') |> List.ofArray
            match request.Method, segments with
            | "GET", [ "user" ] -> answer 200 """{ "login": "acme" }"""
            | "GET", [ "repos"; owner; repo; "releases"; "tags"; tag ] when releases.Contains($"{owner}/{repo}", tag) -> answer 200 "{}"
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
            | "POST", [ "repos"; "nanyx-lang"; "packages"; "forks" ] -> answer 202 """{ "full_name": "acme/packages" }"""
            | "GET", [ "repos"; "nanyx-lang"; "packages"; "git"; "ref"; "heads"; "main" ] -> answer 200 """{ "object": { "sha": "c0ffee" } }"""
            | "POST", [ "repos"; "acme"; "packages"; "git"; "refs" ] -> answer 201 "{}"
            | "PUT", "repos" :: "acme" :: "packages" :: "contents" :: file ->
                written.Add(String.concat "/" file, Encoding.UTF8.GetString(Convert.FromBase64String(string (body ()).["content"])))
                answer 201 "{}"
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

/// The index file of acme.util, listing 1.0.0 and 1.1.0, the second `yanked` already when `alreadyYanked`.
let private indexFile (alreadyYanked: bool) =
    let entry (version: string) (yanked: bool) =
        $$"""{ "version": "{{version}}", "url": "u", "sha256": "s", "nanyx": null, "dependencies": {}, "yanked": {{(if yanked then "true" else "false")}}, "available": true }"""
    $$"""{ "name": "acme.util", "repository": "https://github.com/acme/util", "versions": [{{entry "1.0.0" false}}, {{entry "1.1.0" alreadyYanked}}] }"""

[<Fact>]
let ``Yanking opens the pull request that marks the version yanked in the index`` () =
    use directory = new TempDirectory()
    let store = storeIn directory false
    let github = FakeGitHub [ "index/acme/acme.util.json", indexFile false ]
    let outcome = nanyxWithGitHub store github.Client directory.Path [ "yank"; "acme.util"; "1.1.0" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(
        "Opening the pull request that yanks acme.util 1.1.0\n"
        + "Opened https://github.com/nanyx-lang/packages/pull/7: once it is merged, new resolutions pass over acme.util 1.1.0\n",
        outcome.StandardOutput.ReplaceLineEndings "\n"
    )
    Assert.Equal(0, outcome.ExitCode)
    let path, text = List.exactlyOne github.Written
    Assert.Equal("index/acme/acme.util.json", path)
    let versions = JsonNode.Parse(text).["versions"].AsArray() |> Seq.map (fun entry -> string entry.["version"], entry.["yanked"].GetValue<bool>()) |> List.ofSeq
    Assert.Equal<(string * bool) list>([ "1.0.0", false; "1.1.0", true ], versions)
    Assert.Equal<(string * string) list>([ "Yank acme.util 1.1.0", "acme:yank-acme.util-1.1.0" ], github.Pulls)

[<Fact>]
let ``A version yanked already, one the index doesn't have, or a package it doesn't have can't be yanked`` () =
    use directory = new TempDirectory()
    let store = storeIn directory false
    let yank (github: FakeGitHub) (package: string) (version: string) = (nanyxWithGitHub store github.Client directory.Path [ "yank"; package; version ]).StandardError.ReplaceLineEndings "\n"
    Assert.Equal("error: acme.util 1.1.0 is yanked already\n", yank (FakeGitHub [ "index/acme/acme.util.json", indexFile true ]) "acme.util" "1.1.0")
    Assert.Equal("error: The index has no acme.util 2.0.0 to yank\n", yank (FakeGitHub [ "index/acme/acme.util.json", indexFile false ]) "acme.util" "2.0.0")
    Assert.Equal("error: The package index has no package 'acme.nope'\n", yank (FakeGitHub []) "acme.nope" "1.0.0")
