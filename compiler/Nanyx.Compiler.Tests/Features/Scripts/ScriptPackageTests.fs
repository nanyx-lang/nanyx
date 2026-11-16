module Nanyx.Compiler.Tests.Features.Scripts.ScriptPackages

open System.IO
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A script names its host with `::host` and the packages it imports from with `::dependencies`, as a manifest's `host`
// and `dependencies` do (#1504).

/// A library, `acme.util`, in `util/`, beside the script in `scripts/`.
let private util =
    [ "util/util.nyxp", withFinalNewline """(package = (name = "acme.util", version = "0.1.0"))"""
      "util/strings.nyx",
      withFinalNewline """export module strings

export def shout = { s: string -> s ++ "!" }""" ]

/// The diagnostics of the script `source`, saved as `scripts/main.nyx` beside `files`.
let private compileScriptWith (files: (string * string) list) (source: string) =
    use package = new TempPackage(("scripts/main.nyx", source) :: files)
    let path = package.Path "scripts/main.nyx"
    (Script.compileSource path source).Diagnostics

let private codesOf (diagnostics: Diagnostic list) = diagnostics |> List.map _.Code

[<Fact>]
let ``A script imports a public module of a package its ::dependencies finds by path`` () =
    let source = withFinalNewline """::dependencies.acme.util = { path = "../util" }

import acme.util/strings

println(shout("hi"))"""
    Assert.Empty(compileScriptWith util source)

[<Fact>]
let ``Without ::dependencies a script can't import the package`` () =
    let source = withFinalNewline """import acme.util/strings

println(shout("hi"))"""
    Assert.Equal<string list>([ DiagnosticCodes.ImportNotFound.Code ], codesOf (compileScriptWith util source))

[<Fact>]
let ``A script imports only a dependency's public modules`` () =
    let files = util @ [ "util/secrets.nyx", withFinalNewline """module secrets

export def mark = "!" """ ]
    let source = withFinalNewline """::dependencies.acme.util = { path = "../util" }

import acme.util/secrets

println(mark)"""
    Assert.Equal<string list>([ DiagnosticCodes.ModuleNotPublic.Code ], codesOf (compileScriptWith files source))

[<Fact>]
let ``A script depends on a package the compiler ships by its version`` () =
    let source = withFinalNewline $"""::dependencies.nanyx.console.core = "{Toolchain.version}"

import nanyx.console.core/env as (Env)

def count: [Env] () -> int = {{ () -> args() \length }}

println(count())"""
    Assert.Empty(compileScriptWith [] source)

[<Fact>]
let ``A script still imports files beside it by path when it has dependencies`` () =
    let files = util @ [ "scripts/helper.nyx", withFinalNewline """module helper

export def twice = { s: string -> s ++ s }""" ]
    let source = withFinalNewline """::dependencies.acme.util = { path = "../util" }

import acme.util/strings
import helper

println(shout(twice("hi")))"""
    Assert.Empty(compileScriptWith files source)

[<Theory>]
[<InlineData("::host.nanyx.console = \"{version}\"")>]
[<InlineData("::host.nanyx.console = { path = \"{shipped}\" }")>]
let ``A script runs under the host its ::host names, by version or by path`` (directive: string) =
    let directive =
        directive
            .Replace("{version}", Toolchain.version)
            .Replace("{shipped}", (ShippedPackages.directoryOf Hosts.consolePackage).Replace("\\", "/"))
    let source = directive + withFinalNewline """

println("hi")"""
    Assert.Empty(compileScriptWith [] source)

