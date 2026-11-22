/// `nanyx build` of a package whose host has a component root (#2237): a WASI 0.3 component, built from that root,
/// unless `--core` asks for a core module; an entry point that needs a context the component root doesn't provide; and
/// a component root that provides one its core root doesn't.
module TranspilerWasmTests.Features.Components.ComponentBuild

open System.IO
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

/// A package, `app`, under the console host, whose `main.nyx` is `main`, written to a new directory.
let private consoleApp (main: string) =
    let app = new TempDirectory()
    Directory.CreateDirectory(Path.Combine(app.Path, "src")) |> ignore
    let manifest = $"""(package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"]), {consoleHostEntry})"""
    File.WriteAllText(Path.Combine(app.Path, "app.nyxp"), manifest + "\n")
    File.WriteAllText(Path.Combine(app.Path, "src", "main.nyx"), main.ReplaceLineEndings "\n")
    app

let private hello = "module main\n\n@entry\ndef main = { println(\"hello\") }\n"

/// What a binary starts with: a component's preamble differs from a core module's in its version and layer.
let private preamble (path: string) = File.ReadAllBytes(path).[.. 7]

let private componentPreamble = [| 0x00uy; 0x61uy; 0x73uy; 0x6duy; 0x0duy; 0x00uy; 0x01uy; 0x00uy |]

[<Fact>]
let ``A package under the console host is built as a component of wasi:cli/command`` () =
    use app = consoleApp hello
    let built = nanyxIn app.Path [ "build"; "--release" ]
    Assert.Equal("", built.StandardError)
    Assert.Equal(0, built.ExitCode)
    Assert.Contains("a component of wasi:cli/command@0.3.0", built.StandardOutput)
    Assert.Equal<byte array>(componentPreamble, preamble (Path.Combine(app.Path, "out", "app.wasm")))

[<Fact>]
let ``--core builds a core module, which the runtime runs`` () =
    use app = consoleApp hello
    let built = nanyxIn app.Path [ "build"; "--core" ]
    Assert.Equal(0, built.ExitCode)
    Assert.DoesNotContain("component", built.StandardOutput)
    let wasm = File.ReadAllBytes(Path.Combine(app.Path, "out", "app.wasm"))
    Assert.Equal<byte array>([| 0x00uy; 0x61uy; 0x73uy; 0x6duy; 0x01uy; 0x00uy; 0x00uy; 0x00uy |], wasm.[.. 7])

[<Fact>]
let ``A program that reads and writes files is built as a component`` () =
    use app = consoleApp "module main\n\n@entry\ndef main: [Async, Console, FileSystem] () -> () = { println(1) }\n"
    let built = nanyxIn app.Path [ "build"; "--release" ]
    Assert.Equal("", built.StandardError)
    Assert.Equal(0, built.ExitCode)
    Assert.Equal<byte array>(componentPreamble, preamble (Path.Combine(app.Path, "out", "app.wasm")))

[<Fact>]
let ``An entry that needs a context the component root doesn't provide is an error naming it`` () =
    use app = consoleApp "module main\n\n@entry\ndef main: [Console, Http] () -> () = { println(1) }\n"
    let built = nanyxIn app.Path [ "build"; "--release" ]
    Assert.Equal(1, built.ExitCode)
    Assert.Contains(DiagnosticCodes.EntryDoesNotFitHost.Code, built.StandardOutput)
    Assert.Contains("It doesn't fit the host 'nanyx.console/console.host.wasi'", built.StandardOutput)
    Assert.Contains("it doesn't provide 'Http' in a component: build with --core for a core module", built.StandardOutput)
    // The core root provides it.
    Assert.Equal(0, (nanyxIn app.Path [ "build"; "--release"; "--core" ]).ExitCode)

/// A host, `acme.extra`, whose component root's `run` provides `Extra`, a context of its own, which its core root's
/// doesn't.
let private extraHost =
    [ "acme.extra.nyxp",
      """(
  package = (
    name = "acme.extra"
    version = "0.1.0"
    host = (root = "extra.host", component = (root = "extra.host.wasi", world = "wasi:cli/command@0.3.0"))
  )
)
"""
      "extra.nyx",
      """export module extra

export context Extra = (n: () -> int)
"""
      "extra.host.nyx",
      """export module extra.host

import nanyx/host.runtime

export def run: [HostRuntime] (() -> a) -> () = { entry -> ignore(entry()) }
"""
      "extra.host.wasi.nyx",
      """export module extra.host.wasi

import (
  nanyx/host.runtime
  extra as (Extra)
)

export def run: [HostRuntime] ([Extra] () -> a) -> () = { entry ->
  use Extra(n = { _ -> 1 })
  ignore(entry())
}
""" ]

[<Fact>]
let ``A component root that provides a context its core root doesn't is an error at its run`` () =
    use host = new TempDirectory()
    for name, text in extraHost do
        File.WriteAllText(Path.Combine(host.Path, name), text.ReplaceLineEndings "\n")
    use app = new TempDirectory()
    Directory.CreateDirectory(Path.Combine(app.Path, "src")) |> ignore
    let hostPath = host.Path.Replace("\\", "/")
    let manifest = $"""(package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"]), host = (package = "acme.extra", path = "{hostPath}"))"""
    File.WriteAllText(Path.Combine(app.Path, "app.nyxp"), manifest + "\n")
    File.WriteAllText(Path.Combine(app.Path, "src", "main.nyx"), "module main\n\n@entry\ndef main = { -> () }\n")
    let built = nanyxIn app.Path [ "build"; "--release" ]
    Assert.Equal(1, built.ExitCode)
    Assert.Contains(DiagnosticCodes.ComponentRootProvidesMore.Code, built.StandardOutput)
    Assert.Contains("The component root's 'run' provides 'Extra', which the core root 'acme.extra/extra.host' doesn't", built.StandardOutput)
