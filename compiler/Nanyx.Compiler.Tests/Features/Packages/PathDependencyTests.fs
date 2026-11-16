module Nanyx.Compiler.Tests.Features.Packages.PathDependencies

open System.IO
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A package that depends on another in a directory on disk, `(package = "acme.util", path = "../util")`, and imports
// its public modules as `acme.util/strings`.

/// A manifest of the package `name`, whose `package` makes public the modules `publicModules`, and which depends on
/// `dependencies`, each a package's name and the directory beside its own that it is in.
let private manifest (name: string) (publicModules: string list) (dependencies: (string * string) list) =
    let publicModules = publicModules |> List.map (sprintf "\"%s\"") |> String.concat ", "
    let dependencies =
        dependencies
        |> List.map (fun (name, directory) -> $"(package = \"{name}\", path = \"../{directory}\")")
        |> String.concat ", "
    $"(\n  package = (name = \"{name}\", version = \"0.1.0\", publicModules = [{publicModules}])\n  dependencies = [{dependencies}]\n)\n"

let private util =
    [ "util/util.nyxp", manifest "acme.util" [ "strings" ] []
      "util/strings.nyx",
      withFinalNewline """module strings

import secrets

export def shout = { s: string -> s ++ mark }"""
      "util/secrets.nyx", withFinalNewline """module secrets

export def mark = "!" """ ]

/// The app's manifest, depending on `dependencies`, run by the console host.
let private app (dependencies: (string * string) list) =
    "app/app.nyxp", (manifest "app" [] dependencies).Replace("\n)\n", $"\n  {consoleHostEntry}\n)\n")

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
    | Result.Error errors -> failwith (String.concat "\n" (errors |> Seq.map string))

/// The diagnostics of building the package in `app`.
let private buildDiagnostics (package: TempPackage) =
    (Package.build (okOf (Package.find (package.Path "app")))).Diagnostics

[<Fact>]
let ``A public module of a dependency is imported by the package's name and the module's`` () =
    use package =
        new TempPackage(util @ [ app [ "acme.util", "util" ]; mainImporting "acme.util/strings" "ignore(shout(\"hi\"))" ])
    let found = okOf (Package.find (package.Path "app"))
    Assert.Equal<string list>([ "acme.util" ], found.Dependencies |> List.map _.Manifest.Name)
    Assert.Equal<string list>([ "strings" ], found.Dependencies.Head.Manifest.PublicModules |> List.map fst)
    let build = Package.build found
    Assert.Empty(build.Diagnostics)
    Assert.True((Package.entryResult build).Value.Typed.IsSome)

[<Fact>]
let ``A dependency is read as its name and the directory it is in`` () =
    use package = new TempPackage(util @ [ app [ "acme.util", "util" ]; mainImporting "acme.util/strings" "ignore(shout(\"hi\"))" ])
    let read = okOf (Manifest.read (package.Path "app/app.nyxp"))
    Assert.Equal<string list>([ "acme.util" ], read.Dependencies |> List.map _.Name)
    Assert.Equal(Path.GetFullPath(package.Path "util"), read.Dependencies.Head.Directory)

[<Fact>]
let ``A version is refused, since packages can't be downloaded yet`` () =
    let manifest = """(package = (name = "app", version = "0.1.0"), dependencies = [(package = "acme.util", version = "1.2")])"""
    match Manifest.check "app.nyxp" manifest with
    | Result.Error [ error ] ->
        Assert.Equal(DiagnosticCodes.UnsupportedManifestField.Code, error.Code)
        Assert.Equal(
            """a dependency's version names a package to download, but packages can't be downloaded yet: give the directory the package is in, path = "../util" """.TrimEnd(),
            error.Message)
    | other -> failwith $"Unexpected: %A{other}"

[<Fact>]
let ``A dependency without a path is an error`` () =
    let manifest = """(package = (name = "app", version = "0.1.0"), dependencies = [(package = "acme.util")])"""
    match Manifest.check "app.nyxp" manifest with
    | Result.Error [ error ] -> Assert.Equal(DiagnosticCodes.MissingManifestField.Code, error.Code)
    | other -> failwith $"Unexpected: %A{other}"

[<Fact>]
let ``A dependency must be a directory with a manifest`` () =
    use package = new TempPackage([ app [ "acme.util", "missing" ]; mainImporting "nanyx/math" "println(abs(1))" ])
    let error = Assert.Single(errorsOf (Package.find (package.Path "app")))
    Assert.EndsWith($"""error NYX0023: the dependency acme.util can't be read: {Path.GetFullPath(package.Path "missing")} is not a directory""", error)

[<Fact>]
let ``A dependency's package must have the name the dependant gives it`` () =
    use package = new TempPackage(util @ [ app [ "acme.tools", "util" ]; mainImporting "nanyx/math" "println(abs(1))" ])
    let error = Assert.Single(errorsOf (Package.find (package.Path "app")))
    Assert.EndsWith(
        $"""error NYX0023: the package in {Path.GetFullPath(package.Path "util")} is named acme.util, not acme.tools: the names must match""",
        error)

[<Fact>]
let ``A module a dependency does not make public can't be imported`` () =
    use package =
        new TempPackage(util @ [ app [ "acme.util", "util" ]; mainImporting "acme.util/secrets" "ignore(mark)" ])
    let error = Assert.Single(buildDiagnostics package)
    Assert.Equal(DiagnosticCodes.ModuleNotPublic.Code, error.Code)
    Assert.Equal(
        "The module 'secrets' of the package 'acme.util' is not public, so another package can't import it: its public modules are 'strings'",
        error.Message)

