module TranspilerWasmTests.Features.Commands.RunBox

open System
open System.IO
open Xunit
open TranspilerWasmTestHelpers

/// A box on a writer standing in for the terminal, 30 columns wide, and that writer, which ends lines with "\n" so a
/// carriage return written to the terminal shows as itself.
let private boxOn (colour: bool) =
    let screen = new StringWriter(NewLine = "\n")
    new RunBox(screen, "nanyx run emails", (fun () -> 30), colour), screen

[<Fact>]
let ``Run box - the rules are one column short of the terminal's width, titled`` () =
    Assert.Equal("╭─ nanyx run emails " + String('─', 9), RunBox.Top("nanyx run emails", 30, false))
    Assert.Equal("╰─ output " + String('─', 19), RunBox.Bottom("output", 30, false))
    Assert.Equal("╰" + String('─', 28), RunBox.Bottom(null, 30, false))
    // A title wider than the terminal is written whole, with no rule after it.
    Assert.Equal("╭─ nanyx run emails ", RunBox.Top("nanyx run emails", 10, false))

[<Fact>]
let ``Run box - where the console's code page has no rounded corners, the corners are square`` () =
    let codePage437 = Text.CodePagesEncodingProvider.Instance.GetEncoding 437
    Assert.False(RunBox.CanWrite(codePage437, "╭╰"))
    Assert.True(RunBox.CanWrite(codePage437, "┌└─│"))
    Assert.True(RunBox.CanWrite(Text.UTF8Encoding(false), "╭╰"))
    Assert.Equal("┌─ run " + String('─', 3), RunBox.Top("run", 11, false, false))
    Assert.Equal("└─ output " + String('─', 2), RunBox.Bottom("output", 13, false, false))

[<Fact>]
let ``Run box - in colour, the rules and the compiler's lines are dimmed, and a diagnostic only in its gutter`` () =
    Assert.Equal("\u001b[2m╰─ output ──\u001b[0m", RunBox.Bottom("output", 13, true))
    Assert.Equal("\u001b[2m│ Emitted the program\u001b[0m", RunBox.Line("Emitted the program", true, true))
    Assert.Equal("\u001b[2m│ \u001b[0mmain.nyx:1:1: error", RunBox.Line("main.nyx:1:1: error", false, true))
    Assert.Equal("│ main.nyx:1:1: error", RunBox.Line("main.nyx:1:1: error", false, false))

[<Fact>]
let ``Run box - with nothing but the stopwatch's line, closing it ends that line and draws no box`` () =
    let box, screen = boxOn false
    screen.Write "\rCompiled emails (3 files) in 1.9s"
    box.Pending "Compiled emails (3 files) in 1.9s"
    box.Close "output"
    screen.Write "23\n"
    Assert.Equal("\rCompiled emails (3 files) in 1.9s\n23\n", screen.ToString())

[<Fact>]
let ``Run box - a line after the stopwatch's opens the box over it, and takes it in`` () =
    let box, screen = boxOn false
    screen.Write "\rCompiled emails in 1.9s"
    box.Pending "Compiled emails in 1.9s"
    box.Diagnostics.WriteLine "main.nyx:2:3: error NYX0102: Unknown function 'missing'"
    box.Diagnostics.WriteLine "  |     ^^^^^^^"
    box.Lines.WriteLine "Running with 1 deferred error"
    box.Close "output"
    // What the run reports after the program's output is its own, unmarked.
    box.Lines.Write "Runtime error: unreachable\n"
    // The top rule is written from the start of the stopwatch's line, over it.
    let expected =
        """╭─ nanyx run emails ─────────
│ Compiled emails in 1.9s
│ main.nyx:2:3: error NYX0102: Unknown function 'missing'
│   |     ^^^^^^^
│ Running with 1 deferred error
╰─ output ───────────────────
Runtime error: unreachable
"""
    Assert.Equal("\rCompiled emails in 1.9s\r" + expected.ReplaceLineEndings "\n", screen.ToString())

[<Fact>]
let ``Run box - opened from the start, and not closed for the program, it ends with a plain rule`` () =
    let box, screen = boxOn false
    box.Open()
    box.Lines.Write "Emitted"
    box.Lines.Write " the program\r\n"
    box.Dispose()
    let expected =
        """╭─ nanyx run emails ─────────
│ Emitted the program
╰────────────────────────────
"""
    Assert.Equal(expected.ReplaceLineEndings "\n", screen.ToString())

[<Fact>]
let ``Run - to a pipe, with timings and deferred errors, writes no box and no escapes`` () =
    let main = "module main\n\n@entry\ndef main = {\n  println(\"before\")\n  missing(1)\n}\n"
    let outcome, _ = nanyxOnPackage [ "main.nyx", main ] [ "run"; "--timings"; "{package}" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("before\n", outcome.StandardOutput)
    let errors = normalizeOutput outcome.StandardError
    // No escape character: a culture-aware substring search would ignore it, so look for the character itself.
    Assert.False(errors.Contains '\u001b', errors)
    Assert.False(errors.Contains '╭' || errors.Contains '╰', errors)
    let lines = errors.Split '\n'
    // The tree comes first, then the diagnostic, then what the run says, as without the box.
    let at (text: string) = Array.FindIndex(lines, fun line -> line.Contains text)
    Assert.Matches(@"main\.nyx \(", lines[0])
    Assert.True(
        0 < at "error NYX0102" && at "error NYX0102" < at "Running with 1 deferred error"
        && at "Running with 1 deferred error" < at "Emitted the program in ",
        errors)
