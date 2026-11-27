module TranspilerWasmTests.Features.Commands.DiagnosticExcerpt

open Xunit
open TranspilerWasmTestHelpers

// The CLI shows each diagnostic as Rust lays out its errors: the code and headline, where it is, the line of source with
// the span underlined (#880), then the body (#2131).

/// The lines `nanyx check` printed for `source` from its first diagnostic on, with the file's directory left out.
let private diagnosticLines (source: string) =
    let output = (checkScript (source.ReplaceLineEndings "\n")).StandardOutput.ReplaceLineEndings "\n"
    let lines = output.Split '\n' |> List.ofArray
    lines
    |> List.skipWhile (fun line -> not (line.StartsWith "error "))
    |> List.takeWhile (fun line -> not (line.StartsWith "Types:"))
    |> List.map (fun line ->
        match line.IndexOf "--> ", line.IndexOf "main.nyx:" with
        | arrow, file when arrow >= 0 && file > arrow -> line.Substring(0, arrow + 4) + line.Substring file
        | _ -> line)
    |> String.concat "\n"

[<Fact>]
let ``Check - an error shows its headline, its line with the span underlined, then its body`` () =
    let source =
        """def count = 3

def total = count + "one"
"""
    Assert.StartsWith(
        """error NYX0201: Type mismatch
  --> main.nyx:3:13
   |
 3 | def total = count + "one"
   |             ^^^^^^^^^^^^^
   = 'string' is not a number, so it cannot be used with +: use `++` to join them
""".ReplaceLineEndings "\n",
        diagnosticLines source)

[<Fact>]
let ``Check - a missing context's headline names the context`` () =
    let source =
        """context Log = (log: string -> ())
def g: [Log] () -> () = { log("hi") }
def h = { g() }
"""
    Assert.Equal(
        """error NYX0205: Missing context 'Log'
  --> main.nyx:3:11
   |
 3 | def h = { g() }
   |           ^
   = 'g' requires the context 'Log', which is not available here
""".ReplaceLineEndings "\n",
        diagnosticLines source)

[<Fact>]
let ``Check - diagnostics are separated by a blank line`` () =
    let source = "def a = missing\ndef b = alsoMissing\n"
    let lines = (diagnosticLines source).Split '\n'
    Assert.Equal("   = 'missing' is not in scope", lines[5])
    Assert.Equal("", lines[6])
    Assert.Equal("error NYX0101: Unknown identifier 'alsoMissing'", lines[7])

[<Fact>]
let ``Check - the underline keeps the tabs the line indents with`` () =
    let source = "def total = 1\n\n@entry\ndef main = {\n\tprintln(totl)\n}\n"
    Assert.StartsWith(
        "error NYX0101: Unknown identifier 'totl'\n  --> main.nyx:5:10\n   |\n 5 | \tprintln(totl)\n   | \t        ^^^^\n   = 'totl' is not in scope: did you mean 'total'?\n",
        diagnosticLines source)

[<Fact>]
let ``Check - a span over several lines is underlined to the end of its first`` () =
    let source =
        """def count: string = (
  1,
  2)
"""
    let lines = (diagnosticLines source).Split '\n'
    Assert.Equal(" 1 | def count: string = (", lines[3])
    Assert.Equal("   |                     ^", lines[4])

[<Fact>]
let ``Check - a related location is shown under its note with its own excerpt`` () =
    // The place that explains the error, the first definition of a name defined twice, is shown as the error's own is
    // (#2717), and every excerpt's bars line up, whatever its line number.
    let source = "def total = 1\n" + String.replicate 8 "\n" + "def total = 2\n"
    Assert.Equal(
        """error NYX0113: Duplicate definition 'total'
   --> main.nyx:10:5
    |
 10 | def total = 2
    |     ^^^^^
    = The name 'total' is already defined in this module
    = note: 'total' is first defined here
   --> main.nyx:1:5
    |
  1 | def total = 1
    |     ^^^^^
""".ReplaceLineEndings "\n",
        diagnosticLines source)
