module Nanyx.Compiler.Tests.Features.Hosts.UnprovidedContexts

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A library function that requires a context the program's host doesn't provide is an error at its call (#1811).

/// A host, `acme.quiet.host`, in `quiet/`, that provides no contexts.
let private quietHost =
    [ "quiet/acme.quiet.host.nyxp", withFinalNewline """(package = (name = "acme.quiet.host", version = "0.1.0", host = (root = "quiet.host")))"""
      "quiet/quiet.host.nyx",
      withFinalNewline """export module quiet.host

import nanyx/host.runtime

export def run: [HostRuntime] (() -> a) -> () = { entry -> ignore(entry()) }""" ]

/// A library, `acme.lib`, in `lib/`, that depends on `dependency` and whose module `lib` is `body`.
let private library (dependency: string) (body: string) =
    [ "lib/acme.lib.nyxp",
      withFinalNewline $"""(package = (name = "acme.lib", version = "0.1.0"), dependencies = [(package = "{dependency}", version = "{Toolchain.version}")])"""
      "lib/lib.nyx", withFinalNewline body ]

let private consoleLibrary =
    library "nanyx.cli" """export module lib

import nanyx.cli/console

export def greet: [Console] () -> () = { -> println("hi") }"""

let private fileSystemLibrary =
    library "nanyx.fs" """export module lib

import nanyx/async
import nanyx.fs/file as file
import nanyx.fs/filesystem
import nanyx.fs/path as path

export def greet: [Async, FileSystem] () -> () = { -> ignore(file.append(path.of("out.txt"), "hi")) }"""

/// The diagnostics of the script `source`, saved as `main.nyx` beside the quiet host and `files`.
let private compileScriptWith (files: (string * string) list) (source: string) =
    use package = new TempPackage(("main.nyx", source) :: quietHost @ files)
    let path = package.Path "main.nyx"
    (Script.compileSource path source).Diagnostics

/// Each caller of `greet`, with the line of its call, counted from 0.
let private callers =
    [ "def f: () -> () = { greet() }\n\n@entry\ndef main = { f() }", 5
      "@entry\ndef main = { greet() }", 6
      "greet()", 5 ]

[<Fact>]
let ``A library's function requiring a context the host doesn't provide is an error at its call`` () =
    for caller, line in callers do
        let source = withFinalNewline $"""::host acme.quiet.host ./quiet
::dependency acme.lib ./lib

import acme.lib/lib

{caller}"""
        match compileScriptWith consoleLibrary source with
        | [ diagnostic ] ->
            Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
            Assert.Equal("'greet' requires the context 'Console', which the host 'acme.quiet.host/quiet.host' does not provide", diagnostic.Message)
            Assert.Equal(line, diagnostic.Span.Value.StartLine)
        | diagnostics -> Assert.Fail $"Expected one diagnostic for `{caller}`, got %A{diagnostics}"

[<Fact>]
let ``A library's function requiring FileSystem is an error at its call under a host that doesn't provide it`` () =
    let source = withFinalNewline """::host acme.quiet.host ./quiet
::dependency acme.lib ./lib

import acme.lib/lib

greet()"""
    match compileScriptWith fileSystemLibrary source with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
        Assert.Equal("'greet' requires the contexts 'Async' and 'FileSystem', which the host 'acme.quiet.host/quiet.host' does not provide", diagnostic.Message)
        Assert.Equal(5, diagnostic.Span.Value.StartLine)
    | diagnostics -> Assert.Fail $"Expected one diagnostic, got %A{diagnostics}"
