module TranspilerWasmTests.Features.Commands.DiagnosticExcerpt

open Xunit
open TranspilerWasmTestHelpers

// The CLI shows each diagnostic's line of source under it, with the span underlined (#880).

/// The lines `nanyx check` printed for `source` from its first diagnostic on, without the file's path.
let private diagnosticLines (source: string) =
    let output = (checkScript (source.ReplaceLineEndings "\n")).StandardOutput.ReplaceLineEndings "\n"
    let lines = output.Split '\n' |> List.ofArray
    lines
    |> List.skipWhile (fun line -> not (line.Contains ": error "))
    |> List.takeWhile (fun line -> not (line.StartsWith "Types:"))
    |> List.map (fun line -> match line.IndexOf ".nyx:" with -1 -> line | at -> line.Substring(at + 5))
    |> String.concat "\n"

[<Fact>]
let ``Check - an error shows its line with the span underlined`` () =
    let source =
        """def count = 3

def total = count + "one"
"""
    Assert.StartsWith(
        """3:13: error NYX0201: 'string' cannot be used with +: it is not a number. Use ++ to join them
  |
3 | def total = count + "one"
  |             ^^^^^^^^^^^^^
""".ReplaceLineEndings "\n",
        diagnosticLines source)

[<Fact>]
let ``Check - the underline keeps the tabs the line indents with`` () =
    let source = "def total = 1\n\n@entry\ndef main = {\n\tprintln(totl)\n}\n"
    Assert.StartsWith(
        "5:10: error NYX0101: Unknown identifier 'totl': did you mean 'total'?\n  |\n5 | \tprintln(totl)\n  | \t        ^^^^\n",
        diagnosticLines source)

[<Fact>]
let ``Check - a span over several lines is underlined to the end of its first`` () =
    let source =
        """def count: string = (
  1,
  2)
"""
    let lines = (diagnosticLines source).Split '\n'
    Assert.Equal("1 | def count: string = (", lines[2])
    Assert.Equal("  |                     ^", lines[3])
