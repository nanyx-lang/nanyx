/// The compiler ships the console host as a source directory beside it, at its own version (#1342, see
/// docs/src/content/work-in-progress/built-ins-as-packages.md).
module Nanyx.Compiler.Tests.Features.Packages.ShippedPackages

open System.IO
open Xunit
open NyxCompiler

/// The manifest of the shipped package `name`, read.
let private manifestOf (name: string) =
    let path = Path.Combine(ShippedPackages.directoryOf name, name + Manifest.extension)
    match Manifest.check path (File.ReadAllText path) with
    | Result.Ok manifest -> manifest
    | Result.Error errors -> failwith (errors |> List.map Manifest.describe |> String.concat "\n")

[<Fact>]
let ``The console host is shipped beside the compiler, at the compiler's version`` () =
    let manifest = manifestOf Hosts.console.Package
    Assert.Equal(Hosts.console.Package, manifest.Name)
    Assert.Equal(Toolchain.version, manifest.Version)
    // `ShippedPackages` reads a shipped package's sources from its `src` directory.
    Assert.Equal<string list>([ "src/**/*.nyx" ], manifest.Sources)
    Assert.Equal<string list>([ Hosts.console.Module ], manifest.PublicModules |> List.map fst)

[<Fact>]
let ``The console host's module is one of the shipped package's`` () =
    let modules = (ShippedPackages.tryModules Hosts.console.Package).Value
    Assert.True(modules.Modules.ContainsKey Hosts.console.Module, $"%A{modules.Modules}")

[<Fact>]
let ``A package the compiler doesn't ship has no modules`` () =
    Assert.Equal(None, ShippedPackages.tryModules "acme.nothing")

[<Fact>]
let ``A shipped package's file is named after its package, wherever the compiler is`` () =
    let file = Path.Combine(ShippedPackages.directoryOf Hosts.console.Package, "src", "console.host.nyx")
    Assert.Equal(Some "nanyx.console/src/console.host.nyx", ShippedPackages.tryDisplay file)
    Assert.Equal(None, ShippedPackages.tryDisplay (Path.GetFullPath "main.nyx"))
