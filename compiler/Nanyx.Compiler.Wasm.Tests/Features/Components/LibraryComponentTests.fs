/// `nanyx build` of a package with defs marked `@component` and no `@entry` (#2326): a library component, built from a
/// root that imports each of its modules with such a def, in its host's world for libraries, exporting them and no
/// `run`.
module TranspilerWasmTests.Features.Components.LibraryComponents

open System.IO
open Xunit
open TranspilerWasmTestHelpers

/// A package, `acme.geo`, under the console host, with no `@entry`: two modules with defs marked `@component`, and one
/// with none, written to a new directory.
let private library () =
    let geo = new TempDirectory()
    Directory.CreateDirectory(Path.Combine(geo.Path, "src")) |> ignore
    let manifest = $"""(package = (name = "acme.geo", version = "0.1.0", sources = ["src/**/*.nyx"]), {consoleHostEntry})"""
    File.WriteAllText(Path.Combine(geo.Path, "geo.nyxp"), manifest + "\n")
    let write (name: string) (text: string) = File.WriteAllText(Path.Combine(geo.Path, "src", name), text)
    write "shapes.nyx" "module shapes\n\nimport helpers as helpers\n\n@component\nexport def area: float, float -> float = { width, height -> helpers.times(width, height) }\n"
    write "names.nyx" "module names\n\n@component\nexport def twice: int -> int = { n -> n * 2 }\n"
    write "helpers.nyx" "module helpers\n\nexport def times: float, float -> float = { a, b -> a * b }\n"
    geo

[<Fact>]
let ``A package with defs marked @component and no @entry is built as a library component of its host's library world`` () =
    use geo = library ()
    let built = nanyxIn geo.Path [ "build"; "--release" ]
    Assert.Equal("", built.StandardError)
    Assert.Equal(0, built.ExitCode)
    Assert.Contains("a component of acme:geo/component@0.1.0", built.StandardOutput)
    Assert.Equal<byte array>([| 0x00uy; 0x61uy; 0x73uy; 0x6duy; 0x0duy; 0x00uy; 0x01uy; 0x00uy |], File.ReadAllBytes(Path.Combine(geo.Path, "out", "acme.geo.wasm")).[.. 7])
    let wit = File.ReadAllText(Path.Combine(geo.Path, "out", "wit", "component.wit"))
    Assert.Contains("  include nanyx:console/library;", wit)
    Assert.Contains("  export shapes;", wit)
    Assert.Contains("  export names;", wit)
    Assert.DoesNotContain("helpers", wit)

[<Fact>]
let ``A library component's exports are called with no run, which it doesn't export`` () =
    use geo = library ()
    let built = nanyxIn geo.Path [ "build"; "--release"; "--wat" ]
    Assert.Equal(0, built.ExitCode)
    let engine, store, instance = instantiateCore (File.ReadAllText(Path.Combine(geo.Path, "out", "acme.geo.wat")))
    use _ = engine
    use _ = store
    Assert.Equal(6.0, instance.GetFunction<double, double, double>("acme:geo/shapes@0.1.0#area").Invoke(2.0, 3.0))
    Assert.Equal(14L, instance.GetFunction<int64, int64>("acme:geo/names@0.1.0#twice").Invoke(7L))
    Assert.Null(instance.GetFunction "wasi:cli/run@0.3.0#run")

[<Fact>]
let ``A library component under a host with no world for libraries is an error that says so`` () =
    use hostDirectory = new TempDirectory()
    let hostManifest =
        """(package = (name = "acme.bare", version = "0.1.0", host = (root = "bare.host", component = (root = "bare.host.wasi", world = "wasi:cli/command@0.3.0"))))"""
    File.WriteAllText(Path.Combine(hostDirectory.Path, "acme.bare.nyxp"), hostManifest + "\n")
    let root (name: string) = $"export module {name}\n\nimport nanyx/host.runtime\n\nexport def run: [HostRuntime] (() -> a) -> () = {{ entry -> ignore(entry()) }}\n"
    File.WriteAllText(Path.Combine(hostDirectory.Path, "bare.host.nyx"), root "bare.host")
    File.WriteAllText(Path.Combine(hostDirectory.Path, "bare.host.wasi.nyx"), root "bare.host.wasi")
    use app = new TempDirectory()
    Directory.CreateDirectory(Path.Combine(app.Path, "src")) |> ignore
    let hostPath = hostDirectory.Path.Replace("\\", "/")
    let manifest = $"""(package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"]), host = (package = "acme.bare", path = "{hostPath}"))"""
    File.WriteAllText(Path.Combine(app.Path, "app.nyxp"), manifest + "\n")
    File.WriteAllText(Path.Combine(app.Path, "src", "names.nyx"), "module names\n\n@component\nexport def twice: int -> int = { n -> n * 2 }\n")
    let built = nanyxIn app.Path [ "build"; "--release" ]
    Assert.Equal(1, built.ExitCode)
    Assert.Contains("app has @component defs and no @entry, so it is a library component, but its host acme.bare has no world for libraries", built.StandardError)

[<Fact>]
let ``A package with no @entry and no def marked @component is only checked`` () =
    use geo = library ()
    for file in [ "shapes.nyx"; "names.nyx" ] do
        File.Delete(Path.Combine(geo.Path, "src", file))
    let built = nanyxIn geo.Path [ "build"; "--release" ]
    Assert.Equal(0, built.ExitCode)
    Assert.Contains("it has no @entry, so no program was written", built.StandardOutput)