[<Theory>]
[<InlineData("::host.nanyx.console = \"0.0.1\"", "NYX0029", "the compiler ships nanyx.console {version}, not 0.0.1")>]
[<InlineData("::host.nanyx.nope = \"{version}\"", "NYX0023", "the compiler ships no package 'nanyx.nope': a host the compiler doesn't ship is found by its path")>]
[<InlineData("::host.acme.util = { path = \"../util\" }", "NYX0023", "'acme.util' is not a host")>]
[<InlineData("::host.acme.other = { path = \"../util\" }", "NYX0023", "the package in")>]
[<InlineData("::host.nanyx.console = { path = \"../nowhere\" }", "NYX0023", "the host nanyx.console can't be read")>]
[<InlineData("::host = \"{version}\"", "NYX0009", "::host takes a host and its version")>]
[<InlineData("::host.nanyx.console", "NYX0009", "::host takes a host and its version")>]
[<InlineData("::dependencies.acme.util = \"1.2.0\"", "NYX0024", "packages can't be downloaded yet")>]
[<InlineData("::dependencies.acme.util = { path = \"../nowhere\" }", "NYX0023", "the dependency acme.util can't be read")>]
[<InlineData("::dependencies.acme.other = { path = \"../util\" }", "NYX0023", "is named acme.util, not acme.other")>]
[<InlineData("::dependencies.acme.util = { path = \"../util\", version = \"1.0.0\" }", "NYX0023", "found by its path, so it has no version")>]
[<InlineData("::dependencies.nanyx.console.core = \"0.0.1\"", "NYX0029", "the compiler ships nanyx.console.core {version}, not 0.0.1")>]
[<InlineData("::dependencies.acme.util", "NYX0009", "::dependencies takes a package and where it is")>]
let ``A wrong ::host or ::dependencies is the manifest's error, at the directive`` (directive: string, code: string, message: string) =
    let directive = directive.Replace("{version}", Toolchain.version)
    let source = "-- A script.\n" + directive + withFinalNewline """

println("hi")"""
    let diagnostic = (compileScriptWith util source).Head
    Assert.Equal(code, diagnostic.Code)
    Assert.Contains(message.Replace("{version}", Toolchain.version), diagnostic.Message)
    Assert.Equal(1, diagnostic.Span.Value.StartLine)

[<Fact>]
let ``A script runs under one host`` () =
    let source = withFinalNewline $"""::host.nanyx.console = "{Toolchain.version}"
::host.nanyx.console = "{Toolchain.version}"

println("hi")"""
    let diagnostic = Assert.Single(compileScriptWith [] source)
    Assert.Equal(DiagnosticCodes.InvalidDirective.Code, diagnostic.Code)
    Assert.Equal("a script runs under one host, which one ::host names", diagnostic.Message)
    Assert.Equal(1, diagnostic.Span.Value.StartLine)

[<Fact>]
let ``A script lists each package it depends on once`` () =
    let source = withFinalNewline """::dependencies.acme.util = { path = "../util" }
::dependencies.acme.util = { path = "../util" }

println("hi")"""
    let diagnostic = Assert.Single(compileScriptWith util source)
    Assert.Equal(DiagnosticCodes.InvalidManifestValue.Code, diagnostic.Code)
    Assert.Contains("the dependency acme.util is listed twice", diagnostic.Message)
    Assert.Equal(1, diagnostic.Span.Value.StartLine)

[<Fact>]
let ``A dependency's file is a library's, which doesn't see the host's prelude`` () =
    let files =
        [ "util/util.nyxp", withFinalNewline """(package = (name = "acme.util", version = "0.1.0"))"""
          "util/strings.nyx", withFinalNewline """export module strings

export def greet: [Console] () -> () = { () -> println("hi") }""" ]
    let source = withFinalNewline """::dependencies.acme.util = { path = "../util" }

import acme.util/strings

greet()"""
    // `Console` is the host's prelude's, which a library doesn't see without importing it.
    Assert.Equal<string list>([ DiagnosticCodes.UnknownContext.Code ], codesOf (compileScriptWith files source) |> List.distinct)

[<Fact>]
let ``The editor compiles a script in the build its directives make`` () =
    use package = new TempPackage(util)
    let path = package.Path "scripts/main.nyx"
    let source = withFinalNewline """::dependencies.acme.util = { path = "../util" }

import acme.util/strings

println(shout("hi"))"""
    let directives = Script.read path source
    Assert.True(directives.Build.IsSome)
    let result = Script.compileSourceReading CompileListener.none File.ReadAllText directives Linters.none path source
    Assert.Empty(result.Diagnostics)
