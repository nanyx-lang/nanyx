/// The playground host, `nanyx.playground`, a stand-in for the console host in a web page, which provides `Console`,
/// `Random` and `Clock` and nothing else (#2150, see docs/src/content/compiler-internals/hosts.md).
module Nanyx.Compiler.Tests.Features.Hosts.PlaygroundHost

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The diagnostics of the script `source`, saved as `main.nyx` in a directory of its own.
let private diagnosticsOf (source: string) =
    use directory = new TempPackage([ "main.nyx", source ])
    let path = directory.Path "main.nyx"
    (Script.compileSource path source).Diagnostics

[<Fact>]
let ``A console program that prints, draws random numbers and reads the clock compiles unchanged under the playground host, with the host's linters clean`` () =
    let source =
        withFinalNewline """::host nanyx.playground

import nanyx/random as random

@entry
def main = {
  println("Hello, world!")
  eprintln(random.between(1, 6))
  println(now() + ticks())
}"""
    Assert.Empty(diagnosticsOf source)

[<Fact>]
let ``An entry that requires Env doesn't fit the playground host, which says what it provides`` () =
    let source =
        withFinalNewline """::host nanyx.playground
::dependency nanyx.console.core

import nanyx.console.core/env as (Env)

@entry
def main: [Console, Env] () -> () = { println("hi") }"""
    match diagnosticsOf source |> List.filter Diagnostics.isError with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.EntryDoesNotFitHost.Code, diagnostic.Code)
        Assert.StartsWith(
            """It doesn't fit the host 'nanyx.playground/playground.host'
  expected: [Clock, Console, Random] () -> a
""".ReplaceLineEndings "\n",
            diagnostic.Message)
    | diagnostics -> Assert.Fail $"Expected one error, got %A{diagnostics}"

[<Fact>]
let ``A program that uses FileSystem under the playground host is an error naming the host`` () =
    let source =
        withFinalNewline """::host nanyx.playground
::dependency nanyx.fs

import nanyx.fs/file as file
import nanyx.fs/path as path

ignore(file.read(path.of("notes.txt")))"""
    match diagnosticsOf source with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
        Assert.Equal(
            "'file.read' requires the contexts 'Async' and 'FileSystem', which the host 'nanyx.playground/playground.host' does not provide",
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
    Assert.Empty(underPlayground "println(bits())\nprintln(now() + ticks())\n")
    match underPlayground "println(args())\n" with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.UnknownFunction.Code, diagnostic.Code)
        Assert.Equal("'args' is not in scope", diagnostic.Message)
    | diagnostics -> Assert.Fail $"Expected one diagnostic, got %A{diagnostics}"

[<Fact>]
let ``A script that names its host runs under it, whatever the default`` () =
    Assert.Empty(underPlayground "::host nanyx.console\n\nprintln(args())\n")

[<Fact>]
let ``A script with dependencies and no host runs under the default host`` () =
    let source =
        withFinalNewline """::dependency nanyx.fs

import nanyx.fs/file as file
import nanyx.fs/path as path

ignore(file.read(path.of("notes.txt")))"""
    match underPlayground source with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
        Assert.EndsWith("which the host 'nanyx.playground/playground.host' does not provide", diagnostic.Message)
    | diagnostics -> Assert.Fail $"Expected one diagnostic, got %A{diagnostics}"
