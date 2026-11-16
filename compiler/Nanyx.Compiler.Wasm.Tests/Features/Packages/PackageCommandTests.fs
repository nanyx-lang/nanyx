module TranspilerWasmTests.Features.Packages.Commands

open System.IO
open System.Text.RegularExpressions
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

let private greeterExample = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "..", "examples", "greeter"))

/// The greeter example's manifest and sources, copied into `directory`, so that a command can write beside them.
let private copyGreeter (directory: string) =
    File.Copy(Path.Combine(greeterExample, "greeter.nyxp"), Path.Combine(directory, "greeter.nyxp"))
    Directory.CreateDirectory(Path.Combine(directory, "src")) |> ignore
    for source in Directory.GetFiles(Path.Combine(greeterExample, "src"), "*.nyx") do
        File.Copy(source, Path.Combine(directory, "src", Path.GetFileName source))

let private assertSucceeded (outcome: RunOutcome) =
    Assert.True((outcome.ExitCode = 0), $"Expected exit code 0, got {outcome.ExitCode}. stdout:\n{outcome.StandardOutput}\nstderr:\n{outcome.StandardError}")

[<Fact>]
let ``New - makes a package that runs its greeting and builds cleanly`` () =
    use directory = new TempDirectory()
    let created = nanyxIn directory.Path [ "new"; "acme.demo" ]
    assertSucceeded created
    let package = Path.Combine(directory.Path, "demo")
    Assert.True(File.Exists(Path.Combine(package, "acme.demo.nyxp")))
    Assert.True(File.Exists(Path.Combine(package, "src", "main.nyx")))
    assertRan "Hello, World!\n" (nanyxIn package [ "run" ])
    let built = nanyxIn package [ "build" ]
    assertSucceeded built
    Assert.Contains("No diagnostics.", built.StandardOutput)

[<Fact>]
let ``New - writes a formatted manifest naming the compiler's version for its linter and host`` () =
    use directory = new TempDirectory()
    assertSucceeded (nanyxIn directory.Path [ "new"; "acme.demo" ])
    let manifest = Path.Combine(directory.Path, "demo", "acme.demo.nyxp")
    let version = (nanyxIn directory.Path [ "--version" ]).StandardOutput.Trim()
    Assert.Equal(
        $"""(
  package = (name = "acme.demo", version = "0.1.0", sources = ["src/**/*.nyx"])
  linters = [(package = "nanyx.lint", version = "{version}")]
  host = (package = "nanyx.console", version = "{version}")
)
"""
            .ReplaceLineEndings "\n",
        File.ReadAllText manifest
    )
    let text = File.ReadAllText manifest
    Assert.Equal(Nanyx.Formatter.FormatCommand.Formatted text, Nanyx.Formatter.FormatCommand.formatText Nanyx.Formatter.Formatter.Options.defaults manifest text)

[<Fact>]
let ``New - makes the package in the directory it is given`` () =
    use directory = new TempDirectory()
    assertSucceeded (nanyxIn directory.Path [ "new"; "hello_world"; "here" ])
    Assert.True(File.Exists(Path.Combine(directory.Path, "here", "hello_world.nyxp")))

