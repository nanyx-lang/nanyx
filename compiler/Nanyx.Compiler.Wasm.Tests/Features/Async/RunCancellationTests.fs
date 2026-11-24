/// Cancellation and `race` (#1890, see "Cancellation without exceptions" in
/// docs/src/content/work-in-progress/concurrency-and-crash-boundaries.md): `race` gives what the first of its
/// contestants to finish gave, and cancels the others, and every task they started; in a cancelled task, a request or a
/// file's function gives `#cancelled`, and a sleep returns at once. Each program runs with memory checks, straight
/// through and with every wait stopping once, inlined and not.
module TranspilerWasmTests.Features.Async.RunCancellation

open System
open System.IO
open System.Threading
open Xunit
open TranspilerWasmTestHelpers

/// What `nanyx run --check-memory`, with `options`, gives for the script `source`.
let private run (options: string list) (source: string) =
    use directory = new TempDirectory()
    File.WriteAllText(Path.Combine(directory.Path, "main.nyx"), withFinalNewline source)
    let outcome = nanyxIn directory.Path ([ "run"; "--check-memory" ] @ options @ [ "main.nyx" ])
    { outcome with
        StandardOutput = outcome.StandardOutput.ReplaceLineEndings "\n"
        StandardError = outcome.StandardError.ReplaceLineEndings "\n" }

/// Asserts that `source` prints `expected` in every mode. What it writes to stderr is shown whole, with the mode, since
/// an exception from the runtime is written there with its stack (see `WasmRunner.trapped`), and an exception thrown out
/// of a run says which mode it was thrown in, with the exception whole.
let private assertPrints (expected: string) (source: string) =
    for options in [ []; [ "--yield-at-every-wait" ]; [ "--yield-at-every-wait"; "--no-inline" ] ] do
        let outcome =
            try run options source
            with ex -> raise (Exception($"With %A{options}, the run threw:\n{ex}", ex))
        Assert.True(outcome.StandardError = "", $"With %A{options}, it wrote to stderr:\n{outcome.StandardError}")
        Assert.Equal(normalized expected, outcome.StandardOutput)
        Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``race gives what the first to finish gave, and a loser's sleep returns at once`` () =
    assertPrints
        "quick\ntrue\n"
        """def fastest = { _ -> race([{ sleep(50); "quick" }, { sleep(60_000); "slow" }]) }

def started = ticks()
println(fastest())
println(ticks() - started < 30_000)"""

/// A server that holds its answer to `/slow` until the test is done, so a request to it ends only by being
/// cancelled, and answers anything else at once.
let private withSlowServer (test: string -> unit) =
    use release = new ManualResetEventSlim()
    use server =
        new TestServer(fun _ path _ _ ->
            if path = "/slow" then release.Wait(TimeSpan.FromSeconds 60.0) |> ignore
            200, [], "answered")
    try test server.Url finally release.Set()

[<Fact>]
let ``A request in a task race cancels gives #cancelled, so a timeout is a race against a sleep`` () =
    withSlowServer (fun url ->
        assertPrints
            "the request was cancelled\n#timedOut\n"
            $"""::dependency nanyx.http.core

import nanyx.http.core/http as http

def fetch: [Async, Console, Http] string -> #some(string) | #cancelled | #failed = {{ url ->
  match http.get(url)
    | #some(response) -> #some(response.body)
    | #cancelled ->
      println("the request was cancelled")
      #cancelled
    | _ -> #failed
}}

def limited: [Async, Clock, Console, Http] () -> #some(string) | #cancelled | #failed | #timedOut = {{ _ ->
  race([{{ fetch("{url}/slow") }}, {{ sleep(50); #timedOut }}])
}}

println(limited())""")

[<Fact>]
let ``Cancelling a task cancels every task it started`` () =
    withSlowServer (fun url ->
        assertPrints
            "the child's request was cancelled\nquick\n"
            $"""::dependency nanyx.http.core

import nanyx.http.core/http as http

def child: [Async, Console, Http] string -> string = {{ url ->
  match http.get(url)
    | #cancelled ->
      println("the child's request was cancelled")
      "cancelled"
    | _ -> "answered"
}}

def parent = {{ url: string ->
  def task = start! child(url)
  await(task)
}}

def raced = {{ _ -> race([{{ parent("{url}/slow") }}, {{ sleep(50); "quick" }}]) }}

println(raced())""")

[<Fact>]
let ``A file's function in a cancelled task gives #cancelled and does nothing`` () =
    assertPrints
        "the loser's write was cancelled\nfalse\nfirst\n"
        """::dependency nanyx.fs

import (
  nanyx.fs/file as file
  nanyx.fs/path as path
)

def loser: [Async, Console, FileSystem, Clock] () -> string = { _ ->
  sleep(60_000)

  match file.write(path.of("never.txt"), "written")
    | #cancelled -> println("the loser's write was cancelled")
    | _ -> println("written")

  println(file.exists(path.of("never.txt")))
  "loser"
}

def raced = { _ -> race([loser, { "first" }]) }

println(raced())"""

[<Fact>]
let ``race with no contestant crashes`` () =
    let outcome = run [] "def none: [Async] () -> string = { _ -> race([]) }\n\nprintln(none())"
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("race needs a contestant", outcome.StandardError)
