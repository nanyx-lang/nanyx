/// A package chooses its host with the `host` field of its manifest, or `--host` on `run`, `build` and `check` (#1247,
/// see docs/src/content/work-in-progress/hosts.md).
module TranspilerWasmTests.Features.Hosts.ChooseHost

open System.IO
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

let private main =
    """module main

@entry
def main = {
  println("hello")
}
"""

/// What `nanyx` printed for `arguments` (`{package}` standing for the package's directory) on a package whose manifest
/// has the field `host` after its `package`, and whose one source file is `source`.
let private onPackage (host: string) (source: string) (arguments: string list) =
    use directory = new TempDirectory()
    let root = directory.Path
    Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
    let manifest = $"(\n  package = (name = \"app\", version = \"0.1.0\", sources = [\"src/**/*.nyx\"])\n  {host}\n)\n"
    File.WriteAllText(Path.Combine(root, "app.nyxp"), manifest)
    File.WriteAllText(Path.Combine(root, "src", "main.nyx"), source)
    let outcome = nanyxIn root (arguments |> List.map (fun argument -> argument.Replace("{package}", root)))
    { outcome with
        StandardOutput = outcome.StandardOutput.ReplaceLineEndings "\n"
        StandardError = outcome.StandardError.Replace(root + string Path.DirectorySeparatorChar, "").ReplaceLineEndings "\n" }

[<Fact>]
let ``A package whose host is the console host runs under it`` () =
    let outcome = onPackage $"""host = (package = "nanyx.console", version = "{Toolchain.version}")""" main [ "run" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal("hello\n", outcome.StandardOutput)
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``A host that isn't one is an error naming it`` () =
    let outcome = onPackage $"""host = (package = "nanyx.nope", version = "{Toolchain.version}")""" main [ "check" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("app.nyxp:3:21: error NYX0023: there is no host 'nanyx.nope'; the hosts are 'nanyx.console'", outcome.StandardError)

[<Fact>]
let ``A host's settings are an error at the field, until hosts take settings`` () =
    let outcome = onPackage $"""host = (package = "nanyx.console", version = "{Toolchain.version}", settings = (port = 3000))""" main [ "build" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("app.nyxp:3:58: error NYX0024: nanyx.console takes no settings", outcome.StandardError)

[<Fact>]
let ``A host without a package is an error`` () =
    let outcome = onPackage $"""host = (version = "{Toolchain.version}")""" main [ "run" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("error NYX0022: the host has no package, which it needs", outcome.StandardError)
[<Theory>]
[<InlineData("run")>]
[<InlineData("build")>]
[<InlineData("check")>]
let ``--host chooses the package's host in place of its manifest's`` (command: string) =
    let outcome = onPackage "" main [ command; "--host"; "nanyx.console/console.host" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``--host naming a module that isn't a host is a usage error naming it`` () =
    let outcome = onPackage "" main [ "run"; "--host"; "nanyx/console" ]
    Assert.Equal(2, outcome.ExitCode)
    Assert.Equal(
        "--host: 'nanyx/console' is not a host: until any package can be one, a host is one the compiler ships, 'nanyx.console/console.host'",
        outcome.StandardError.Trim())

[<Fact>]
let ``--host on a script is a usage error: a script runs under the console host`` () =
    use directory = new TempDirectory()
    let path = Path.Combine(directory.Path, "script.nyx")
    File.WriteAllText(path, "import nanyx/console\n\nprintln(\"hi\")\n")
    let outcome = nanyxIn directory.Path [ "run"; "--host"; "nanyx.console/console.host"; path ]
    Assert.Equal(2, outcome.ExitCode)
    Assert.Equal("--host chooses a package's host: a script always runs under nanyx.console/console.host.", outcome.StandardError.Trim())

[<Fact>]
let ``An entry that doesn't fit the package's host names the host, in the editor as on the command line`` () =
    use directory = new TempDirectory()
    let root = directory.Path
    Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
    let manifest =
        $"""(
  package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"])
  host = (package = "nanyx.console", version = "{Toolchain.version}")
)
"""
    File.WriteAllText(Path.Combine(root, "app.nyxp"), manifest)
    let path = Path.Combine(root, "src", "main.nyx")
    let source =
        """module main

@entry
def main = { x -> x + 1 }
"""
    File.WriteAllText(path, source)
    // The editor compiles a package's file as `Package.compileSourceWith` does.
    let result = Package.compileSourceWith Linters.none path source
    let mismatch = result.Diagnostics |> List.find (fun diagnostic -> diagnostic.Code = DiagnosticCodes.EntryDoesNotFitHost.Code)
    Assert.StartsWith("The entry point 'main' doesn't fit the host 'nanyx.console/console.host'", mismatch.Message)
