/// The compiler ships the console host and its interface package as source directories beside it, at its own version
/// (#1342 and #1344, see docs/src/content/deep-dives/shipped-packages.md).
module Nanyx.Compiler.Tests.Features.Packages.ShippedPackages

open System.IO
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The manifest of the shipped package `name`, read.
let private manifestOf (name: string) =
    let path = Path.Combine(ShippedPackages.directoryOf name, name + Manifest.extension)
    match Manifest.check path (File.ReadAllText path) with
    | Result.Ok manifest -> manifest
    | Result.Error errors -> failwith (errors |> List.map Manifest.describe |> String.concat "\n")

[<Fact>]
let ``The console host is shipped beside the compiler, at the compiler's version`` () =
    let manifest = manifestOf Hosts.consolePackage
    Assert.Equal(Hosts.consolePackage, manifest.Name)
    Assert.Equal(Toolchain.version, manifest.Version)
    // `ShippedPackages` reads a shipped package's sources from its `src` directory.
    Assert.Equal<string list>([ "src/**/*.nyx" ], manifest.Sources)
    Assert.Equal<string list>([ "cli.host.memory"; "cli.host"; "cli.host.wasi"; "cli.prelude" ], (ShippedPackages.tryModules Hosts.consolePackage).Value.PublicModules)
    // It declares that it is a host, with its root module and its prelude.
    let host = manifest.Declares.Value
    Assert.Equal(("cli.host", Some "cli.prelude"), (host.Module, host.Prelude))
    // And the root `nanyx run --files-in-memory` builds a program from (see `Host.InMemory`).
    Assert.Equal(Some "cli.host.memory", host.FilesInMemory)
    Assert.Equal(host, HostPackages.console.Value)

[<Fact>]
let ``The console's interface package is shipped beside the compiler, and the console host depends on it`` () =
    let core = manifestOf "nanyx.cli"
    Assert.Equal(("nanyx.cli", Toolchain.version), (core.Name, core.Version))
    Assert.Equal<string list>([ "console"; "env" ], (ShippedPackages.tryModules "nanyx.cli").Value.PublicModules)
    Assert.Equal(None, core.Declares)
    let host = manifestOf Hosts.consolePackage
    Assert.Equal<string list>([ "nanyx.cli"; "nanyx.fs"; "nanyx.http"; "nanyx.wasi" ], host.Dependencies |> List.map _.Name)
    // A script's build reads the host's package with the shipped packages it depends on.
    let modules = (ShippedPackages.tryModules Hosts.consolePackage).Value
    Assert.Equal<string list>([ "nanyx.cli"; "nanyx.fs"; "nanyx.http"; "nanyx.wasi" ], modules.Dependencies |> List.map _.Package)
    Assert.Equal<string list>([ "console"; "env" ], modules.Dependencies.Head.PublicModules)

[<Fact>]
let ``The file system's interface package is shipped beside the compiler, with no dependencies`` () =
    let fs = manifestOf "nanyx.fs"
    Assert.Equal(("nanyx.fs", Toolchain.version), (fs.Name, fs.Version))
    Assert.Equal(None, fs.Declares)
    Assert.Empty(fs.Dependencies)
    Assert.Equal<string list>([ "dir"; "file"; "filesystem"; "path" ], (ShippedPackages.tryModules "nanyx.fs").Value.PublicModules |> List.sort)

[<Fact>]
let ``The HTTP interface package is shipped beside the compiler, with no dependencies`` () =
    let http = manifestOf "nanyx.http"
    Assert.Equal(("nanyx.http", Toolchain.version), (http.Name, http.Version))
    Assert.Equal(None, http.Declares)
    Assert.Empty(http.Dependencies)
    Assert.Equal<string list>([ "client"; "http"; "request"; "response"; "wire" ], (ShippedPackages.tryModules "nanyx.http").Value.PublicModules |> List.sort)

[<Fact>]
let ``The HTTP host is shipped beside the compiler, and takes a port, 3000 by default`` () =
    let host = (manifestOf "nanyx.http.host").Declares.Value
    Assert.Equal(("http.host", Some "http.prelude"), (host.Module, host.Prelude))
    Assert.Equal(3000, HostSettings.port (Some host) None)
    Assert.Equal<string list>([ "nanyx.http"; "nanyx.wasi" ], (manifestOf "nanyx.http.host").Dependencies |> List.map _.Name)

