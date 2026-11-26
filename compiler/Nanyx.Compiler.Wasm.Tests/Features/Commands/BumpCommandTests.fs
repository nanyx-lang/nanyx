/// `nanyx bump`: a package's version set to the one the changes to its public API need, compared with the newest version
/// of it published (#1470).
module TranspilerWasmTests.Features.Commands.Bump

open System.IO
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

/// A store whose index, in `directory`, lists the package in `util` as it is now, packed by `nanyx pack`.
let private published (directory: TempDirectory) =
    let index = Path.Combine(directory.Path, "index")
    let store = { Cache = Path.Combine(directory.Path, "cache"); Index = index; GitHub = GitHub.unsupported; Seed = SeededPackages.seed }
    let packed = nanyxInStore store directory.Path [ "pack"; "util"; "--out"; Path.Combine(index, "artifacts") ]
    Assert.Equal(0, packed.ExitCode)
    let hash = packed.StandardOutput.ReplaceLineEndings("\n").Split('\n') |> Array.find (fun line -> line.StartsWith "sha256 ") |> fun line -> line.Substring 7
    directory.Write "index/config.json" """{ "format": 1, "index": "index/" }"""
    directory.Write
        "index/index/acme/acme.util.json"
        $$"""{ "name": "acme.util", "versions": [{ "version": "1.0.0", "url": "../../artifacts/acme.util-1.0.0.nyxpkg", "sha256": "{{hash}}", "unlisted": false, "available": true }] }"""
    store

[<Fact>]
let ``A removed export bumps the major version, and the change is listed`` () =
    use directory = new TempDirectory()
    directory.Write "util/util.nyxp" "(package = (name = \"acme.util\", version = \"1.0.0\"))\n"
    directory.Write "util/strings.nyx" "export module strings\n\nexport def shout = { s: string -> s ++ \"!\" }\n\nexport def whisper = { s: string -> s }\n"
    let store = published directory
    directory.Write "util/strings.nyx" "export module strings\n\nexport def shout = { s: string -> s ++ \"!\" }\n"
    let outcome = nanyxInStore store (Path.Combine(directory.Path, "util")) [ "bump" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(
        "major: strings's def whisper was removed, or is no longer exported\nacme.util 1.0.0 -> 2.0.0, after 1.0.0: written to util.nyxp\n",
        outcome.StandardOutput.ReplaceLineEndings "\n"
    )
    Assert.Equal("(package = (name = \"acme.util\", version = \"2.0.0\"))\n", File.ReadAllText(Path.Combine(directory.Path, "util", "util.nyxp")))
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``With no version published, the version stays`` () =
    use directory = new TempDirectory()
    directory.Write "util/util.nyxp" "(package = (name = \"acme.util\", version = \"0.1.0\"))\n"
    directory.Write "util/strings.nyx" "export module strings\n\nexport def shout = { s: string -> s ++ \"!\" }\n"
    directory.Write "index/config.json" """{ "format": 1, "index": "index/" }"""
    let store = { Cache = Path.Combine(directory.Path, "cache"); Index = Path.Combine(directory.Path, "index"); GitHub = GitHub.unsupported; Seed = SeededPackages.seed }
    let outcome = nanyxInStore store (Path.Combine(directory.Path, "util")) [ "bump" ]
    Assert.Equal("acme.util has no version published before 0.1.0, so there is nothing to compare: its version stays 0.1.0\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal(0, outcome.ExitCode)