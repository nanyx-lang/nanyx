/// `nanyx manifest --json` prints a package's manifest as JSON, for tools outside the compiler (#1404).
module TranspilerWasmTests.Features.Packages.ManifestCommand

open System.IO
open Xunit
open TranspilerWasmTestHelpers

/// What `nanyx` printed for `arguments` in a directory holding the manifest `demo.nyxp` with the text `manifest`.
let private withManifest (manifest: string) (arguments: string list) =
    use directory = new TempDirectory()
    File.WriteAllText(Path.Combine(directory.Path, "demo.nyxp"), manifest)
    let outcome = nanyxIn directory.Path arguments
    { outcome with
        StandardOutput = outcome.StandardOutput.ReplaceLineEndings "\n"
        StandardError = outcome.StandardError.Replace(directory.Path + string Path.DirectorySeparatorChar, "").ReplaceLineEndings "\n" }

[<Fact>]
let ``The manifest is printed as JSON, a tag as a string`` () =
    let outcome =
        withManifest
            """(
  package = (name = "demo", version = "0.1.0", sources = ["src/**/*.nyx"])
  linters = [(package = "nanyx.lint", version = "0.28.0", settings = (rules = (unusedBindings = #error)))]
  formatter = (lineWidth = 100)
)
"""
            [ "manifest"; "--json" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal(
        """{
  "package": {
    "name": "demo",
    "version": "0.1.0",
    "sources": [
      "src/**/*.nyx"
    ]
  },
  "linters": [
    {
      "package": "nanyx.lint",
      "version": "0.28.0",
      "settings": {
        "rules": {
          "unusedBindings": "#error"
        }
      }
    }
  ],
  "formatter": {
    "lineWidth": 100
  }
}
"""
            .ReplaceLineEndings "\n",
        outcome.StandardOutput
    )

[<Fact>]
let ``A manifest with errors prints them and no JSON`` () =
    let outcome = withManifest """(package = (name = "demo"))""" [ "manifest"; "--json"; "demo.nyxp" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("", outcome.StandardOutput)
    Assert.Contains("error NYX0022: package has no version, which it needs", outcome.StandardError)

[<Fact>]
let ``Without --json it is a usage error`` () =
    let outcome = withManifest """(package = (name = "demo", version = "0.1.0"))""" [ "manifest" ]
    Assert.Equal(2, outcome.ExitCode)
    Assert.Contains("--json", outcome.StandardError)