[<Fact>]
let ``The playground host is shipped beside the compiler, and depends only on the console's interface package`` () =
    let host = (manifestOf "nanyx.playground.host").Declares.Value
    Assert.Equal(("playground.host", Some "playground.prelude"), (host.Module, host.Prelude))
    let manifest = manifestOf "nanyx.playground.host"
    Assert.Equal(("nanyx.playground.host", Toolchain.version), (manifest.Name, manifest.Version))
    Assert.Equal<string list>([ "nanyx.cli" ], manifest.Dependencies |> List.map _.Name)

[<Fact>]
let ``The test host is shipped beside the compiler, and depends on the console's interface package, the file system's and the host library it runs @test(#io) tests with`` () =
    let host = (manifestOf Hosts.testPackage).Declares.Value
    Assert.Equal(("test.host", Some "test.prelude"), (host.Module, host.Prelude))
    // And the root `nanyx test --files-in-memory` builds a test build from (#3195).
    Assert.Equal(Some "test.host.memory", host.FilesInMemory)
    let manifest = manifestOf Hosts.testPackage
    Assert.Equal((Hosts.testPackage, Toolchain.version), (manifest.Name, manifest.Version))
    Assert.Equal<string list>([ "nanyx.cli"; "nanyx.fs"; "nanyx.wasi" ], manifest.Dependencies |> List.map _.Name)

[<Fact>]
let ``A library uses Console by depending on the console's interface package and importing it`` () =
    let library (dependencies: string) =
        [ "acme.greet.nyxp",
          $"""(package = (name = "acme.greet", version = "0.1.0", sources = ["src/**/*.nyx"]), dependencies = {dependencies})"""
          "src/greet.nyx",
          "module greet\n\nimport nanyx.cli/console\n\nexport def greet: [Console] string -> () = { name -> println(\"hello {name}\") }\n" ]
    let diagnosticsOf (dependencies: string) =
        use package = new TempPackage(library dependencies)
        let file = package.Path "src/greet.nyx"
        (Package.compileSourceWith (SeededPackages.user ()) Linters.none file (File.ReadAllText file)).Diagnostics
    Assert.Empty(diagnosticsOf $"""[(package = "nanyx.cli", version = "{Toolchain.version}")]""")
    // A library sees no host's prelude, so without the dependency it can't name the package.
    let diagnostic = Assert.Single(diagnosticsOf "[]")
    Assert.Equal(DiagnosticCodes.ImportNotFound.Code, diagnostic.Code)

[<Fact>]
let ``The console host's module is one of the shipped package's`` () =
    let modules = (ShippedPackages.tryModules Hosts.consolePackage).Value
    Assert.True(modules.Modules.ContainsKey HostPackages.console.Value.Module, $"%A{modules.Modules}")

[<Fact>]
let ``A package the compiler doesn't ship has no modules`` () =
    Assert.Equal(None, ShippedPackages.tryModules "acme.nothing")

[<Fact>]
let ``A shipped package's file is named after its package, wherever the compiler is`` () =
    let file = Path.Combine(ShippedPackages.directoryOf Hosts.consolePackage, "src", "cli.host.nyx")
    Assert.Equal(Some "nanyx.cli.host/src/cli.host.nyx", ShippedPackages.tryDisplay file)
    Assert.Equal(None, ShippedPackages.tryDisplay (Path.GetFullPath "main.nyx"))

[<Fact>]
let ``Every shipped package is published from this repository, at the compiler's version, building with the compiler's release`` () =
    // A release publishes each one to the package index, whose sync records the releases of the repository a manifest
    // names, so each names this one. A host, and a host library hosts are written on, build with the compiler they ship
    // with only, since they use its internals; any other with it or a later one.
    let manifests =
        ShippedPackages.names ()
        |> List.map (fun name ->
            let manifest = manifestOf name
            name, manifest.Version, manifest.Repository |> Option.map string, manifest.Nanyx |> Option.map string)
    let expected =
        ShippedPackages.names ()
        |> List.map (fun name ->
            let manifest = manifestOf name
            let nanyx =
                if manifest.Declares.IsSome || manifest.HostLibrary then $"={Toolchain.version}"
                else NewPackage.nanyxOf Toolchain.version
            name, Toolchain.version, Some "nanyx-lang/nanyx", Some nanyx)
    Assert.Equal<(string * string * string option * string option) list>(expected, manifests)
    Assert.Equal(11, manifests.Length)
