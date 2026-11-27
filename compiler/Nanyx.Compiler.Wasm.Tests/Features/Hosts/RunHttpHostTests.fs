/// The HTTP host, `nanyx.http.host`, whose entry is a `Request -> Response` handler: `nanyx run` serves it on the port of the
/// host's `port` setting, answering each request with a fresh instance of the program (#1786, see
/// docs/src/content/deep-dives/hosts.md).
module TranspilerWasmTests.Features.Hosts.HttpHost

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open Xunit
open TranspilerWasmTestHelpers

/// The program of the HTTP server cookbook page, its ```nanyx block that is the module `main`: a fragment there, since
/// it is one file of a package, which is run here, under the HTTP host.
let private cookbookProgram () =
    let page = Path.Combine(docsContentDirectory, "cookbook", "http-server.md")
    docExamplesOf page |> Map.values |> Seq.map _.Code |> Seq.find _.StartsWith("module main\n")

/// A package in `directory` whose host is the HTTP host, serving on `port`, with `main` as its one source file.
let private writePackage (directory: string) (port: int) (main: string) =
    let manifest =
        $"""(
  package = (name = "server", version = "0.1.0", sources = ["src/**/*.nyx"])
  host = (package = "nanyx.http.host", version = "{NyxCompiler.Toolchain.version}", settings = (port = {port}))
  dependencies = [(package = "nanyx.http", version = "{NyxCompiler.Toolchain.version}")]
)
"""
    Directory.CreateDirectory(Path.Combine(directory, "src")) |> ignore
    File.WriteAllText(Path.Combine(directory, "server.nyxp"), manifest)
    File.WriteAllText(Path.Combine(directory, "src", "main.nyx"), main)

/// Runs `nanyx run` on the package in `directory`, which serves on `port`, calls `requests` with a client once it
/// answers, then stops it, and gives what the run printed.
let private serving (directory: string) (port: int) (requests: HttpClient -> unit) : RunOutcome =
    use stop = new CancellationTokenSource()
    // On a thread of its own, not the thread pool's, which it would hold for as long as it serves: the pool also
    // finishes the requests it answers, and the test's own, and on a loaded machine is slow to make up a thread it lacks
    // (#2367).
    let run = Task.Factory.StartNew((fun () -> nanyxInUntil stop.Token directory [ "run" ]), TaskCreationOptions.LongRunning)
    use client = new HttpClient(BaseAddress = Uri $"http://localhost:{port}", Timeout = TimeSpan.FromSeconds 30.0)
    let deadline = DateTime.UtcNow.AddSeconds 60.0
    let rec waitForServer () =
        let answered =
            try
                client.GetAsync("/").GetAwaiter().GetResult() |> ignore
                true
            with :? HttpRequestException -> false
        if not answered then
            if run.IsCompleted || DateTime.UtcNow > deadline then
                stop.Cancel()
                failwith $"The server didn't start:\n{run.Result.StandardError}"
            Thread.Sleep 200
            waitForServer ()
    try
        waitForServer ()
        requests client
    finally
        stop.Cancel()
    let outcome = run.Result
    { outcome with StandardError = outcome.StandardError.ReplaceLineEndings "\n" }

/// The status and body of the answer to GET `path`.
let private get (client: HttpClient) (path: string) =
    let response = client.GetAsync(path: string).GetAwaiter().GetResult()
    int response.StatusCode, response.Content.ReadAsStringAsync().GetAwaiter().GetResult()

[<Fact>]
let ``nanyx run serves the HTTP server cookbook's program`` () =
    use directory = new TempDirectory()
    let port = freePort ()
    writePackage directory.Path port (cookbookProgram ())
    let outcome =
        serving directory.Path port (fun client ->
            Assert.Equal((200, "Welcome to Nanyx!"), get client "/")
            Assert.Equal((200, "Hello, Ada!"), get client "/hello/Ada")
            Assert.Equal((200, "Hello, Grace!"), get client "/hello/Grace?greeting=hi")
            Assert.Equal((404, "Not Found"), get client "/goodbye"))
    Assert.Equal(0, outcome.ExitCode)
    Assert.Contains($"Serving http://localhost:{port}/ (Ctrl+C to stop)", outcome.StandardError)

[<Fact>]
let ``A handler that traps answers its request with a 500, and the next request is answered`` () =
    use directory = new TempDirectory()
    let port = freePort ()
    writePackage directory.Path port """module main

@entry
def main: Request -> Response = { request ->
  match Request.path(request)
    | "/divide" -> Response.text(200, "{10 // string.length(request.body)}")
    | path -> Response.text(200, "{request.method} {path} {request.body}")
}
"""
    let outcome =
        serving directory.Path port (fun client ->
            Assert.Equal((500, "Internal Server Error"), get client "/divide")
            let posted = client.PostAsync("/echo", new StringContent("hello")).GetAwaiter().GetResult()
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode)
            Assert.Equal("#post /echo hello", posted.Content.ReadAsStringAsync().GetAwaiter().GetResult())
            Assert.Equal((200, "#get /after "), get client "/after"))
    Assert.Equal(0, outcome.ExitCode)
    Assert.Contains("error: the program stopped while answering GET /divide", outcome.StandardError)

[<Fact>]
let ``A handler that crashes answers with a 500, and the server's log says where it crashed and why`` () =
    use directory = new TempDirectory()
    let port = freePort ()
    writePackage directory.Path port """module main

@entry
def main: Request -> Response = { request ->
  if Request.path(request) == "/crash" then crash("no such page") else Response.text(200, "ok")
}
"""
    let outcome = serving directory.Path port (fun client -> Assert.Equal((500, "Internal Server Error"), get client "/crash"))
    Assert.Equal(0, outcome.ExitCode)
    Assert.Contains("it crashed at src/main.nyx:5: no such page", outcome.StandardError.ReplaceLineEndings "\n")

