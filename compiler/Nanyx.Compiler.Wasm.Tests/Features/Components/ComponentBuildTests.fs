/// `nanyx build` of a package whose host has a component root (#2237, #2238): a WASI 0.3 component, built from that
/// root, unless `--core` asks for a core module; and a component root that provides other contexts than its core root.
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
let ``A package under the console host is built as a component of nanyx:console/command`` () =
    use app = consoleApp hello
    let built = nanyxIn app.Path [ "build"; "--release" ]
    Assert.Equal("", built.StandardError)
    Assert.Equal(0, built.ExitCode)
    Assert.Contains("a component of nanyx:console/command", built.StandardOutput)
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
let ``A program that makes HTTP requests is built as a component`` () =
    use app = consoleApp "module main\n\n@entry\ndef main: [Async, Console, Http] () -> () = { println(1) }\n"
    let built = nanyxIn app.Path [ "build"; "--release" ]
    Assert.Equal("", built.StandardError)
    Assert.Equal(0, built.ExitCode)
    Assert.Contains("a component of nanyx:console/command", built.StandardOutput)

/// A host, `acme.extra`, with a context of its own, `Extra`, whose core root's `run` is `coreRun` and whose component
/// root's is `componentRun`, each with what it provides and `use`s.
let private extraHost (coreRun: string) (componentRun: string) =
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
      $"""export module extra.host

import (
  nanyx/host.runtime
  extra as (Extra)
)

{coreRun}
"""
      "extra.host.wasi.nyx",
      $"""export module extra.host.wasi

import (
  nanyx/host.runtime
  extra as (Extra)
)

{componentRun}
""" ]

let private providing = """export def run: [HostRuntime] ([Extra] () -> a) -> () = { entry ->
  use Extra(n = { _ -> 1 })
  ignore(entry())
}"""

let private notProviding = """export def run: [HostRuntime] (() -> a) -> () = { entry -> ignore(entry()) }"""

/// A build of a program under `host` (see `extraHost`), as a component.
let private buildUnder (host: (string * string) list) =
    use hostDirectory = new TempDirectory()
    for name, text in host do
        File.WriteAllText(Path.Combine(hostDirectory.Path, name), text.ReplaceLineEndings "\n")
    use app = new TempDirectory()
    Directory.CreateDirectory(Path.Combine(app.Path, "src")) |> ignore
    let hostPath = hostDirectory.Path.Replace("\\", "/")
    let manifest = $"""(package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"]), host = (package = "acme.extra", path = "{hostPath}"))"""
    File.WriteAllText(Path.Combine(app.Path, "app.nyxp"), manifest + "\n")
    File.WriteAllText(Path.Combine(app.Path, "src", "main.nyx"), "module main\n\n@entry\ndef main = { -> () }\n")
    nanyxIn app.Path [ "build"; "--release" ]

[<Fact>]
let ``A component root that provides a context its core root doesn't is an error at its run`` () =
    let built = buildUnder (extraHost notProviding providing)
    Assert.Equal(1, built.ExitCode)
    Assert.Contains(DiagnosticCodes.ComponentRootContextsDiffer.Code, built.StandardOutput)
    Assert.Contains("The component root's 'run' provides 'Extra', which the core root 'acme.extra/extra.host' doesn't", built.StandardOutput)

[<Fact>]
let ``A component root that doesn't provide a context its core root does is an error at its run`` () =
    let built = buildUnder (extraHost providing notProviding)
    Assert.Equal(1, built.ExitCode)
    Assert.Contains(DiagnosticCodes.ComponentRootContextsDiffer.Code, built.StandardOutput)
    Assert.Contains("The component root's 'run' doesn't provide 'Extra', which the core root 'acme.extra/extra.host' does", built.StandardOutput)
