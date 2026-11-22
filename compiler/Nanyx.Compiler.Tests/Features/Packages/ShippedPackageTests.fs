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
    Assert.Equal<string list>([ "console.host"; "console.host.wasi"; "console.prelude" ], (ShippedPackages.tryModules Hosts.consolePackage).Value.PublicModules)
    // It declares that it is a host, with its root module and its prelude.
    let host = manifest.Declares.Value
    Assert.Equal(("console.host", Some "console.prelude"), (host.Module, host.Prelude))
    Assert.Equal(host, HostPackages.console.Value)

[<Fact>]
let ``The console's interface package is shipped beside the compiler, and the console host depends on it`` () =
    let core = manifestOf "nanyx.console.core"
    Assert.Equal(("nanyx.console.core", Toolchain.version), (core.Name, core.Version))
    Assert.Equal<string list>([ "console"; "env" ], (ShippedPackages.tryModules "nanyx.console.core").Value.PublicModules)
    Assert.Equal(None, core.Declares)
    let host = manifestOf Hosts.consolePackage
    Assert.Equal<string list>([ "nanyx.console.core"; "nanyx.fs"; "nanyx.http.core" ], host.Dependencies |> List.map _.Name)
    // A script's build reads the host's package with the shipped packages it depends on.
    let modules = (ShippedPackages.tryModules Hosts.consolePackage).Value
    Assert.Equal<string list>([ "nanyx.console.core"; "nanyx.fs"; "nanyx.http.core" ], modules.Dependencies |> List.map _.Package)
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
    let http = manifestOf "nanyx.http.core"
    Assert.Equal(("nanyx.http.core", Toolchain.version), (http.Name, http.Version))
    Assert.Equal(None, http.Declares)
    Assert.Empty(http.Dependencies)
    Assert.Equal<string list>([ "client"; "http"; "request"; "response"; "wire" ], (ShippedPackages.tryModules "nanyx.http.core").Value.PublicModules |> List.sort)

[<Fact>]
let ``The HTTP host is shipped beside the compiler, and takes a port, 3000 by default`` () =
    match HostPackages.tryShipped "nanyx.http" with
    | Ok host ->
        Assert.Equal(("http.host", Some "http.prelude"), (host.Module, host.Prelude))
        Assert.Equal(3000, HostSettings.port (Some host) None)
    | Error message -> failwith message
    Assert.Equal<string list>([ "nanyx.http.core" ], (manifestOf "nanyx.http").Dependencies |> List.map _.Name)

[<Fact>]
let ``The playground host is shipped beside the compiler, and depends only on the console's interface package`` () =
    match HostPackages.tryShipped "nanyx.playground" with
    | Ok host -> Assert.Equal(("playground.host", Some "playground.prelude"), (host.Module, host.Prelude))
    | Error message -> failwith message
    let manifest = manifestOf "nanyx.playground"
    Assert.Equal(("nanyx.playground", Toolchain.version), (manifest.Name, manifest.Version))
    Assert.Equal<string list>([ "nanyx.console.core" ], manifest.Dependencies |> List.map _.Name)

[<Fact>]
let ``The regex package is shipped beside the compiler, with one module and no dependencies`` () =
    let manifest = manifestOf "nanyx.regex"
    Assert.Equal(("nanyx.regex", Toolchain.version), (manifest.Name, manifest.Version))
    Assert.Equal(None, manifest.Declares)
    Assert.Empty(manifest.Dependencies)
    Assert.Equal<string list>([ "regex" ], (ShippedPackages.tryModules "nanyx.regex").Value.PublicModules)

[<Fact>]
let ``A library uses Console by depending on the console's interface package and importing it`` () =
    let library (dependencies: string) =
        [ "acme.greet.nyxp",
          $"""(package = (name = "acme.greet", version = "0.1.0", sources = ["src/**/*.nyx"]), dependencies = {dependencies})"""
          "src/greet.nyx",
          "module greet\n\nimport nanyx.console.core/console\n\nexport def greet: [Console] string -> () = { name -> println(\"hello {name}\") }\n" ]
    let diagnosticsOf (dependencies: string) =
        use package = new TempPackage(library dependencies)
        let file = package.Path "src/greet.nyx"
        (Package.compileSourceWith Linters.none file (File.ReadAllText file)).Diagnostics
    Assert.Empty(diagnosticsOf $"""[(package = "nanyx.console.core", version = "{Toolchain.version}")]""")
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
    let file = Path.Combine(ShippedPackages.directoryOf Hosts.consolePackage, "src", "console.host.nyx")
    Assert.Equal(Some "nanyx.console/src/console.host.nyx", ShippedPackages.tryDisplay file)
    Assert.Equal(None, ShippedPackages.tryDisplay (Path.GetFullPath "main.nyx"))
