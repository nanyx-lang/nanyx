/// Reading the package index at an address, as nanyx-lang's is: a package it doesn't list has no index file there
/// (404), so it has no versions, as one missing from an index on disk has, and `nanyx publish` can publish its first
/// version.
module TranspilerWasmTests.Features.Packages.PackageIndexAddress

open System
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

/// A server for an index whose files are `files`, each a path and its text, and that answers anything else 404, or
/// `failing` for the path it names.
let private indexServer (files: (string * string) list) (failing: string option) =
    new TestServer(fun _ path _ _ ->
        match files |> List.tryFind (fst >> (=) path), failing with
        | Some(_, text), _ -> 200, [], text
        | None, Some failed when failed = path -> 500, [], "broken"
        | None, _ -> 404, [], "")

let private config = "/config.json", """{ "format": 1, "index": "index/" }"""

[<Fact>]
let ``A package an index at an address doesn't list has no versions`` () =
    use server = indexServer [ config ] None
    match PackageIndex.versions server.Url "acme.core" with
    | Ok(file, versions) ->
        Assert.Equal($"{server.Url}/index/acme/acme.core.json", file)
        Assert.Empty versions
    | Error message -> failwith message

[<Fact>]
let ``The versions an index at an address lists are read, and one it can't serve is an error`` () =
    let listing =
        "/index/acme/acme.core.json",
        """{ "name": "acme.core", "versions": [{ "version": "1.0.0", "url": "../../artifacts/acme.core-1.0.0.nyxpkg", "sha256": "00", "dependencies": {}, "unlisted": false, "available": true }] }"""
    use server = indexServer [ config; listing ] (Some "/index/acme/acme.web.json")
    match PackageIndex.versions server.Url "acme.core" with
    | Ok(_, versions) -> Assert.Equal<string list>([ "1.0.0" ], versions |> List.map (fun version -> string version.Version))
    | Error message -> failwith message
    match PackageIndex.versions server.Url "acme.web" with
    | Error message -> Assert.Contains("answered 500", message)
    | Ok _ -> failwith "Expected an error"
