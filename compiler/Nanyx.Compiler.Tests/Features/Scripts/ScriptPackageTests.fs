module Nanyx.Compiler.Tests.Features.Scripts.ScriptPackages

open System.IO
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A script names its host with `::host` and the packages it imports from with `::dependency`, as a manifest's `host`
// and `dependencies` do (#1504, #1599).

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
    (Script.compileSourceReportingIn (storeOf package) CompileListener.none path source).Diagnostics

let private codesOf (diagnostics: Diagnostic list) = diagnostics |> List.map _.Code

[<Fact>]
let ``A script imports a public module of a package its ::dependency finds by path`` () =
    let source = withFinalNewline """::dependency acme.util ../util

import acme.util/strings

println(shout("hi"))"""
    Assert.Empty(compileScriptWith util source)

[<Fact>]
let ``Without ::dependency a script can't import the package`` () =
    let source = withFinalNewline """import acme.util/strings

println(shout("hi"))"""
    Assert.Equal<string list>([ DiagnosticCodes.ImportNotFound.Code ], codesOf (compileScriptWith util source))

[<Fact>]
let ``A script imports only a dependency's public modules`` () =
    let files = util @ [ "util/secrets.nyx", withFinalNewline """module secrets

export def mark = "!" """ ]
    let source = withFinalNewline """::dependency acme.util ../util

import acme.util/secrets

println(mark)"""
    Assert.Equal<string list>([ DiagnosticCodes.ModuleNotPublic.Code ], codesOf (compileScriptWith files source))

[<Fact>]
let ``A script depends on a package the compiler ships by its name`` () =
    let source = withFinalNewline $"""::dependency nanyx.console.core

import nanyx.console.core/env as (Env)

def count: [Env] () -> int = {{ () -> args() \length }}

println(count())"""
    Assert.Empty(compileScriptWith [] source)

[<Fact>]
let ``A script still imports files beside it by path when it has dependencies`` () =
    let files = util @ [ "scripts/helper.nyx", withFinalNewline """module helper

export def twice = { s: string -> s ++ s }""" ]
    let source = withFinalNewline """::dependency acme.util ../util

import acme.util/strings
import helper

println(shout(twice("hi")))"""
    Assert.Empty(compileScriptWith files source)

[<Theory>]
[<InlineData("::host nanyx.console")>]
[<InlineData("::host nanyx.console {version}")>]
[<InlineData("::host nanyx.console {shipped}")>]
let ``A script runs under the host its ::host names, by name, version or path`` (directive: string) =
    let directive =
        directive
            .Replace("{version}", Toolchain.version)
            .Replace("{shipped}", (ShippedPackages.directoryOf Hosts.consolePackage).Replace("\\", "/"))
    let source = directive + withFinalNewline """

println("hi")"""
    Assert.Empty(compileScriptWith [] source)

