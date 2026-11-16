/// `nanyx pack`: a package packed as a `.nyxpkg`, the file a release of it carries, with its `api.json` (#863, #1470).
module TranspilerWasmTests.Features.Commands.Pack

open System.IO
open System.IO.Compression
open Xunit
open TranspilerWasmTestHelpers

let private write (directory: TempDirectory) (path: string) (text: string) =
    let full = Path.Combine(directory.Path, path)
    Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
    File.WriteAllText(full, text)

[<Fact>]
let ``A package is packed beside its manifest, with its SHA-256`` () =
    use directory = new TempDirectory()
    write directory "util/util.nyxp" "(package = (name = \"acme.util\", version = \"0.3.0\"))\n"
    write directory "util/src/strings.nyx" "export module strings\n\nexport def shout = { s: string -> s ++ \"!\" }\n"
    let outcome = nanyxIn (Path.Combine(directory.Path, "util")) [ "pack" ]
    Assert.Equal("", outcome.StandardError)
    let lines = outcome.StandardOutput.ReplaceLineEndings("\n").TrimEnd().Split('\n')
    Assert.Equal("Packed acme.util 0.3.0: acme.util-0.3.0.nyxpkg", lines[0])
    let packed = Path.Combine(directory.Path, "util", "acme.util-0.3.0.nyxpkg")
    Assert.Equal($"sha256 {NyxCompiler.PackageArchive.sha256 (File.ReadAllBytes packed)}", lines[1])
    use zip = ZipFile.OpenRead packed
    Assert.Equal<string list>([ "api.json"; "src/strings.nyx"; "util.nyxp" ], zip.Entries |> Seq.map _.FullName |> List.ofSeq)
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``A package with a path dependency can't be packed`` () =
    use directory = new TempDirectory()
    write directory "util/util.nyxp" "(package = (name = \"acme.util\", version = \"0.3.0\"))\n"
    write directory "util/strings.nyx" "export module strings\n\nexport def shout = { s: string -> s ++ \"!\" }\n"
    write directory "web/web.nyxp" "(package = (name = \"acme.web\", version = \"1.0.0\"), dependencies = [(package = \"acme.util\", path = \"../util\")])\n"
    write directory "web/loud.nyx" "export module loud\n\nimport acme.util/strings\n\nexport def loud = { s: string -> shout(s) }\n"
    let outcome = nanyxIn (Path.Combine(directory.Path, "web")) [ "pack" ]
    Assert.Contains("error NYX0023: the dependency acme.util is found by its path, so acme.web can't be packed", outcome.StandardError)
    Assert.False(File.Exists(Path.Combine(directory.Path, "web", "acme.web-1.0.0.nyxpkg")))
    Assert.Equal(1, outcome.ExitCode)