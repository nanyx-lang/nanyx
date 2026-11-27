/// `nanyx run --show-values` (#3201): with `--dbg-to`, each value the program's own code binds or gives is written to
/// the channel too, as a line whose kind is `value`, beside the `dbg`s, whose kind is `dbg`.
module TranspilerWasmTests.Features.Dbg.ShowValues

open System.IO
open System.Text.Json
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

/// A line of the channel: its kind, its line, its column and its value.
type private Shown = { Kind: string; Line: int; Column: int; Value: string }

/// The lines written to the channel `hits.jsonl` in `directory`, in order, each with the file it names.
let private shownIn (directory: string) =
    File.ReadAllLines(Path.Combine(directory, "hits.jsonl"))
    |> Array.filter (fun line -> line <> "")
    |> Array.map (fun line ->
        let json = JsonDocument.Parse(line).RootElement
        json.GetProperty("file").GetString(),
        { Kind = json.GetProperty("kind").GetString()
          Line = json.GetProperty("line").GetInt32()
          Column = json.GetProperty("column").GetInt32()
          Value = json.GetProperty("value").GetString() })
    |> List.ofArray

[<Fact>]
let ``--show-values writes each value a def binds and a statement gives, and a dbg as a dbg`` () =
    use directory = new TempDirectory()
    directory.Write
        "main.nyx"
        "def square = { x: int -> x * x }\ndef total = 40 + 2\ndef main = {\n  def names = [\"ada\", \"bo\"]\n  println(\"hi\")\n  dbg(names \\list.length)\n}\nmain()\nsquare(total)\n"
    let outcome = nanyxIn directory.Path [ "run"; "--show-values"; "--dbg-to"; "hits.jsonl"; "main.nyx" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal("hi\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal(0, outcome.ExitCode)
    let main = Path.GetFullPath(Path.Combine(directory.Path, "main.nyx"))
    // The functions, `square` and `main`, and `println`, which gives `()`, aren't shown; `main()` gives what its `dbg` does.
    Assert.Equal<(string * Shown) list>(
        [ main, { Kind = "value"; Line = 4; Column = 3; Value = "[\"ada\", \"bo\"]" }
          main, { Kind = "dbg"; Line = 6; Column = 3; Value = "2" }
          main, { Kind = "value"; Line = 8; Column = 1; Value = "2" }
          main, { Kind = "value"; Line = 2; Column = 13; Value = "42" }
          main, { Kind = "value"; Line = 1; Column = 26; Value = "1764" }
          main, { Kind = "value"; Line = 9; Column = 1; Value = "1764" } ],
        shownIn directory.Path)

[<Fact>]
let ``A shown sequence is written by its type, and isn't pulled`` () =
    use directory = new TempDirectory()
    directory.Write "main.nyx" "def ticks = seq.unfold(0, { n ->\n  println(\"pulled\")\n  #some(n, n + 1)\n})\nticks\nprintln(\"done\")\n"
    let outcome = nanyxIn directory.Path [ "run"; "--show-values"; "--dbg-to"; "hits.jsonl"; "main.nyx" ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal("done\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal<string list>([ "<sequence of int>"; "<sequence of int>" ], shownIn directory.Path |> List.map (snd >> _.Value))

[<Fact>]
let ``A shown value no printer writes is written by its type, where a dbg of it doesn't compile`` () =
    use directory = new TempDirectory()
    directory.Write "main.nyx" "import nanyx/marray\n\ndef size = memory {\n  def cells = MArray.fromList([1, 2])\n  MArray.length(cells)\n}\nprintln(size)\n"
    let outcome = nanyxIn directory.Path [ "run"; "--show-values"; "--dbg-to"; "hits.jsonl"; "main.nyx" ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal<(int * string) list>(
        [ 4, "<value of MArray(int)>"; 5, "2"; 3, "2" ],
        shownIn directory.Path |> List.map (fun (_, shown) -> shown.Line, shown.Value))

[<Fact>]
let ``A package shows the values of its own files, and none of the library's`` () =
    use directory = new TempDirectory()
    directory.Write "app.nyxp" $"(\n  package = (name = \"app\", version = \"0.1.0\", sources = [\"src/**/*.nyx\"])\n  host = (package = \"nanyx.cli.host\", version = \"{Toolchain.version}\")\n)\n"
    directory.Write "src/shapes.nyx" "module shapes\n\nexport def area: int -> int = { side ->\n  def squared = side * side\n  squared\n}\n"
    directory.Write "src/main.nyx" "module main\n\nimport shapes as shapes\n\n@entry\ndef main = {\n  println([1, 2] \\list.map(shapes.area))\n}\n"
    let outcome = nanyxIn directory.Path [ "run"; "--show-values"; "--dbg-to"; "hits.jsonl" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(0, outcome.ExitCode)
    let shapes = Path.GetFullPath(Path.Combine(directory.Path, "src", "shapes.nyx"))
    Assert.Equal<(string * int * string) list>(
        [ shapes, 4, "1"; shapes, 5, "1"; shapes, 4, "4"; shapes, 5, "4" ],
        shownIn directory.Path |> List.map (fun (file, shown) -> file, shown.Line, shown.Value))

[<Fact>]
let ``--show-values without --dbg-to is a usage error, and the program doesn't run`` () =
    use directory = new TempDirectory()
    directory.Write "main.nyx" "println(\"ran\")\n"
    let outcome = nanyxIn directory.Path [ "run"; "--show-values"; "main.nyx" ]
    Assert.Equal(2, outcome.ExitCode)
    Assert.Equal("error: --show-values writes the values to the file --dbg-to names: give it --dbg-to too.\n", outcome.StandardError.ReplaceLineEndings "\n")
    Assert.Equal("", outcome.StandardOutput)
