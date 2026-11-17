/// The console host provides `Http`, from the shipped package `nanyx.http.core`, on the runtime's own import
/// `nanyx:http`, backed by .NET's `HttpClient` (#1785, see docs/src/content/work-in-progress/files-http-json.md).
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

/// A port nothing is listening on, as the system gives it for a moment.
let private freePort () =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    listener.Stop()
    port

/// A server on a port of its own on this machine, for as long as it is held, that answers each request with what
/// `answer` makes of its method, path, headers and body: a status, headers and a body.
type private TestServer(answer: string -> string -> (string * string) list -> string -> int * (string * string) list * string) =
    let port = freePort ()
    let listener = new HttpListener()
    do
        listener.Prefixes.Add($"http://localhost:{port}/")
        listener.Start()
    let serving =
        Thread(
            (fun () ->
                try
                    while listener.IsListening do
                        let context = listener.GetContext()
                        let request = context.Request
                        let headers = [ for name in request.Headers.AllKeys -> name.ToLowerInvariant(), request.Headers.[name] ]
                        let body = (new StreamReader(request.InputStream)).ReadToEnd()
                        let status, answerHeaders, text = answer request.HttpMethod request.Url.AbsolutePath headers body
                        try
                            context.Response.StatusCode <- status
                            for name, value in answerHeaders do
                                context.Response.Headers.Add(name, value)
                            let bytes = Encoding.UTF8.GetBytes(text: string)
                            context.Response.OutputStream.Write(bytes, 0, bytes.Length)
                            context.Response.Close()
                        with _ -> ()
                with _ -> ()),
            IsBackground = true)
    do serving.Start()

    /// Where it is: `http://localhost:<port>`.
    member _.Url = $"http://localhost:{port}"

    interface IDisposable with
        member _.Dispose() =
            listener.Stop()
            listener.Close()

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
  | #error(_) -> println("error")

match http.post("{server.Url}/echo", "the body")
  | #some(response) -> println("{{response.status}} {{response.body}}")
  | #error(_) -> println("error")

def request = Request.of(#put, "{server.Url}/echo") with headers = [("X-Sent", "yes")]

match http.send(request with body = "é")
  | #some(response) -> println(response.body)
  | #error(_) -> println("error")

match http.get("{server.Url}/missing")
  | #some(response) -> println("{{response.status}} {{response.body}}")
  | #error(_) -> println("error")
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
    use server =
        new TestServer(fun _ _ _ _ ->
            Thread.Sleep 3000
            200, [], "late")
    run $"""match http.send(Request.of(#get, "{server.Url}/slow") with timeout = 200)
  | #some(_) -> println("answered")
  | #error(#timeout) -> println("timed out")
  | #error(_) -> println("another error")
"""
    |> assertPrinted "timed out\n"

[<Fact>]
let ``A server that isn't there, or a URL that isn't one, is an error saying so`` () =
    run $"""match http.get("http://localhost:{freePort ()}/")
  | #error(#connectionFailed(_)) -> println("connection failed")
  | _ -> println("?")

match http.get("not a url")
  | #error(#invalidUrl) -> println("invalid url")
  | _ -> println("?")

match http.get("ftp://example.com/file")
  | #error(#invalidUrl) -> println("invalid url")
  | _ -> println("?")
"""
    |> assertPrinted "connection failed\ninvalid url\ninvalid url\n"

[<Fact>]
let ``A program that does not use Http imports nothing for it`` () =
    let wat = transpileWat (withFinalNewline """@entry
def main = { println("hi") }""")
    Assert.DoesNotContain("nanyx:http", wat)