[<Fact>]
let ``A public module that no source file declares is an error at its entry, naming the closest module`` () =
    let misspelt = [ "util/util.nyxp", manifest "acme.util" [ "strngs" ] [] ] @ List.tail util
    use package = new TempPackage(misspelt @ [ app [ "acme.util", "util" ]; mainImporting "acme.util/strings" "ignore(shout(\"hi\"))" ])
    let message = "publicModules names the module 'strngs', but no source file of acme.util declares it: did you mean 'strings'?"
    Assert.EndsWith($"util.nyxp:2:70: error NYX0023: {message}", Assert.Single(errorsOf (Package.find (package.Path "util"))))
    Assert.EndsWith($"error NYX0023: {message}", Assert.Single(errorsOf (Package.find (package.Path "app"))))
    let manifestPath = package.Path "util/util.nyxp"
    let error = Assert.Single((Package.compileManifest manifestPath (File.ReadAllText manifestPath)).Diagnostics)
    Assert.Equal(DiagnosticCodes.InvalidManifestValue.Code, error.Code)
    Assert.Equal(message, error.Message)
    Assert.Equal("\"strings\"", (Assert.Single (Assert.Single error.Fixes).Edits).NewText)

[<Fact>]
let ``A package a dependency depends on can't be imported without depending on it`` () =
    let middle =
        [ "middle/middle.nyxp", manifest "acme.middle" [ "loud" ] [ "acme.util", "util" ]
          "middle/loud.nyx", withFinalNewline """module loud

import acme.util/strings

export def louder = { s: string -> shout(shout(s)) }""" ]
    use package =
        new TempPackage(util @ middle @ [ app [ "acme.middle", "middle" ]; mainImporting "acme.util/strings" "ignore(shout(\"hi\"))" ])
    let error = Assert.Single(buildDiagnostics package)
    Assert.Equal(DiagnosticCodes.IndirectDependency.Code, error.Code)
    Assert.StartsWith("The package 'acme.util' is not a dependency of 'app', only of a package it depends on", error.Message)

[<Fact>]
let ``A dependency's own dependencies are imported in its files`` () =
    let middle =
        [ "middle/middle.nyxp", manifest "acme.middle" [ "loud" ] [ "acme.util", "util" ]
          "middle/loud.nyx", withFinalNewline """module loud

import acme.util/strings

export def louder = { s: string -> shout(shout(s)) }""" ]
    use package =
        new TempPackage(util @ middle @ [ app [ "acme.middle", "middle" ]; mainImporting "acme.middle/loud" "ignore(louder(\"hi\"))" ])
    Assert.Empty(buildDiagnostics package)

[<Fact>]
let ``A package that is no dependency is not found`` () =
    use package = new TempPackage([ app []; mainImporting "acme.util/strings" "ignore(1)" ])
    let error = Assert.Single(buildDiagnostics package)
    Assert.Equal(DiagnosticCodes.ImportNotFound.Code, error.Code)
    Assert.EndsWith("there is no package 'acme.util': 'app' can import the standard library, its own modules, and the public modules of the packages it depends on, and it has no dependencies", error.Message)

[<Fact>]
let ``A build has one version of each package`` () =
    let copy = util |> List.map (fun (path, text) -> path.Replace("util/", "copy/"), text)
    let middle =
        [ "middle/middle.nyxp", manifest "acme.middle" [ "loud" ] [ "acme.util", "copy" ]
          "middle/loud.nyx", withFinalNewline """module loud

export def louder = { s: string -> s }""" ]
    use package =
        new TempPackage(
            util @ copy @ middle
            @ [ app [ "acme.util", "util"; "acme.middle", "middle" ]
                mainImporting "nanyx/math" "println(abs(1))" ])
    let error = Assert.Single(errorsOf (Package.find (package.Path "app")))
    Assert.Contains(
        $"""error NYX0028: the build reaches the package acme.util by two paths, {Path.GetFullPath(package.Path "util")} and {Path.GetFullPath(package.Path "copy")}""",
        error)

[<Fact>]
let ``The same package reached through two dependants is one package`` () =
    let middle =
        [ "middle/middle.nyxp", manifest "acme.middle" [ "loud" ] [ "acme.util", "util" ]
          "middle/loud.nyx", withFinalNewline """module loud

import acme.util/strings

export def louder = { s: string -> shout(s) }""" ]
    use package =
        new TempPackage(
            util @ middle
            @ [ app [ "acme.util", "util"; "acme.middle", "middle" ]
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
            [ "a/a.nyxp", manifest "acme.a" [] [ "acme.b", "b" ]
              "a/main.nyx", withFinalNewline "module main\n\nexport def x = 1"
              "b/b.nyxp", manifest "acme.b" [] [ "acme.a", "a" ]
              "b/other.nyx", withFinalNewline "module other\n\nexport def y = 1" ])
    let error = Assert.Single(errorsOf (Package.find (package.Path "a")))
    Assert.EndsWith("error NYX0027: packages can't depend on each other in a cycle: acme.a -> acme.b -> acme.a", error)

[<Fact>]
let ``A file of the package compiled on its own, as an editor compiles it, resolves its dependencies`` () =
    use package =
        new TempPackage(util @ [ app [ "acme.util", "util" ]; mainImporting "acme.util/strings" "ignore(shout(\"hi\"))" ])
    let mainPath = package.Path "app/main.nyx"
    let result = Package.compileSourceWith Linters.none mainPath (File.ReadAllText mainPath)
    Assert.Empty(result.Diagnostics)
