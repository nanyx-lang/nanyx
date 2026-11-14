module Nanyx.Compiler.Tests.Features.Packages.PathDependencies

open System.IO
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A package that depends on another in a directory on disk, `"acme.util" = { path = "../util" }`, and imports its
// public modules as `acme.util/strings`.

/// A manifest of the package `name`, with `extra` after its `[package]` table's name and version.
let private manifest (name: string) (extra: string) = $"[package]\nname = \"{name}\"\nversion = \"0.1.0\"\n{extra}"

let private util =
    [ "util/util.nyxp", manifest "acme.util" "public-modules = [\"strings\"]\n"
      "util/strings.nyx",
      withFinalNewline """module strings

import secrets

export def shout = { s: string -> s ++ mark }"""
      "util/secrets.nyx", withFinalNewline """module secrets

export def mark = "!" """ ]

/// The app's manifest, depending on `util` in the directory beside it.
let private app (dependencies: string) = "app/app.nyxp", manifest "app" $"\n[dependencies]\n{dependencies}"

let private mainImporting (import: string) (body: string) =
    "app/main.nyx",
    withFinalNewline $"""module main

import {import}

@entry
def run = {{ () -> {body} }}"""

let private errorsOf result =
    match result with
    | Result.Error errors -> errors
    | Result.Ok _ -> failwith "Expected errors"

let private okOf result =
    match result with
    | Result.Ok value -> value
    | Result.Error errors -> failwith (String.concat "\n" errors)

/// The diagnostics of building the package in `app`.
let private buildDiagnostics (package: TempPackage) =
    (Package.build (okOf (Package.find (package.Path "app")))).Diagnostics

[<Fact>]
let ``A public module of a dependency is imported by the package's name and the module's`` () =
    use package =
        new TempPackage(util @ [ app "\"acme.util\" = { path = \"../util\" }"; mainImporting "acme.util/strings" "ignore(shout(\"hi\"))" ])
    let found = okOf (Package.find (package.Path "app"))
    Assert.Equal<string list>([ "acme.util" ], found.Dependencies |> List.map _.Manifest.Name)
    Assert.Equal<string list>([ "strings" ], found.Dependencies.Head.Manifest.PublicModules)
    let build = Package.build found
    Assert.Empty(build.Diagnostics)
    Assert.True((Package.entryResult build).Value.Typed.IsSome)

[<Fact>]
let ``A dotted name written without quotes is read as the name it spells`` () =
    use package = new TempPackage(util @ [ app "acme.util = { path = \"../util\" }"; mainImporting "acme.util/strings" "ignore(shout(\"hi\"))" ])
    let read = okOf (Manifest.read (package.Path "app/app.nyxp"))
    Assert.Equal<string list>([ "acme.util" ], read.Dependencies |> List.map _.Name)
    Assert.Equal(Path.GetFullPath(package.Path "util"), read.Dependencies.Head.Directory)

[<Fact>]
let ``A version without a path is refused, since packages can't be downloaded yet`` () =
    use package = new TempPackage([ app "\"acme.util\" = \"1.2\"" ])
    let error = Assert.Single(errorsOf (Manifest.read (package.Path "app/app.nyxp")))
    Assert.EndsWith(
        """[dependencies] acme.util = "1.2" names a version, but packages can't be downloaded yet: give the directory the package is in, "acme.util" = { path = "../util" }""",
        error)

[<Fact>]
let ``A dependency must be a directory with a manifest`` () =
    use package = new TempPackage([ app "\"acme.util\" = { path = \"../missing\" }"; mainImporting "nanyx/console" "println(1)" ])
    let error = Assert.Single(errorsOf (Package.find (package.Path "app")))
    Assert.EndsWith($"""[dependencies] acme.util: {Path.GetFullPath(package.Path "missing")} is not a directory""", error)

[<Fact>]
let ``A dependency's package must have the name the dependant gives it`` () =
    use package = new TempPackage(util @ [ app "\"acme.tools\" = { path = \"../util\" }"; mainImporting "nanyx/console" "println(1)" ])
    let error = Assert.Single(errorsOf (Package.find (package.Path "app")))
    Assert.EndsWith(
        $"""[dependencies] acme.tools: the package in {Path.GetFullPath(package.Path "util")} is named acme.util, not acme.tools: the names must match""",
        error)

[<Fact>]
let ``A module a dependency does not make public can't be imported`` () =
    use package =
        new TempPackage(util @ [ app "\"acme.util\" = { path = \"../util\" }"; mainImporting "acme.util/secrets" "ignore(mark)" ])
    let error = Assert.Single(buildDiagnostics package)
    Assert.Equal(DiagnosticCodes.ModuleNotPublic.Code, error.Code)
    Assert.Equal(
        "The module 'secrets' of the package 'acme.util' is not public, so another package can't import it: its public modules are 'strings'",
        error.Message)

