/// A package chooses its host with the `host` field of its manifest, or `--host` on `run`, `build` and `check` (#1247,
/// see docs/src/content/compiler-internals/hosts.md).
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
let ``A host the compiler doesn't ship, with no path, is an error saying how to find it`` () =
    let outcome = onPackage $"""host = (package = "nanyx.nope", version = "{Toolchain.version}")""" main [ "check" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains(
        """app.nyxp:3:21: error NYX0023: The compiler ships no package 'nanyx.nope': give the path of a host the compiler doesn't ship, as `host = (package = "nanyx.nope", path = "../nanyx.nope")`""",
        outcome.StandardError)

[<Fact>]
let ``A host is found by its path`` () =
    let path = (ShippedPackages.directoryOf Hosts.consolePackage).Replace("\\", "/")
    let outcome = onPackage $"""host = (package = "nanyx.console", path = "{path}")""" main [ "run" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal("hello\n", outcome.StandardOutput)
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``A package whose manifest doesn't declare it a host is not one`` () =
    use other = new TempDirectory()
    File.WriteAllText(Path.Combine(other.Path, "acme.util.nyxp"), "(package = (name = \"acme.util\", version = \"0.1.0\"))\n")
    let path = other.Path.Replace("\\", "/")
    let outcome = onPackage $"""host = (package = "acme.util", path = "{path}")""" main [ "check" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("'acme.util' is not a host: a host's manifest declares that it is one", outcome.StandardError)

[<Fact>]
let ``A setting for a host that takes none is an error at the setting`` () =
    let outcome = onPackage $"""host = (package = "nanyx.console", version = "{Toolchain.version}", settings = (port = 3000))""" main [ "build" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("app.nyxp:3:70: error NYX0021: The host 'nanyx.console' takes no settings", outcome.StandardError)

[<Fact>]
let ``A host without a package is an error`` () =
    let outcome = onPackage $"""host = (version = "{Toolchain.version}")""" main [ "run" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("error NYX0022: The host has no package, which it needs", outcome.StandardError)
[<Theory>]
[<InlineData("run")>]
[<InlineData("build")>]
[<InlineData("check")>]
let ``--host chooses the package's host in place of its manifest's`` (command: string) =
    let outcome = onPackage "" main [ command; "--host"; Hosts.consolePackage ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``--host naming a package the compiler doesn't ship is a usage error naming it`` () =
    let outcome = onPackage "" main [ "run"; "--host"; "nanyx.nope" ]
    Assert.Equal(2, outcome.ExitCode)
    Assert.StartsWith("--host: ", outcome.StandardError.Trim())
    Assert.Contains("The compiler ships no package 'nanyx.nope'", outcome.StandardError)

[<Fact>]
let ``--host takes the directory of a host package`` () =
    let outcome = onPackage "" main [ "run"; "--host"; ShippedPackages.directoryOf Hosts.consolePackage ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal("hello\n", outcome.StandardOutput)
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``--host on a script is a usage error: a script names its host with ::host`` () =
    use directory = new TempDirectory()
    let path = Path.Combine(directory.Path, "script.nyx")
    File.WriteAllText(path, "println(\"hi\")\n")
    let outcome = nanyxIn directory.Path [ "run"; "--host"; Hosts.consolePackage; path ]
    Assert.Equal(2, outcome.ExitCode)
    Assert.Equal(
        """`--host` chooses a package's host, and a script names its own with a `::host` directive, as `::host acme.server ../server`, or runs under 'nanyx.console' without one: leave out `--host`""",
        outcome.StandardError.Trim())

[<Fact>]
let ``A script runs under the host its ::host finds by path`` () =
    use directory = new TempDirectory()
    let path = Path.Combine(directory.Path, "script.nyx")
    let host = (ShippedPackages.directoryOf Hosts.consolePackage).Replace("\\", "/")
    File.WriteAllText(path, $"::host nanyx.console {host}\n\nprintln(\"hi\")\n")
    let outcome = nanyxIn directory.Path [ "run"; path ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal("hi\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``A script runs with the packages its ::dependency lines name`` () =
    use directory = new TempDirectory()
    Directory.CreateDirectory(Path.Combine(directory.Path, "util")) |> ignore
    File.WriteAllText(
        Path.Combine(directory.Path, "util", "util.nyxp"),
        """(package = (name = "acme.util", version = "0.1.0"))""")
    File.WriteAllText(Path.Combine(directory.Path, "util", "strings.nyx"), "export module strings\n\nexport def shout = { s: string -> s ++ \"!\" }\n")
    let path = Path.Combine(directory.Path, "script.nyx")
    File.WriteAllText(path, "::dependency acme.util ./util\n\nimport acme.util/strings\n\nprintln(shout(\"hi\"))\n")
    let outcome = nanyxIn directory.Path [ "run"; path ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal("hi!\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal(0, outcome.ExitCode)

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
    Assert.StartsWith("It doesn't fit the host 'nanyx.console/console.host'", mismatch.Message)