[<Fact>]
let ``What a handler's dbg prints goes to the server's log with its request, not into the response (#2058)`` () =
    use directory = new TempDirectory()
    let port = freePort ()
    writePackage directory.Path port """module main

@entry
def main: Request -> Response = { request ->
  def path = dbg(Request.path(request))
  Response.text(200, "at {path}")
}
"""
    let outcome = serving directory.Path port (fun client -> Assert.Equal((200, "at /seen"), get client "/seen"))
    Assert.Contains("dbg (GET /seen): [src/main.nyx:5] \"/seen\"\n", outcome.StandardError)
    Assert.DoesNotContain("dbg", outcome.StandardOutput)

[<Fact>]
let ``A handler reads the time through the host's Clock`` () =
    use directory = new TempDirectory()
    let port = freePort ()
    writePackage directory.Path port """module main

@entry
def main: [Clock] Request -> Response = { _ ->
  def start = ticks()
  Response.text(200, "{now() \since(Instant.epoch) // 1ms} {ticks() >= start}")
}
"""
    let before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    let outcome =
        serving directory.Path port (fun client ->
            let status, body = get client "/time"
            let after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            Assert.Equal(200, status)
            match body.Split ' ' with
            | [| time; rising |] ->
                Assert.InRange(Int64.Parse time, before - 1000L, after + 1000L)
                Assert.Equal("true", rising)
            | _ -> Assert.Fail $"Unexpected body: {body}")
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``A handler makes requests of its own, through Http`` () =
    use directory = new TempDirectory()
    let port = freePort ()
    writePackage directory.Path port $"""module main

import nanyx.http/http as http

@entry
def main: [Async, Http] Request -> Response = {{ request ->
  match Request.path(request)
    | "/proxy" ->
      match http.get("http://localhost:{port}/inner")
        | #some(inner) -> Response.text(200, "proxied: {{inner.body}}")
        | _ -> Response.text(502, "Bad Gateway")
    | _ -> Response.text(200, "inner")
}}
"""
    serving directory.Path port (fun client -> Assert.Equal((200, "proxied: inner"), get client "/proxy")) |> ignore

[<Fact>]
let ``The HTTP host provides no Console`` () =
    use directory = new TempDirectory()
    writePackage directory.Path (freePort ()) """module main

@entry
def main: Request -> Response = { request ->
  println("hi")
  Response.text(200, "")
}
"""
    let outcome = nanyxIn directory.Path [ "check" ]
    Assert.Equal(1, outcome.ExitCode)
    assertDiagnostic "NYX0102" "Unknown function 'println'" "" "'println' is not in scope" (outcome.StandardOutput + outcome.StandardError)

[<Theory>]
[<InlineData("(colour = \"red\")", "NYX0021", "The host 'nanyx.http.host' takes no setting 'colour': it takes port")>]
[<InlineData("(port = \"3000\")", "NYX0023", "The host's setting 'port' is 3000 by default, so it can't be the string \"3000\"")>]
let ``A setting the host doesn't take, or of another kind, is an error at the setting`` (settings: string) (code: string) (message: string) =
    use directory = new TempDirectory()
    let manifest =
        $"""(
  package = (name = "server", version = "0.1.0", sources = ["src/**/*.nyx"])
  host = (package = "nanyx.http.host", version = "{NyxCompiler.Toolchain.version}", settings = {settings})
)
"""
    Directory.CreateDirectory(Path.Combine(directory.Path, "src")) |> ignore
    File.WriteAllText(Path.Combine(directory.Path, "server.nyxp"), manifest)
    File.WriteAllText(Path.Combine(directory.Path, "src", "main.nyx"), cookbookProgram ())
    let outcome = nanyxIn directory.Path [ "check" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains($"error {code}: {message}", outcome.StandardError)

[<Fact>]
let ``A request is answered while another's handler waits on a request of its own`` () =
    use slow = new HttpListener()
    let slowPort = freePort ()
    slow.Prefixes.Add $"http://localhost:{slowPort}/"
    slow.Start()
    // The slow server holds its answer until the quick request has been answered, so the order of the answers is
    // what the test checks, not how long either took.
    use received = new ManualResetEventSlim()
    use release = new ManualResetEventSlim()
    // On a thread of its own, as `serving` runs the server, since it holds it until the test releases it.
    let answeringSlowly =
        Task.Factory.StartNew((fun () ->
            try
                let context = slow.GetContext()
                received.Set()
                release.Wait()
                let bytes = Text.Encoding.UTF8.GetBytes "late"
                context.Response.OutputStream.Write(bytes, 0, bytes.Length)
                context.Response.Close()
            with _ -> ()), TaskCreationOptions.LongRunning)
    use directory = new TempDirectory()
    let port = freePort ()
    writePackage directory.Path port $"""module main

import nanyx.http/http as http

@entry
def main = {{ request: Request ->
  match Request.path(request)
    | "/wait" ->
      match http.get("http://localhost:{slowPort}/")
        | #some(answer) -> Response.text(200, answer.body)
        | _ -> Response.text(502, "Bad Gateway")
    | _ -> Response.text(200, "quick")
}}
"""
    serving directory.Path port (fun client ->
        try
            let waiting = client.GetStringAsync "/wait"
            Assert.True(received.Wait(TimeSpan.FromSeconds 30.0), "The handler of /wait never made its request")
            Assert.Equal((200, "quick"), get client "/quick")
            Assert.False(waiting.IsCompleted)
            release.Set()
            Assert.Equal("late", waiting.GetAwaiter().GetResult())
        finally
            release.Set())
    |> ignore
    slow.Stop()
    answeringSlowly.Wait()