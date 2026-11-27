/// The standard library's pages on the docs site (docs/src/content/stdlib) list what the library, and each package the
/// compiler ships but the hosts, exports, as `nanyx stdlib-docs` writes it from their doc comments.
module TranspilerWasmTests.Features.Docs.StandardLibraryReference

open System.IO
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

[<Fact>]
let ``Every standard library page's reference is the library's`` () =
    for path, update in StandardLibraryDocs.update (testStore ()) (Path.Combine(docsContentDirectory, "stdlib")) do
        match update with
        | StandardLibraryDocs.UpToDate -> ()
        | StandardLibraryDocs.OutOfDate written ->
            // Run `nanyx stdlib-docs ../docs/src/content/stdlib` from compiler/ to write it.
            Assert.Equal(written, File.ReadAllText(path).ReplaceLineEndings "\n")
        | StandardLibraryDocs.Unwritable message -> Assert.Fail $"{Path.GetFileName path} {message}"

[<Fact>]
let ``The list of nominal types beside the pages has the library's protected and private types by their own names`` () =
    let directory = Path.Combine(docsContentDirectory, "stdlib")
    let written =
        StandardLibraryDocs.update (testStore ()) directory
        |> List.pick (fun (path, update) ->
            match update with
            | StandardLibraryDocs.OutOfDate text when Path.GetFileName path = StandardLibraryDocs.nominalTypesFile -> Some text
            | StandardLibraryDocs.UpToDate when Path.GetFileName path = StandardLibraryDocs.nominalTypesFile -> Some (File.ReadAllText path)
            | _ -> None)
    let listed (kind: string) =
        let start = written.IndexOf $"\"{kind}\": ["
        written.Substring(start, written.IndexOf(']', start) - start)
    Assert.Contains("\"Memory\"", listed "protected")
    Assert.Contains("\"MRef\"", listed "private")
    Assert.DoesNotContain("\"MRef\"", listed "protected")

[<Fact>]
let ``A public module of a package the compiler ships has a page, but a host's modules don't`` () =
    let pages = StandardLibraryDocs.references ()
    Assert.Contains("console", pages.Keys)
    Assert.Contains("env", pages.Keys)
    Assert.Contains("## `Console`", pages["console"])
    Assert.DoesNotContain("console-host", pages.Keys)

/// What `StandardLibraryDocs.update` says of the page `page.md`, whose text is `text`, alone in a directory, with the
/// tests' package store, which holds nanyx.json 0.1.0, 0.2.0, 0.4.2, 0.5.0 and 0.5.1 and no other version.
let private updateOf (text: string) =
    use directory = new TempDirectory()
    directory.Write "page.md" text
    StandardLibraryDocs.update (testStore ()) directory.Path
    |> List.filter (fun (path, _) -> Path.GetFileName path = "page.md")
    |> List.map snd

[<Fact>]
let ``A page that pins a version the index doesn't have, or a pin written wrong, can't be written`` () =
    let page (pin: string) = $"---\ntitle: page\npackage: {pin}\n---\n\n# Reference\n"
    match updateOf (page "nanyx.json 9.9.9") with
    | [ StandardLibraryDocs.Unwritable message ] -> Assert.StartsWith("pins nanyx.json 9.9.9, which can't be read: ", message)
    | other -> Assert.Fail $"%A{other}"
    match updateOf (page "nanyx.json") with
    | [ StandardLibraryDocs.Unwritable message ] ->
        Assert.Equal("pins its package as 'nanyx.json', but a pin is a package and its exact version, `package: nanyx.json 0.1.0`", message)
    | other -> Assert.Fail $"%A{other}"

[<Fact>]
let ``A page that pins a package that doesn't compile can't be written, and says why`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    directory.Write "broken/broken.nyxp" "(package = (name = \"acme.broken\", version = \"1.0.0\"))\n"
    directory.Write "broken/broken.nyx" "export module broken\n\nexport def answer: int = 42\n"
    publishToIndex store (Path.Combine(directory.Path, "broken")) [] "" |> ignore
    directory.Write "pages/broken.md" "---\ntitle: broken\npackage: acme.broken 1.0.0\n---\n\n# Reference\n"
    let pages = Path.Combine(directory.Path, "pages")
    // Packing checks a package, so it is published whole and then broken where it is unpacked, as a package that a
    // later compiler rejects is.
    StandardLibraryDocs.update store pages |> ignore
    let unpacked = PackageStore.directoryOf store "acme.broken" (SemanticVersion.tryParse "1.0.0").Value
    File.WriteAllText(Path.Combine(unpacked, "broken.nyx"), "export module broken\n\nexport def answer: int = \"forty-two\"\n")
    match StandardLibraryDocs.update store pages |> List.filter (fun (path, _) -> Path.GetFileName path = "broken.md") with
    | [ _, StandardLibraryDocs.Unwritable message ] ->
        Assert.StartsWith("pins acme.broken 1.0.0, which can't be read: broken.nyx:3:", message)
        Assert.Contains("error NYX0", message)
    | other -> Assert.Fail $"%A{other}"

[<Fact>]
let ``A page of a published package's file has its reference from the version the page pins`` () =
    let page = File.ReadAllText(Path.Combine(docsContentDirectory, "stdlib", "encode.md"))
    Assert.Equal(Result.Ok(Some("nanyx.json", (SemanticVersion.tryParse "0.5.1").Value)), StandardLibraryDocs.pinOf page)
    Assert.Contains(StandardLibraryDocs.beginMarkerOf (Some("nanyx.json", (SemanticVersion.tryParse "0.5.1").Value)), page)
    Assert.Contains("## `string`", page)
