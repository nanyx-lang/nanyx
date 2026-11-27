/// `nanyx run --dbg-to` (#2054): each `dbg` the program reaches is written to a channel, a file of JSON lines, for an
/// editor to show where it is written, and none of them to stderr.
module TranspilerWasmTests.Features.Dbg.RunDbgChannel

open System.IO
open System.Text.Json
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

/// A hit as the channel writes it: its file, line, column, value and sequence number.
type private Hit = { File: string; Line: int; Column: int; Value: string; Seq: int }

/// The hits written to the file at `path`, a line each, in order.
let private hitsIn (path: string) =
    File.ReadAllLines path
    |> Array.filter (fun line -> line <> "")
    |> Array.map (fun line ->
        let json = JsonDocument.Parse(line).RootElement
        { File = json.GetProperty("file").GetString()
          Line = json.GetProperty("line").GetInt32()
          Column = json.GetProperty("column").GetInt32()
          Value = json.GetProperty("value").GetString()
          Seq = json.GetProperty("seq").GetInt32() })
    |> List.ofArray

[<Fact>]
let ``--dbg-to writes each hit as a JSON line, in order, with its full path and place, and nothing to stderr`` () =
    use directory = new TempDirectory()
    directory.Write "main.nyx" "def total = [3, 4, 5] \\list.sum\ndbg(total)\n\nfor [1, 2] | n -> dbg(n * 2)\n\nprintln(\"done\")\n"
    let outcome = nanyxIn directory.Path [ "run"; "--dbg-to"; "hits.jsonl"; "main.nyx" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal("done\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal(0, outcome.ExitCode)
    let main = Path.GetFullPath(Path.Combine(directory.Path, "main.nyx"))
    Assert.Equal<Hit list>(
        [ { File = main; Line = 2; Column = 1; Value = "12"; Seq = 0 }
          { File = main; Line = 4; Column = 19; Value = "2"; Seq = 1 }
          { File = main; Line = 4; Column = 19; Value = "4"; Seq = 2 } ],
        hitsIn (Path.Combine(directory.Path, "hits.jsonl")))

[<Fact>]
let ``Without --dbg-to, the program writes each dbg to its stderr, and nothing to stdout`` () =
    use directory = new TempDirectory()
    directory.Write "main.nyx" "dbg(1)\nprintln(\"out\")\n"
    let outcome = nanyxIn directory.Path [ "run"; "main.nyx" ]
    Assert.Equal("[main.nyx:1] 1\n", outcome.StandardError.ReplaceLineEndings "\n")
    Assert.Equal("out\n", outcome.StandardOutput.ReplaceLineEndings "\n")

[<Fact>]
let ``A dbg in a module the script imports names that module's file, in a package names its own`` () =
    use directory = new TempDirectory()
    directory.Write "lib/shapes.nyx" "export module shapes\n\nexport def area: int -> int = { side -> dbg(side * side) }\n"
    directory.Write "main.nyx" "import lib/shapes as shapes\n\ndbg(shapes.area(3))\n"
    let outcome = nanyxIn directory.Path [ "run"; "--dbg-to"; "hits.jsonl"; "main.nyx" ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal<(string * int * string) list>(
        [ Path.GetFullPath(Path.Combine(directory.Path, "lib", "shapes.nyx")), 3, "9"
          Path.GetFullPath(Path.Combine(directory.Path, "main.nyx")), 3, "9" ],
        hitsIn (Path.Combine(directory.Path, "hits.jsonl")) |> List.map (fun hit -> hit.File, hit.Line, hit.Value))

[<Fact>]
let ``A package's dbg names its source file by its full path`` () =
    use directory = new TempDirectory()
    directory.Write "app.nyxp" $"(\n  package = (name = \"app\", version = \"0.1.0\", sources = [\"src/**/*.nyx\"])\n  host = (package = \"nanyx.cli.host\", version = \"{Toolchain.version}\")\n)\n"
    directory.Write "src/main.nyx" "module main\n\n@entry\ndef main = {\n  dbg(\"hello\")\n}\n"
    let outcome = nanyxIn directory.Path [ "run"; "--dbg-to"; "hits.jsonl" ]
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal<(string * int) list>(
        [ Path.GetFullPath(Path.Combine(directory.Path, "src", "main.nyx")), 5 ],
        hitsIn (Path.Combine(directory.Path, "hits.jsonl")) |> List.map (fun hit -> hit.File, hit.Line))

[<Fact>]
let ``A --dbg-to path that can't be written is a usage error, and the program doesn't run`` () =
    use directory = new TempDirectory()
    directory.Write "main.nyx" "println(\"ran\")\n"
    let outcome = nanyxIn directory.Path [ "run"; "--dbg-to"; "missing/hits.jsonl"; "main.nyx" ]
    Assert.Equal(2, outcome.ExitCode)
    Assert.StartsWith("error: --dbg-to can't write to ", outcome.StandardError)
    Assert.Equal("", outcome.StandardOutput)
