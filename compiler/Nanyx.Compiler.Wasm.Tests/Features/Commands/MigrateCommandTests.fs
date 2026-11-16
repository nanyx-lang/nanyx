/// `nanyx migrate`: a script, or a directory of scripts, made a package (#1471).
module TranspilerWasmTests.Features.Commands.Migrate

open System.IO
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

/// A directory called `name` in a directory of its own, holding `files`, each a path and its text.
let private scripts (directory: TempDirectory) (name: string) (files: (string * string) list) =
    let root = Path.Combine(directory.Path, name)
    for path, text in files do
        let full = Path.Combine(root, path)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, text.ReplaceLineEndings "\n")
    root

/// The repository's `examples` directory.
let private examples = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "..", "examples"))

let private read (root: string) (path: string) = File.ReadAllText(Path.Combine(root, path)).ReplaceLineEndings "\n"

[<Fact>]
let ``The directives example migrates to a package that runs, its directives in the manifest`` () =
    use directory = new TempDirectory()
    let root = scripts directory "greetings" [ "directives.nyx", File.ReadAllText(Path.Combine(examples, "directives.nyx")) ]
    let outcome = nanyxIn directory.Path [ "migrate"; "greetings" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal(
        $"""(
  package = (name = "greetings", version = "0.1.0", sources = ["**/*.nyx"])
  linters = [(package = "nanyx.lint", version = "{Toolchain.version}", settings = (rules = (unusedBindings = #error)))]
  host = (package = "nanyx.console", version = "{Toolchain.version}")
  formatter = (lineWidth = 80)
)
""",
        read root "greetings.nyxp")
    let migrated = read root "directives.nyx"
    Assert.StartsWith("module main\n\n-- A script says", migrated)
    Assert.DoesNotContain("::", migrated)
    Assert.DoesNotContain("#!", migrated)
    Assert.EndsWith(
        """
def greet = { name -> println("Hello, " ++ name ++ "!") }

@entry
def main = {
  list.fold(["Ada", "Grace", "Barbara"], (), { _, name -> greet(name) })
}
""",
        migrated)
    let run = nanyxIn root [ "run" ]
    Assert.Equal("Hello, Ada!\nHello, Grace!\nHello, Barbara!\n", run.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal(0, run.ExitCode)

[<Fact>]
let ``Scripts that import each other by path become modules that import each other by name`` () =
    use directory = new TempDirectory()
    let root =
        scripts directory "shouter" [
            "main.nyx", "import \"./words/loud\" as loud\n\nprintln(loud.shout(\"hi\"))\n"
            "words/loud.nyx", "export def shout = { s: string -> s ++ \"!\" }\n" ]
    let outcome = nanyxIn directory.Path [ "migrate"; "shouter" ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal("Migrated 2 files to a package: shouter/shouter.nyxp\n", outcome.StandardOutput.ReplaceLineEndings("\n").Replace('\\', '/'))
    Assert.Equal("module main\n\nimport loud as loud\n\n@entry\ndef main = {\n  println(loud.shout(\"hi\"))\n}\n", read root "main.nyx")
    Assert.Equal("module loud\n\nexport def shout = { (s: string) -> s ++ \"!\" }\n", read root "words/loud.nyx")
    let run = nanyxIn root [ "run" ]
    Assert.Equal("hi!\n", run.StandardOutput.ReplaceLineEndings "\n")

[<Fact>]
let ``A value that needs a context moves into the entry with the statements, in order, and one that doesn't stays`` () =
    use directory = new TempDirectory()
    let root =
        scripts directory "echo" [
            "echo.nyx", "def greeting = \"Hello\"\ndef line = readLine()\nignore(line)\nprintln(greeting)\n" ]
    Assert.Equal(0, (nanyxIn directory.Path [ "migrate"; "echo" ]).ExitCode)
    Assert.Equal(
        "module main\n\ndef greeting = \"Hello\"\n\n@entry\ndef main = {\n  def line = readLine()\n  ignore(line)\n  println(greeting)\n}\n",
        read root "echo.nyx")
    Assert.Equal("Hello\n", (nanyxIn root [ "run" ]).StandardOutput.ReplaceLineEndings "\n")

[<Fact>]
let ``Scripts without statements make a library, which exports their modules and runs under no host`` () =
    use directory = new TempDirectory()
    let root = scripts directory "strings" [ "shout.nyx", "export def shout = { s: string -> s ++ \"!\" }\n" ]
    Assert.Equal(0, (nanyxIn directory.Path [ "migrate"; "strings" ]).ExitCode)
    Assert.Equal("(\n  package = (name = \"strings\", version = \"0.1.0\", sources = [\"**/*.nyx\"])\n)\n", read root "strings.nyxp")
    Assert.Equal("export module shout\n\nexport def shout = { (s: string) -> s ++ \"!\" }\n", read root "shout.nyx")
    Assert.Equal(0, (nanyxIn root [ "check" ]).ExitCode)

[<Fact>]
let ``One script migrates on its own, with a manifest that selects only it`` () =
    use directory = new TempDirectory()
    let root = scripts directory "tools" [ "hello.nyx", "println(\"hello\")\n"; "other.nyx", "println(\"other\")\n" ]
    let outcome = nanyxIn root [ "migrate"; "--name"; "acme.hello"; "hello.nyx" ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.Contains("sources = [\"hello.nyx\"]", read root "acme.hello.nyxp")
    Assert.Equal("println(\"other\")\n", read root "other.nyx")
    Assert.Equal("hello\n", (nanyxIn root [ "run" ]).StandardOutput.ReplaceLineEndings "\n")

[<Fact>]
let ``A directory with several scripts that have statements is refused, naming each, and nothing is written`` () =
    use directory = new TempDirectory()
    let root = scripts directory "tasks" [ "build.nyx", "println(\"build\")\n"; "release.nyx", "println(\"release\")\n" ]
    let outcome = nanyxIn directory.Path [ "migrate"; "tasks" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal(
        "error: These scripts each have top-level statements, but a package runs from one @entry: build.nyx, release.nyx. Migrate them one at a time (nanyx migrate <file>), or make all but one of them modules first\n",
        outcome.StandardError.ReplaceLineEndings "\n")
    Assert.Empty(Directory.GetFiles(root, "*.nyxp"))
    Assert.Equal("println(\"build\")\n", read root "build.nyx")

[<Fact>]
let ``Directives that disagree are an error naming both lines`` () =
    use directory = new TempDirectory()
    scripts directory "tools" [
        "a.nyx", "::formatter.lineWidth = 80\n\nexport def a = 1\n"
        "b.nyx", "::formatter.lineWidth = 100\n\nexport def b = 2\n" ]
    |> ignore
    let outcome = nanyxIn directory.Path [ "migrate"; "tools" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal(
        "error: a.nyx:1: ::formatter.lineWidth = 80 and b.nyx:1: ::formatter.lineWidth = 100 disagree: a package has one manifest, so make them say the same, then migrate again\n",
        outcome.StandardError.ReplaceLineEndings "\n")

[<Fact>]
let ``Dependencies move into the manifest, a path made relative to it, and one named by its version is noted`` () =
    use directory = new TempDirectory()
    let root =
        scripts directory "app" [
            "tools/fetch.nyx", "::dependencies.acme.util = { path = \"../../util\" }\n::dependencies.acme.json = \"1.2.0\"\n\nexport def fetch = 1\n" ]
    let outcome = nanyxIn directory.Path [ "migrate"; "app" ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.Contains("""  dependencies = [(package = "acme.util", path = "../util"), (package = "acme.json", version = "1.2.0")]""", read root "app.nyxp")
    Assert.Contains(
        "note: acme.json is named by its version, but packages can't be downloaded yet, so the package won't build until they can",
        outcome.StandardOutput)

[<Fact>]
let ``A directory that is already a package is refused`` () =
    use directory = new TempDirectory()
    let root = scripts directory "app" [ "app.nyxp", "(package = (name = \"app\", version = \"0.1.0\"))\n"; "main.nyx", "println(1)\n" ]
    let outcome = nanyxIn directory.Path [ "migrate"; "app" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal($"error: {root} is already a package: it has app.nyxp\n", outcome.StandardError.ReplaceLineEndings "\n")

[<Fact>]
let ``A directory whose name is not a package name needs --name`` () =
    use directory = new TempDirectory()
    scripts directory "My Scripts" [ "main.nyx", "println(1)\n" ] |> ignore
    let outcome = nanyxIn directory.Path [ "migrate"; "My Scripts" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("is not a package name, which must be lower case, with words separated by dots: acme.web: give one with --name", outcome.StandardError)
    let named = nanyxIn directory.Path [ "migrate"; "--name"; "acme.scripts"; "My Scripts" ]
    Assert.Equal(0, named.ExitCode)
