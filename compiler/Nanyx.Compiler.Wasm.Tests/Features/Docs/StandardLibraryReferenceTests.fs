/// The standard library's pages on the docs site (docs/src/content/stdlib) list what the library, and each package the
/// compiler ships but the hosts, exports, as `nanyx stdlib-docs` writes it from their doc comments.
module TranspilerWasmTests.Features.Docs.StandardLibraryReference

open System.IO
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

[<Fact>]
let ``Every standard library page's reference is the library's`` () =
    for path, update in StandardLibraryDocs.update (Path.Combine(docsContentDirectory, "stdlib")) do
        match update with
        | StandardLibraryDocs.UpToDate -> ()
        | StandardLibraryDocs.OutOfDate written ->
            // Run `nanyx stdlib-docs ../docs/src/content/stdlib` from compiler/ to write it.
            Assert.Equal(written, File.ReadAllText(path).ReplaceLineEndings "\n")
        | StandardLibraryDocs.Unwritable message -> Assert.Fail $"{Path.GetFileName path} {message}"

[<Fact>]
let ``A public module of a package the compiler ships has a page, but a host's modules don't`` () =
    let pages = StandardLibraryDocs.references ()
    Assert.Contains("console", pages.Keys)
    Assert.Contains("env", pages.Keys)
    Assert.Contains("## `Console`", pages["console"])
    Assert.DoesNotContain("console-host", pages.Keys)
