/// `nanyx api-check`: whether a new version of a package is a big enough bump from an older one, compared from the
/// `api.json` each `.nyxpkg` holds, as the package index checks a release before recording it (#865).
module TranspilerWasmTests.Features.Commands.ApiCheck

open System.IO
open System.IO.Compression
open Xunit
open TranspilerWasmTestHelpers

/// Packs acme.util at `version`, whose module `strings` is `source`, into `directory`'s `out`, and gives the file.
let private packed (directory: TempDirectory) (version: string) (source: string) =
    directory.Write "util/util.nyxp" $"(package = (name = \"acme.util\", version = \"{version}\"))\n"
    directory.Write "util/strings.nyx" source
    let outcome = nanyxIn directory.Path [ "pack"; "util"; "--out"; "out" ]
    Assert.Equal(0, outcome.ExitCode)
    Path.Combine(directory.Path, "out", $"acme.util-{version}.nyxpkg")

let private shout = "export module strings\n\nexport def shout = { s: string -> s ++ \"!\" }\n"

let private shoutAndWhisper = shout + "\nexport def whisper = { s: string -> s }\n"

[<Fact>]
let ``A big enough bump lists the changes and passes`` () =
    use directory = new TempDirectory()
    let older = packed directory "1.0.0" shout
    let newer = packed directory "1.1.0" shoutAndWhisper
    let outcome = nanyxIn directory.Path [ "api-check"; older; newer ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(
        "minor: strings's def whisper was added\nacme.util 1.1.0 is a big enough bump from 1.0.0\n",
        outcome.StandardOutput.ReplaceLineEndings "\n"
    )
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``A bump too small fails, listing the changes that need a bigger one and what to do`` () =
    use directory = new TempDirectory()
    let older = packed directory "1.0.0" shoutAndWhisper
    let newer = packed directory "1.0.1" shout
    let outcome = nanyxIn directory.Path [ "api-check"; older; newer ]
    Assert.Equal(
        "error: acme.util 1.0.1 is too small a bump from 1.0.0 for what changed, which needs 2.0.0 at least:\n"
        + "error:   strings's def whisper was removed, or is no longer exported\n"
        + "error: Run `nanyx bump` in acme.util to set the version its changes need, then pack and publish that version\n",
        outcome.StandardError.ReplaceLineEndings "\n"
    )
    Assert.Equal("", outcome.StandardOutput)
    Assert.Equal(1, outcome.ExitCode)

[<Fact>]
let ``A package with no api.json, or two different packages, can't be compared`` () =
    use directory = new TempDirectory()
    let older = packed directory "1.0.0" shout
    let bare = Path.Combine(directory.Path, "bare.nyxpkg")
    (
        use zip = ZipFile.Open(bare, ZipArchiveMode.Create)
        zip.CreateEntry "util.nyxp" |> ignore
    )
    let missing = nanyxIn directory.Path [ "api-check"; older; bare ]
    Assert.Equal(
        "error: bare.nyxpkg has no api.json, so its API can't be compared: pack it with `nanyx pack`, which writes one\n",
        missing.StandardError.ReplaceLineEndings "\n"
    )
    Assert.Equal(1, missing.ExitCode)
    directory.Write "other/other.nyxp" "(package = (name = \"acme.other\", version = \"1.1.0\"))\n"
    directory.Write "other/strings.nyx" shout
    Assert.Equal(0, (nanyxIn directory.Path [ "pack"; "other"; "--out"; "out" ]).ExitCode)
    let other = nanyxIn directory.Path [ "api-check"; older; Path.Combine(directory.Path, "out", "acme.other-1.1.0.nyxpkg") ]
    Assert.Equal("error: The older package is acme.util, and the newer acme.other: give two versions of one package\n", other.StandardError.ReplaceLineEndings "\n")
    Assert.Equal(1, other.ExitCode)