[<Theory>]
[<InlineData("::host nanyx.console 0.0.1", "NYX0029", "The compiler ships 'nanyx.console' {version}, not 0.0.1")>]
[<InlineData("::host nanyx.nope {version}", "NYX0023", "The compiler ships no package 'nanyx.nope': give the path of a host the compiler doesn't ship")>]
[<InlineData("::host nanyx.nope", "NYX0009", "The compiler ships no package 'nanyx.nope': give the directory it is in, as in `../nope`")>]
[<InlineData("::host acme.util ../util", "NYX0023", "'acme.util' is not a host")>]
[<InlineData("::host acme.other ../util", "NYX0023", "The package in")>]
[<InlineData("::host nanyx.console ../nowhere", "NYX0023", "The host 'nanyx.console' can't be read")>]
[<InlineData("::host", "NYX0009", "`::host` takes a host and where it is found")>]
[<InlineData("::host nanyx.console ../console extra", "NYX0009", "`::host` takes a host and where it is found")>]
[<InlineData("::host nanyx.console latest", "NYX0009", "'latest' is neither a version, which starts with a digit, as `1.2.0`, nor a directory")>]
[<InlineData("::dependency acme.util 1.2.0", "NYX0041", "'acme.util' 1.2.0 isn't in the package cache")>]
[<InlineData("::dependency acme.util 1.2", "NYX0009", "A script names the exact version of 'acme.util' it uses, as in `1.2.0`, not the versions it accepts, '1.2'")>]
[<InlineData("::dependency acme.util ../nowhere", "NYX0023", "The dependency 'acme.util' can't be read")>]
[<InlineData("::dependency acme.other ../util", "NYX0023", "is named 'acme.util', not 'acme.other'")>]
[<InlineData("::dependency acme.util", "NYX0009", "`::dependency acme.util` needs the version of 'acme.util' to use, as in `::dependency acme.util 1.2.0`, or the directory it is in, as in `../util`")>]
[<InlineData("::dependency nanyx.console.core 0.0.1", "NYX0029", "The compiler ships 'nanyx.console.core' {version}, not 0.0.1")>]
[<InlineData("::dependency", "NYX0009", "`::dependency` takes a package and where it is found")>]
let ``A wrong ::host or ::dependency is the manifest's error, at the directive`` (directive: string, code: string, message: string) =
    let directive = directive.Replace("{version}", Toolchain.version)
    let source = "-- A script.\n" + directive + withFinalNewline """

println("hi")"""
    let diagnostic = (compileScriptWith util source).Head
    Assert.Equal(code, diagnostic.Code)
    Assert.Contains(message.Replace("{version}", Toolchain.version), diagnostic.Message)
    Assert.Equal(1, diagnostic.Span.Value.StartLine)

[<Fact>]
let ``A script runs under one host`` () =
    let source = withFinalNewline $"""::host nanyx.console
::host nanyx.console

println("hi")"""
    let diagnostic = Assert.Single(compileScriptWith [] source)
    Assert.Equal(DiagnosticCodes.InvalidDirective.Code, diagnostic.Code)
    Assert.Equal("A script runs under one host, which an earlier `::host` names already: remove this one", diagnostic.Message)
    Assert.Equal(1, diagnostic.Span.Value.StartLine)

[<Fact>]
let ``A script lists each package it depends on once`` () =
    let source = withFinalNewline """::dependency acme.util ../util
::dependency acme.util ../util

println("hi")"""
    let diagnostic = Assert.Single(compileScriptWith util source)
    Assert.Equal(DiagnosticCodes.InvalidManifestValue.Code, diagnostic.Code)
    Assert.Contains("The dependency 'acme.util' is listed twice", diagnostic.Message)
    Assert.Equal(1, diagnostic.Span.Value.StartLine)

[<Fact>]
let ``A dependency's file is a library's, which doesn't see the host's prelude`` () =
    let files =
        [ "util/util.nyxp", withFinalNewline """(package = (name = "acme.util", version = "0.1.0"))"""
          "util/strings.nyx", withFinalNewline """export module strings

export def greet: [Console] () -> () = { () -> println("hi") }""" ]
    let source = withFinalNewline """::dependency acme.util ../util

import acme.util/strings

greet()"""
    // `Console` is the host's prelude's, which a library doesn't see without importing it.
    Assert.Equal<string list>([ DiagnosticCodes.UnknownContext.Code ], codesOf (compileScriptWith files source) |> List.distinct)

[<Fact>]
let ``The editor compiles a script in the build its directives make`` () =
    use package = new TempPackage(util)
    let path = package.Path "scripts/main.nyx"
    let source = withFinalNewline """::dependency acme.util ../util

import acme.util/strings

println(shout("hi"))"""
    let directives = Script.read path source
    Assert.True(directives.Build.IsSome)
    let result = Script.compileSourceReading CompileListener.none File.ReadAllText directives Linters.none path source
    Assert.Empty(result.Diagnostics)
