/// Host libraries: a package whose manifest says `hostLibrary = true`, whose modules may require `HostRuntime` when the
/// build host's package depends on it, so that hosts share what they are written on.
module TranspilerWasmTests.Features.Hosts.HostLibrary

open System.IO
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

/// A host, `acme.quiet.host`, whose `run` ends the program with the exit code 7 through `exitWith`, which is in
/// `acme.exits`, a package it depends on, declared a host library when `hostLibrary` is true.
let private quietHost (hostLibrary: bool) =
    [ "acme.exits/acme.exits.nyxp",
      $"""(package = (name = "acme.exits", version = "0.1.0", hostLibrary = {(if hostLibrary then "true" else "false")}))
"""
      "acme.exits/exits.nyx",
      """export module exits

import nanyx/host.runtime

@extern("wasi_snapshot_preview1", "proc_exit")
def procExit: [HostRuntime] I32 -> ()

export def exitWith: [HostRuntime] () -> () = { -> procExit(7) }
"""
      "acme.quiet.host/acme.quiet.host.nyxp",
      """(
  package = (name = "acme.quiet.host", version = "0.1.0", host = (root = "quiet.host"))
  dependencies = [(package = "acme.exits", path = "../acme.exits")]
)
"""
      "acme.quiet.host/quiet.host.nyx",
      """export module quiet.host

import (
  nanyx/host.runtime
  acme.exits/exits as (exitWith)
)

export def run: [HostRuntime] (() -> a) -> () = { entry ->
  ignore(entry())
  exitWith()
}
""" ]

/// Writes `acme.quiet.host` and `acme.exits` (see `quietHost`) to `packages`.
let private writeQuietHost (packages: TempDirectory) (hostLibrary: bool) =
    for name, text in quietHost hostLibrary do
        packages.Write name (text.ReplaceLineEndings "\n")

/// What `nanyx run` does with a program under `acme.quiet.host` (see `quietHost`).
let private runUnder (hostLibrary: bool) =
    use packages = new TempDirectory()
    writeQuietHost packages hostLibrary
    use app = new TempDirectory()
    Directory.CreateDirectory(Path.Combine(app.Path, "src")) |> ignore
    let hostPath = Path.Combine(packages.Path, "acme.quiet.host").Replace("\\", "/")
    let manifest = $"""(package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"]), host = (package = "acme.quiet.host", path = "{hostPath}"))"""
    File.WriteAllText(Path.Combine(app.Path, "app.nyxp"), manifest + "\n")
    File.WriteAllText(Path.Combine(app.Path, "src", "main.nyx"), "module main\n\n@entry\ndef main = { -> () }\n")
    nanyxIn app.Path [ "run" ]

[<Fact>]
let ``A host calls what a host library it depends on writes on HostRuntime`` () =
    let outcome = runUnder true
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(7, outcome.ExitCode)

[<Fact>]
let ``A package that isn't a host library may not require HostRuntime, though a host depends on it`` () =
    let outcome = runUnder false
    Assert.Contains(DiagnosticCodes.HostRuntimeOutsideHost.Code, outcome.StandardError + outcome.StandardOutput)
    Assert.Contains("'exitWith' requires the context 'HostRuntime'", outcome.StandardError + outcome.StandardOutput)

/// What `nanyx check` says of the package `package`, one of `acme.quiet.host` and `acme.exits` (see `quietHost`), checked
/// on its own.
let private checkAlone (hostLibrary: bool) (package: string) =
    use packages = new TempDirectory()
    writeQuietHost packages hostLibrary
    nanyxIn (Path.Combine(packages.Path, package)) [ "check" ]

[<Fact>]
let ``A host checks on its own, with the host libraries it depends on`` () =
    let outcome = checkAlone true "acme.quiet.host"
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``A host library checks on its own`` () =
    let outcome = checkAlone true "acme.exits"
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``A host checked on its own still may not depend on a package that isn't a host library requiring HostRuntime`` () =
    let outcome = checkAlone false "acme.quiet.host"
    Assert.Contains(DiagnosticCodes.HostRuntimeOutsideHost.Code, outcome.StandardError + outcome.StandardOutput)
    Assert.Contains("'exitWith' requires the context 'HostRuntime'", outcome.StandardError + outcome.StandardOutput)
    Assert.Equal(1, outcome.ExitCode)