[<Fact>]
let ``A package a dependency depends on can't be imported without depending on it`` () =
    let middle =
        [ "middle/middle.nyxp", manifest "acme.middle" "public-modules = [\"loud\"]\n[dependencies]\n\"acme.util\" = { path = \"../util\" }\n"
          "middle/loud.nyx", withFinalNewline """module loud

import acme.util/strings

export def louder = { s: string -> shout(shout(s)) }""" ]
    use package =
        new TempPackage(util @ middle @ [ app "\"acme.middle\" = { path = \"../middle\" }"; mainImporting "acme.util/strings" "ignore(shout(\"hi\"))" ])
    let error = Assert.Single(buildDiagnostics package)
    Assert.Equal(DiagnosticCodes.IndirectDependency.Code, error.Code)
    Assert.StartsWith("The package 'acme.util' is not a dependency of 'app', only of a package it depends on", error.Message)

[<Fact>]
let ``A dependency's own dependencies are imported in its files`` () =
    let middle =
        [ "middle/middle.nyxp", manifest "acme.middle" "public-modules = [\"loud\"]\n[dependencies]\n\"acme.util\" = { path = \"../util\" }\n"
          "middle/loud.nyx", withFinalNewline """module loud

import acme.util/strings

export def louder = { s: string -> shout(shout(s)) }""" ]
    use package =
        new TempPackage(util @ middle @ [ app "\"acme.middle\" = { path = \"../middle\" }"; mainImporting "acme.middle/loud" "ignore(louder(\"hi\"))" ])
    Assert.Empty(buildDiagnostics package)

[<Fact>]
let ``A package that is no dependency is not found`` () =
    use package = new TempPackage([ app ""; mainImporting "acme.util/strings" "ignore(1)" ])
    let error = Assert.Single(buildDiagnostics package)
    Assert.Equal(DiagnosticCodes.ImportNotFound.Code, error.Code)
    Assert.EndsWith("there is no package 'acme.util': 'app' can import the standard library, its own modules, and the public modules of the packages it depends on, and it has no [dependencies]", error.Message)

[<Fact>]
let ``A build has one version of each package`` () =
    let copy = util |> List.map (fun (path, text) -> path.Replace("util/", "copy/"), text)
    let middle =
        [ "middle/middle.nyxp", manifest "acme.middle" "public-modules = [\"loud\"]\n[dependencies]\n\"acme.util\" = { path = \"../copy\" }\n"
          "middle/loud.nyx", withFinalNewline """module loud

export def louder = { s: string -> s }""" ]
    use package =
        new TempPackage(
            util @ copy @ middle
            @ [ app "\"acme.util\" = { path = \"../util\" }\n\"acme.middle\" = { path = \"../middle\" }"
                mainImporting "nanyx/console" "println(1)" ])
    let error = Assert.Single(errorsOf (Package.find (package.Path "app")))
    Assert.Contains(
        $"""the build reaches the package acme.util by two paths, {Path.GetFullPath(package.Path "util")} and {Path.GetFullPath(package.Path "copy")}""",
        error)

[<Fact>]
let ``The same package reached through two dependants is one package`` () =
    let middle =
        [ "middle/middle.nyxp", manifest "acme.middle" "public-modules = [\"loud\"]\n[dependencies]\n\"acme.util\" = { path = \"../util\" }\n"
          "middle/loud.nyx", withFinalNewline """module loud

import acme.util/strings

export def louder = { s: string -> shout(s) }""" ]
    use package =
        new TempPackage(
            util @ middle
            @ [ app "\"acme.util\" = { path = \"../util\" }\n\"acme.middle\" = { path = \"../middle\" }"
                "app/main.nyx",
                withFinalNewline """module main

import acme.util/strings
import acme.middle/loud

@entry
def run = { () -> ignore(louder(shout("hi"))) }""" ])
    Assert.Empty(buildDiagnostics package)

[<Fact>]
let ``Packages can't depend on each other in a cycle`` () =
    use package =
        new TempPackage(
            [ "a/a.nyxp", manifest "acme.a" "[dependencies]\n\"acme.b\" = { path = \"../b\" }\n"
              "a/main.nyx", withFinalNewline "module main\n\nexport def x = 1"
              "b/b.nyxp", manifest "acme.b" "[dependencies]\n\"acme.a\" = { path = \"../a\" }\n"
              "b/other.nyx", withFinalNewline "module other\n\nexport def y = 1" ])
    let error = Assert.Single(errorsOf (Package.find (package.Path "a")))
    Assert.EndsWith("packages can't depend on each other in a cycle: acme.a -> acme.b -> acme.a", error)

[<Fact>]
let ``A file of the package compiled on its own, as an editor compiles it, resolves its dependencies`` () =
    use package =
        new TempPackage(util @ [ app "\"acme.util\" = { path = \"../util\" }"; mainImporting "acme.util/strings" "ignore(shout(\"hi\"))" ])
    let mainPath = package.Path "app/main.nyx"
    let result = Package.compileSourceWith Linters.none mainPath (File.ReadAllText mainPath)
    Assert.Empty(result.Diagnostics)
