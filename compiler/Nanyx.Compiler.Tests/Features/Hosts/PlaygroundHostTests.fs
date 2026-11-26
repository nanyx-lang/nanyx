/// The playground host, `nanyx.playground.host`, a stand-in for the console host in a web page, which provides `Console`,
/// `Random` and `Clock` and nothing else (#2150, see docs/src/content/deep-dives/hosts.md).
module Nanyx.Compiler.Tests.Features.Hosts.PlaygroundHost

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The diagnostics of the script `source`, saved as `main.nyx` in a directory of its own.
let private diagnosticsOf (source: string) =
    use directory = new TempPackage([ "main.nyx", source ])
    let path = directory.Path "main.nyx"
    (Script.compileSourceIn (SeededPackages.user ()) path source).Diagnostics

[<Fact>]
let ``A console program that prints, draws random numbers and reads the clock compiles unchanged under the playground host, with the host's linters clean`` () =
    let source =
        withFinalNewline """::host nanyx.playground.host

import nanyx/random as random

@entry
def main = {
  println("Hello, world!")
  eprintln(random.between(1, 6))
  println("{now()} {ticks()}")
}"""
    Assert.Empty(diagnosticsOf source)

[<Fact>]
let ``An entry that requires Env doesn't fit the playground host, which says what it provides`` () =
    let source =
        withFinalNewline """::host nanyx.playground.host
::install nanyx.cli

import nanyx.cli/env as (Env)

@entry
def main: [Console, Env] () -> () = { println("hi") }"""
    match diagnosticsOf source |> List.filter Diagnostics.isError with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.EntryDoesNotFitHost.Code, diagnostic.Code)
        Assert.StartsWith(
            """It doesn't fit the host 'nanyx.playground.host/playground.host'
  expected: [Async, Clock, Console, Random] () -> a
""".ReplaceLineEndings "\n",
            diagnostic.Message)
    | diagnostics -> Assert.Fail $"Expected one error, got %A{diagnostics}"

[<Fact>]
let ``A program that uses FileSystem under the playground host is an error naming the host`` () =
    let source =
        withFinalNewline """::host nanyx.playground.host
::install nanyx.fs

import nanyx.fs/file as file
import nanyx.fs/path as path

ignore(file.read(path.of("notes.txt")))"""
    match diagnosticsOf source with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
        Assert.Equal(
            "'file.read' requires the context 'FileSystem', which the host 'nanyx.playground.host/playground.host' does not provide",
            diagnostic.Message)
        Assert.Equal(6, diagnostic.Span.Value.StartLine)
    | diagnostics -> Assert.Fail $"Expected one diagnostic, got %A{diagnostics}"

// Printing the result, whose type names `FsError`, which the playground host's prelude doesn't export, also reported
// NYX0109 "Unknown type 'FsError'", with no span, as if the `ToString` of the union were looked up by a name the program
// can't see (#2165).
[<Fact>]
let ``Printing a result whose error type the host's prelude doesn't export reports only the missing context`` () =
    let source =
        withFinalNewline """::host nanyx.playground.host
::install nanyx.fs

import nanyx.fs/file as file
import nanyx.fs/path as path

println(file.read(path.of("notes.txt")))"""
    match diagnosticsOf source with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
        Assert.Equal(
            "'file.read' requires the context 'FileSystem', which the host 'nanyx.playground.host/playground.host' does not provide",
            diagnostic.Message)
        Assert.Equal(6, diagnostic.Span.Value.StartLine)
    | diagnostics -> Assert.Fail $"Expected one diagnostic, got %A{diagnostics}"

/// The diagnostics of `source` compiled as the compiler built for the browser compiles a page's program: a script that
/// runs under the playground host unless its `::host` names another (`Script.compileUnder`).
let private underPlayground (source: string) =
    use directory = new TempPackage([ "main.nyx", source ])
    (Script.compileUnder Hosts.playgroundPackage (storeOf directory) (directory.Path "main.nyx") source).Diagnostics

[<Fact>]
let ``A script with no directives runs under the playground host where it is the default`` () =
    Assert.Empty(underPlayground "println(bits())\nprintln(\"{now()} {ticks()}\")\n")
    match underPlayground "println(args())\n" with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.UnknownFunction.Code, diagnostic.Code)
        Assert.Equal("'args' is not in scope", diagnostic.Message)
    | diagnostics -> Assert.Fail $"Expected one diagnostic, got %A{diagnostics}"

[<Fact>]
let ``A script that names its host runs under it, whatever the default`` () =
    Assert.Empty(underPlayground "::host nanyx.cli.host\n\nprintln(args())\n")

[<Fact>]
let ``A script with dependencies and no host runs under the default host`` () =
    let source =
        withFinalNewline """::install nanyx.fs

import nanyx.fs/file as file
import nanyx.fs/path as path

ignore(file.read(path.of("notes.txt")))"""
    match underPlayground source with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
        Assert.EndsWith("which the host 'nanyx.playground.host/playground.host' does not provide", diagnostic.Message)
    | diagnostics -> Assert.Fail $"Expected one diagnostic, got %A{diagnostics}"

[<Fact>]
let ``A program that starts tasks and sleeps compiles under the playground host, which provides Async`` () =
    let source =
        withFinalNewline """::host nanyx.playground.host

def later: [Async, Clock, Console] string -> () = { text ->
  sleep(100ms)
  println(text)
}

def both = { _ ->
  def first = start! later("the sleeper wakes")
  def second = start! println("another task runs meanwhile")
  await(first)
  await(second)
}

both()"""
    Assert.Empty(diagnosticsOf source)

[<Fact>]
let ``A page's program uses the packages the compiler ships with nothing packed or unpacked, as the browser compiles it`` () =
    use directory = new TempPackage([])
    let store = SeededPackages.inMemory (directory.Path "cache") (directory.Path "index") GitHub.unsupported
    let source = withFinalNewline "::install nanyx.cli\n\nimport nanyx.cli/console as console\n\n@entry\ndef main = {\n  console.println(\"hi\")\n}"
    Assert.Empty((Script.compileUnder Hosts.playgroundPackage store (directory.Path "main.nyx") source).Diagnostics)
    // A page locks and downloads nothing, so the cache isn't even made.
    Assert.False(System.IO.Directory.Exists(directory.Path "cache"))
