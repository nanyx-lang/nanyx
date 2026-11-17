/// A host's root module split across files with `@partial module` (#1816).
module TranspilerWasmTests.Features.Hosts.RunPartialHost

open System.IO
open Xunit
open TranspilerWasmTestHelpers

/// A host, `acme.quiet`, whose `run` ends the program with the exit code 7 through `exitWith`, which is in the other
/// fragment of its root module.
let private quietHost =
    [ "acme.quiet.nyxp", """(package = (name = "acme.quiet", version = "0.1.0", host = (root = "quiet.host")))
"""
      "quiet.host.nyx",
      """@partial export module quiet.host

import nanyx/host.runtime

export def run: [HostRuntime] (() -> a) -> () = { entry ->
  ignore(entry())
  exitWith()
}
"""
      "quiet.host.exit.nyx",
      """@partial export module quiet.host

@extern("wasi_snapshot_preview1", "proc_exit")
def procExit: [HostRuntime] I32 -> ()

def exitWith: [HostRuntime] () -> () = { -> procExit(7) }
""" ]

[<Fact>]
let ``A program runs under a host whose root module is split across files`` () =
    use host = new TempDirectory()
    for name, text in quietHost do
        File.WriteAllText(Path.Combine(host.Path, name), text.ReplaceLineEndings "\n")
    use app = new TempDirectory()
    Directory.CreateDirectory(Path.Combine(app.Path, "src")) |> ignore
    let hostPath = host.Path.Replace("\\", "/")
    let manifest = $"""(package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"]), host = (package = "acme.quiet", path = "{hostPath}"))
"""
    let main = """module main

@entry
def main = { -> () }
"""
    File.WriteAllText(Path.Combine(app.Path, "app.nyxp"), manifest)
    File.WriteAllText(Path.Combine(app.Path, "src", "main.nyx"), main.ReplaceLineEndings "\n")
    let outcome = nanyxIn app.Path [ "run" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(7, outcome.ExitCode)
