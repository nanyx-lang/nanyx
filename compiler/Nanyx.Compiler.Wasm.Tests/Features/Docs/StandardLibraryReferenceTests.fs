/// The standard library's pages on the docs site (docs/src/content/stdlib) list what the library exports, as
/// `nanyx stdlib-docs` writes it from the library's doc comments.
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
