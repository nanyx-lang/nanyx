/// A test that loads an instance of a context with `use`, a fake, and calls the code it tests, which requires the
/// context: the test's module is imported by the test build's root, so a context it declares privately, or imports, is
/// one the root doesn't name, and the backend types its members from the module's definition of it
/// (`TypedModule.InlinedTypeDefs`).
module TranspilerWasmTests.Features.Tests.TestUseInstance

open Xunit
open TranspilerWasmTestHelpers

/// What `nanyx test` gives for the script `source`, saved as `main.nyx` in a new directory.
let private testScript (source: string) =
    use directory = new TempDirectory()
    directory.Write "main.nyx" source
    nanyxIn directory.Path [ "test"; "main.nyx" ]

[<Fact>]
let ``A test loads a fake of a context its module declares, and the code it calls uses the fake`` () =
    let outcome =
        testScript
            """context Log = (log: string -> (), other: string -> ())

def greet: [Log] string -> () = { name -> log("hello, {name}") }

@test
def `greets through the fake` = {
  use Log(log = { line -> assert(line == "hello, test") }, other = { _ -> () })
  greet("test")
}
"""
    Assert.True(outcome.ExitCode = 0, outcome.StandardOutput + outcome.StandardError)
    Assert.Contains("PASS main.`greets through the fake`", outcome.StandardOutput)
    Assert.Contains("1 passed", outcome.StandardOutput)

[<Fact>]
let ``A test loads a fake FileSystem, and the code it calls reads through the fake`` () =
    let outcome =
        testScript
            """::install nanyx.fs

import (
  nanyx/async as (Async)
  nanyx.fs/file as file
  nanyx.fs/filesystem as (FileSystem)
  nanyx.fs/path as path
)

def port: [Async, FileSystem] () -> string = {
  match file.read(path.of("app.toml"))
    | #some(text) -> text
    | _ -> "none"
}

@test(#io)
def `reads the fake's file` = {
  use FileSystem(
    readFile = { _ -> #some("port = 8080") }
    readFileBytes = { _ -> #notFound }
    readFileChunk = { _, _, _ -> #notFound }
    writeFile = { _, _ -> #ok }
    appendFile = { _, _ -> #ok }
    pathExists = { _ -> true }
    deleteFile = { _ -> #ok }
    listDirectory = { _ -> #notFound }
    createDirectory = { _ -> #ok }
  )

  assert(port() == "port = 8080")
}
"""
    Assert.True(outcome.ExitCode = 0, outcome.StandardOutput + outcome.StandardError)
    Assert.Contains("1 passed", outcome.StandardOutput)
