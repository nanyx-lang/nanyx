/// A package with a GitHub reference in its manifest is built from what `nanyx pull` fetched into the cache (#1472,
/// #1742): a build doesn't download, so one not fetched yet is an error at the entry that says to pull, with exit code 1.
module TranspilerWasmTests.Features.Packages.GitHubReferenceRun

open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Running a package with a file on GitHub not pulled yet says to pull it`` () =
    use directory = new TempDirectory()
    let write (path: string) (text: string) =
        let full = Path.Combine(directory.Path, path)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, text.ReplaceLineEndings "\n")
    write
        "app/app.nyxp"
        $"""(
  package = (name = "app", version = "0.1.0")
  dependencies = [(module = "retry", github = "acme/snippets/retry.nyx", rev = "1f8ae8b3c9d04e5f6a7b8c9d0e1f2a3b4c5d6e7f")]
  {consoleHostEntry}
)
"""
    write "app/main.nyx" "module main\n\nimport retry\n\n@entry\ndef main = {\n  println(twice(2))\n}\n"
    let outcome = nanyxIn (Path.Combine(directory.Path, "app")) [ "run" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("NYX0041: The module 'retry' is on GitHub, but it isn't in the package cache: run `nanyx pull` to fetch it", outcome.StandardError)
