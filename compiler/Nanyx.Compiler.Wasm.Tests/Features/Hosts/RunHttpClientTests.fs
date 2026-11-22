/// The console host provides `Http`, from the shipped package `nanyx.http.core`, on the runtime's own import
/// `nanyx:http`, backed by .NET's `HttpClient` (#1785, see docs/src/content/deep-dives/hosts.md).
module TranspilerWasmTests.Features.Hosts.HttpClient

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open Xunit
open TranspilerWasmTestHelpers

/// The directives and imports of a script that uses `nanyx.http.core`.
let private header =
    """::dependency nanyx.http.core

import nanyx.http.core/http as http

"""

/// What `nanyx run --check-memory` gives for `source` after `header`, saved as `main.nyx` in a directory of its own.
let private run (source: string) =
    use directory = new TempDirectory()
    File.WriteAllText(Path.Combine(directory.Path, "main.nyx"), header + source.ReplaceLineEndings "\n")
    let outcome = nanyxIn directory.Path [ "run"; "--check-memory"; "main.nyx" ]
    { outcome with
        StandardOutput = outcome.StandardOutput.ReplaceLineEndings "\n"
        StandardError = outcome.StandardError.ReplaceLineEndings "\n" }

/// Asserts that `outcome` ran cleanly and printed `expected`.
let private assertPrinted (expected: string) (outcome: RunOutcome) =
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(expected.ReplaceLineEndings "\n", outcome.StandardOutput)
    Assert.Equal(0, outcome.ExitCode)

/// The test server's answer: `/hello` says hello with a header of its own, `/echo` gives back what it was sent, and
/// anything else is not found.
let private echo method path (headers: (string * string) list) body =
    match path with
    | "/hello" -> 200, [ "X-Greeting", "hi" ], "Hello!"
    | "/echo" ->
        let sent = headers |> List.tryFind (fst >> (=) "x-sent") |> Option.map snd |> Option.defaultValue "none"
        201, [], $"{method} {body} {sent}"
    | _ -> 404, [], "Not found"

[<Fact>]
let ``A GET and a POST read the status, the headers and the body`` () =
    use server = new TestServer(echo)
    run $"""match http.get("{server.Url}/hello")
  | #some(response) ->
    println(response.status)
    println(Response.header(response, "X-Greeting"))
    println(response.body)
  | _ -> println("error")

match http.post("{server.Url}/echo", "the body")
  | #some(response) -> println("{{response.status}} {{response.body}}")
  | _ -> println("error")

def request = Request.of(#put, "{server.Url}/echo") with headers = [("X-Sent", "yes")]

match http.send(request with body = "é")
  | #some(response) -> println(response.body)
  | _ -> println("error")

match http.get("{server.Url}/missing")
  | #some(response) -> println("{{response.status}} {{response.body}}")
  | _ -> println("error")
"""
    |> assertPrinted
        """200
#some(hi)
Hello!
201 POST the body none
PUT é yes
404 Not found
"""

[<Fact>]
let ``A request with no answer in its timeout is #timeout`` () =
    // The server holds its answer until the program has ended, however long that takes, so the request can only time
    // out.
    use release = new ManualResetEventSlim()
    use server =
        new TestServer(fun _ _ _ _ ->
            release.Wait()
            200, [], "late")
    let outcome =
        try
            run $"""match http.send(Request.of(#get, "{server.Url}/slow") with timeout = 200)
  | #some(_) -> println("answered")
  | #timeout -> println("timed out")
  | _ -> println("another error")
"""
        finally
            release.Set()
    outcome |> assertPrinted "timed out\n"

[<Fact>]
let ``A server that isn't there, or a URL that isn't one, is an error saying so`` () =
    run $"""match http.get("http://localhost:{freePort ()}/")
  | #connectionFailed(_) -> println("connection failed")
  | _ -> println("?")

match http.get("not a url")
  | #invalidUrl -> println("invalid url")
  | _ -> println("?")

match http.get("ftp://example.com/file")
  | #invalidUrl -> println("invalid url")
  | _ -> println("?")
"""
    |> assertPrinted "connection failed\ninvalid url\ninvalid url\n"

[<Fact>]
let ``A function that waits on a request is called from the top level, which has Async from the host`` () =
    use server = new TestServer(fun _ _ _ _ -> 200, [], "Ada")
    run $"""def fetchName: [Async, Http] string -> string = {{ url ->
  def response = http.get(url) \orCrash("no name")
  response.body
}}

def greetings = {{ urls: list(string) -> urls \list.map {{ url -> "Hello, {{fetchName(url)}}" }} }}

println("Hello, {{fetchName("{server.Url}/name")}}")
println(greetings(["{server.Url}/a", "{server.Url}/b"]))
"""
    |> assertPrinted "Hello, Ada\n[Hello, Ada, Hello, Ada]\n"

[<Fact>]
let ``A program that does not use Http imports nothing for it`` () =
    let wat = transpileWat (withFinalNewline """@entry
def main = { println("hi") }""")
    Assert.DoesNotContain("nanyx:http", wat)
