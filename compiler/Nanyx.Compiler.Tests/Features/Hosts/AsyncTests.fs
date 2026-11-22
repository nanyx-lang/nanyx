/// `Async`, the context of the functions that can wait, which only a host provides (see
/// docs/src/content/deep-dives/async.md).
module Nanyx.Compiler.Tests.Features.Hosts.Async

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// `source` compiled as `src/main.nyx` of an application under the console host that depends on `nanyx.http.core`
/// and `nanyx.fs`.
let private compiledFile (source: string) =
    let manifest =
        $"""(
  package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"])
  dependencies = [
    (package = "nanyx.http.core", version = "{Toolchain.version}")
    (package = "nanyx.fs", version = "{Toolchain.version}")
  ]
  {consoleHostEntry}
)
"""
    use package = new TempPackage([ "app.nyxp", manifest; "src/main.nyx", source ])
    Package.compileSourceWith Linters.none (package.Path "src/main.nyx") source

/// The type a hover shows for the def `name` in `source`, compiled as `compiledFile` compiles it, with no diagnostics.
let private hoveredTypeOf (name: string) (source: string) =
    let result = compiledFile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Hover.typeAt Unicode result (source.IndexOf $"def {name}" + 4)
    |> Option.map (fun hover -> hover.Type.Substring($"def {name}: ".Length))

/// `source` after the module line and the import of a module that makes HTTP requests.
let private withHttp (source: string) =
    withFinalNewline (
        """module main

import nanyx.http.core/http as http

"""
        + normalized source
    )

[<Fact>]
let ``A function that calls http.get is inferred to require Async and Http`` () =
    let ty =
        hoveredTypeOf "fetchName" (
            withHttp
                """def fetchName = { url: string ->
  def response = http.get(url) \orCrash("no name")
  response.body
}"""
        )
    Assert.Equal(Some "[Async, Http] string -> string", ty)

[<Fact>]
let ``list.map takes a lambda that requires Async, and the function calling it requires Async`` () =
    let ty =
        hoveredTypeOf "bodies" (
            withHttp
                """def bodies = { urls: list(string) ->
  urls \list.map { url -> http.get(url) \orCrash("no answer") } \list.map { response -> response.body }
}"""
        )
    Assert.Equal(Some "[Async, Http] list(string) -> list(string)", ty)

[<Fact>]
let ``The host provides Async to the entry point`` () =
    let ty =
        hoveredTypeOf "main" (
            withHttp
                """def fetchName: [Async, Http] string -> string = { url ->
  def response = http.get(url) \orCrash("no name")
  response.body
}

@entry
def main = { println("Hello, {fetchName("https://example.com/name")}") }"""
        )
    Assert.Equal(Some "[Async, Console, Http] () -> ()", ty)

[<Fact>]
let ``http.get in a def whose type doesn't declare Async is the missing-context error`` () =
    let source =
        withHttp
            """def fetchName: [Http] string -> string = { url ->
  def response = http.get(url) \orCrash("no name")
  response.body
}"""
    let code, message, text = onlyDiagnosticIn source (compiledFile source)
    Assert.Equal(DiagnosticCodes.UndeclaredContext.Code, code)
    Assert.StartsWith("'http.get' requires the context 'Async', which 'fetchName' does not declare", message)
    Assert.Equal("http.get", text)

[<Fact>]
let ``A file read requires Async too`` () =
    let ty =
        hoveredTypeOf "contents" (
            withFinalNewline
                """module main

import (
  nanyx.fs/file as file
  nanyx.fs/path as path
)

def contents = { name: string -> file.read(path.of(name)) }"""
        )
    Assert.Equal(Some "[Async, FileSystem] string -> #some(string) | #error(FsError)", ty)

[<Fact>]
let ``Only the module that declares Async can make one with use`` () =
    let source =
        """def run = {
  use Async()
  42
}
"""
    let code, _, _ = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.NominalConstructedOutsideItsModule.Code, code)
