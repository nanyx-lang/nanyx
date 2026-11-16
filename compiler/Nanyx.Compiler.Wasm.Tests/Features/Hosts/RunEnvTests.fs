/// The console host (`nanyx.console/console.host`) provides `Env`: the program's arguments, its environment variables and its exit code (#1246, see
/// docs/src/content/work-in-progress/hosts.md).
module TranspilerWasmTests.Features.Hosts.Env

open System.IO
open Xunit
open TranspilerWasmTestHelpers

/// What `nanyx run` gives for `source`, saved as `main.nyx`, with the program's arguments `arguments` and the
/// environment variables `variables`.
let private runWith (variables: (string * string) list) (arguments: string list) (source: string) =
    use directory = new TempDirectory()
    let path = Path.Combine(directory.Path, "main.nyx")
    File.WriteAllText(path, source)
    nanyxWithVariables variables ([ "run"; path ] @ arguments)

[<Fact>]
let ``An entry point reads the arguments after the program`` () =
    let source =
        """module main

import nanyx/env

@entry
def main = {
  println(args())
}
"""
    let outcome = runWith [] [ "a"; "b c" ] source
    Assert.Equal("", outcome.StandardError)
    Assert.Equal("[a, b c]\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``Arguments after -- reach the program as they are, even ones that start with -`` () =
    let source =
        """module main

import nanyx/env

@entry
def main = {
  println(args())
}
"""
    let outcome = runWith [] [ "--"; "-v"; "--timings"; "--"; "x" ] source
    Assert.Equal("", outcome.StandardError)
    Assert.Equal("[-v, --timings, --, x]\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``A program run with no arguments has none`` () =
    let source =
        """import nanyx/console
import nanyx/env

println(args())
"""
    let outcome = runWith [] [] source
    Assert.Equal("[]\n", outcome.StandardOutput.ReplaceLineEndings "\n")

[<Fact>]
let ``A variable that is set gives its value, and one that is not gives notSet`` () =
    let source =
        """module main

import nanyx/env

def show: [Env & Console] string -> () = { name ->
  match variable(name)
    | #some(value) -> println("{name}={value}")
    | #notSet -> println("{name} is not set")
}

@entry
def main = {
  show("GREETING")
  show("EMPTY")
  show("MISSING")
  show("GREET")
}
"""
    let outcome = runWith [ "GREETING", "hello=world"; "EMPTY", "" ] [] source
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(
        """GREETING=hello=world
EMPTY=
MISSING is not set
GREET is not set
""".ReplaceLineEndings "\n",
        outcome.StandardOutput.ReplaceLineEndings "\n")

[<Fact>]
let ``exit ends the program with its exit code`` () =
    let source =
        """module main

import nanyx/env

@entry
def main = {
  println("before")
  if args() == ["fail"] then exit(3) else ()
  println("after")
}
"""
    let failed = runWith [] [ "fail" ] source
    Assert.Equal("before\n", failed.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal(3, failed.ExitCode)
    let passed = runWith [] [] source
    Assert.Equal("before\nafter\n", passed.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal(0, passed.ExitCode)

[<Fact>]
let ``exit with code 0 ends the program cleanly, with its memory checked`` () =
    let source =
        """import nanyx/console
import nanyx/env

println(args())
exit(0)
println("after")
"""
    let path = Path.GetTempFileName() + ".nyx"
    File.WriteAllText(path, source)
    try
        let outcome = runFile path
        Assert.Equal("", outcome.StandardError)
        Assert.Equal("[]\n", outcome.StandardOutput.ReplaceLineEndings "\n")
        Assert.Equal(0, outcome.ExitCode)
    finally
        File.Delete path

[<Fact>]
let ``A program that does not use Env imports nothing for it`` () =
    let wat = transpileWat (withFinalNewline """@entry
def main = { println("hi") }""")
    Assert.DoesNotContain("args_get", wat)
    Assert.DoesNotContain("environ_get", wat)
