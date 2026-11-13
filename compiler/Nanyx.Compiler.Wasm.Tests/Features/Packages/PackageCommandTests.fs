module TranspilerWasmTests.Features.Packages.Commands

open System.IO
open System.Text.RegularExpressions
open Xunit
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
let ``New - makes the package in the directory it is given`` () =
    use directory = new TempDirectory()
    assertSucceeded (nanyxIn directory.Path [ "new"; "hello_world"; "here" ])
    Assert.True(File.Exists(Path.Combine(directory.Path, "here", "hello_world.nyxp")))

[<Fact>]
let ``New - refuses a name the manifest would refuse`` () =
    use directory = new TempDirectory()
    let outcome = nanyxIn directory.Path [ "new"; "Demo" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("The package name \"Demo\" must be lower case, with words separated by dots: acme.web", outcome.StandardError)
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
            Assert.Matches(@"src.\w+\.nyx \((\d+\.\d\ds of \d+\.\d\ds|cached)\)$", line)
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
    // The package's own modules, without their times or where the package is; the standard library's lines are left
    // out, since which of them a check reports depends on what the process checked before.
    let shape =
        (normalizeOutput outcome.StandardError).Split('\n')
        |> Array.filter (fun line -> line.Contains ".nyx")
        |> Array.map (fun line ->
            let line = Regex.Replace(line, @"\S*src[\\/](\w+\.nyx)", "src/$1")
            Regex.Replace(line, @" \(\d+\.\d\ds of \d+\.\d\ds\)", ""))
        |> String.concat "\n"
    let expected =
        """src/a.nyx
└── src/c.nyx
src/b.nyx
└── src/c.nyx (cached)
src/c.nyx (cached)
src/main.nyx
├── src/a.nyx (cached)
└── src/b.nyx (cached)"""
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
        Assert.Matches(@"^[│├└─ ]*\S+ \((\d+\.\d\ds of \d+\.\d\ds|cached)\)$", line)

[<Fact>]
let ``Check - a package with an error fails, even one that a debug build would defer`` () =
    let outcome, wrote = nanyxOnPackage [ "main.nyx", "module main\n\n@entry\ndef main = { missing() }\n" ] [ "check"; "{package}" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.False(wrote)
    Assert.Contains("Unknown function 'missing'", outcome.StandardOutput)