[<Fact>]
let ``New - refuses a name the manifest would refuse`` () =
    use directory = new TempDirectory()
    let outcome = nanyxIn directory.Path [ "new"; "Demo" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("""The package name "Demo" must be lower case, with words separated by dots: acme.web""", outcome.StandardError)
    Assert.Empty(Directory.GetFileSystemEntries directory.Path)

[<Fact>]
let ``New - refuses a directory that is already a package, or is not empty`` () =
    use directory = new TempDirectory()
    assertSucceeded (nanyxIn directory.Path [ "new"; "demo" ])
    let again = nanyxIn directory.Path [ "new"; "other"; "demo" ]
    Assert.Equal(1, again.ExitCode)
    Assert.Contains("is already a package: it has demo.nyxp", again.StandardError)
    let notEmpty = nanyxIn directory.Path [ "new"; "other"; "demo/src" ]
    Assert.Equal(1, notEmpty.ExitCode)
    Assert.Contains("is not empty", notEmpty.StandardError)

[<Fact>]
let ``Check - a package, however it is named, reports what build does and writes nothing`` () =
    use directory = new TempDirectory()
    copyGreeter directory.Path
    // Everything but the last line, which says what was checked or built.
    let diagnostics (outcome: RunOutcome) =
        let lines = (normalizeOutput outcome.StandardOutput).Split('\n')
        lines[.. lines.Length - 2]
    let checks = [ [ "check" ]; [ "check"; "." ]; [ "check"; "greeter.nyxp" ] ] |> List.map (nanyxIn directory.Path)
    let built = nanyxIn directory.Path [ "build"; "--out"; Path.Combine(directory.Path, "elsewhere", "greeter.wat") ]
    assertSucceeded built
    for check in checks do
        assertSucceeded check
        Assert.Contains("Checked greeter (2 files).", check.StandardOutput)
        Assert.Equal<string array>(diagnostics built, diagnostics check)
    Assert.False(Directory.Exists(Path.Combine(directory.Path, "out")))

[<Fact>]
let ``Build --timings - writes each module's check and the program's emit to stderr, and nothing else changes`` () =
    use directory = new TempDirectory()
    copyGreeter directory.Path
    let plain = nanyxIn directory.Path [ "build" ]
    let timed = nanyxIn directory.Path [ "build"; "--timings" ]
    assertSucceeded timed
    Assert.Equal(normalizeOutput plain.StandardOutput, normalizeOutput timed.StandardOutput)
    Assert.Empty(plain.StandardError)
    let lines = (normalizeOutput timed.StandardError).Split('\n') |> List.ofArray
    // Each of the package's files checked once, by the path the user would write, with its own and total time.
    for source in [ "main.nyx"; "greetings.nyx" ] do
        let checkedLines = lines |> List.filter (fun line -> line.Contains(Path.Combine("src", source) + " ("))
        Assert.Single(checkedLines |> List.filter (fun line -> not (line.EndsWith "(cached)"))) |> ignore
        for line in checkedLines do
            Assert.Matches(@"src.\w+\.nyx \(((\d+\.\d\ds of )?\d+\.\d\ds|cached)\)$", line)
    Assert.True(lines |> List.exists (fun line -> line.StartsWith "Emitted the program in "), timed.StandardError)

[<Fact>]
let ``Check --timings - writes the modules' checks as a tree, with a module checked before shown as cached`` () =
    let outcome, _ =
        nanyxOnPackage
            [ "main.nyx", "module main\n\nimport a\nimport b\n\n@entry\ndef main = {\n  println(\"{one + two}\")\n}\n"
              "a.nyx", "module a\n\nimport c\n\nexport def one = zero + 1\n"
              "b.nyx", "module b\n\nimport c\n\nexport def two = zero + 2\n"
              "c.nyx", "module c\n\nexport def zero = 0\n" ]
            [ "check"; "--timings"; "{package}" ]
    assertSucceeded outcome
    // The package's own modules, without their times or where the package is; the standard library's lines and those
    // of the console host and its interface package are left out, since which of them a check reports depends on what
    // the process checked before. Each module checks last the library's modules that attach definitions to built-in types, `nanyx/text` and `nanyx/array`, as their definitions are in scope.
    let shipped (line: string) = line.Contains(Hosts.consolePackage + "/") || line.Contains(Hosts.consolePackage + ".core/")
    let shape =
        (normalizeOutput outcome.StandardError).Split('\n')
        |> Array.filter (fun line -> line.Contains ".nyx" && not (shipped line))
        |> Array.map (fun line ->
            let line = Regex.Replace(line, @"\S*src[\\/](\w+\.nyx)", "src/$1")
            Regex.Replace(line, @" \((\d+\.\d\ds of )?\d+\.\d\ds\)", ""))
        |> String.concat "\n"
    let expected =
        """src/a.nyx
├── src/c.nyx
src/b.nyx
├── src/c.nyx (cached)
src/c.nyx (cached)
src/main.nyx
├── src/a.nyx (cached)
├── src/b.nyx (cached)"""
    Assert.Equal(expected.ReplaceLineEndings "\n", shape)

[<Fact>]
let ``Check --timings - to a pipe, writes only the finished tree, with no live view`` () =
    let outcome, _ =
        nanyxOnPackage
            [ "main.nyx", "module main\n\nimport util\n\n@entry\ndef main = {\n  println(\"{one}\")\n}\n"
              "util.nyx", "module util\n\nexport def one = 1\n" ]
            [ "check"; "--timings"; "{package}" ]
    assertSucceeded outcome
    // No escape character: a culture-aware substring search would ignore it, so look for the character itself.
    Assert.False(outcome.StandardError.Contains '\u001b', outcome.StandardError)
    // Every line is one of the tree's, as it is written at the end: no stopwatch, and no check's time so far.
    for line in (normalizeOutput outcome.StandardError).TrimEnd('\n').Split('\n') do
        Assert.Matches(@"^[│├└─ ]*\S+ \(((\d+\.\d\ds of )?\d+\.\d\ds|cached)\)$", line)

[<Fact>]
let ``Check - a package with an error fails, even one that a debug build would defer`` () =
    let outcome, wrote = nanyxOnPackage [ "main.nyx", "module main\n\n@entry\ndef main = { missing() }\n" ] [ "check"; "{package}" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.False(wrote)
    Assert.Contains("Unknown function 'missing'", outcome.StandardOutput)

/// What `nanyx <command>` does with the library `acme.util`, which names no host, holding `files` under `src`.
let private onLibrary (files: (string * string) list) (command: string) =
    use directory = new TempDirectory()
    Directory.CreateDirectory(Path.Combine(directory.Path, "src")) |> ignore
    File.WriteAllText(Path.Combine(directory.Path, "util.nyxp"), """(package = (name = "acme.util", version = "0.1.0", sources = ["src/**/*.nyx"]))""")
    for path, text in files do
        File.WriteAllText(Path.Combine(directory.Path, "src", path), text)
    nanyxIn directory.Path [ command ]

[<Fact>]
let ``A library, a package with no host, checks and builds, but running it is an error: it has no entry point`` () =
    let files = [ "strings.nyx", "export module strings\n\nexport def shout = { s: string -> s ++ \"!\" }\n" ]
    assertSucceeded (onLibrary files "check")
    assertSucceeded (onLibrary files "build")
    let run = onLibrary files "run"
    Assert.Equal(1, run.ExitCode)
    Assert.Contains("error: no entry point: acme.util names no host, so it is a library, which can't be run.", run.StandardError)

[<Theory>]
[<InlineData("check")>]
[<InlineData("run")>]
[<InlineData("build")>]
let ``An @entry in a library is an error, from every command`` (command: string) =
    let outcome = onLibrary [ "main.nyx", "module main\n\n@entry\ndef main = { () }\n" ] command
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("NYX0031", outcome.StandardOutput + outcome.StandardError)
