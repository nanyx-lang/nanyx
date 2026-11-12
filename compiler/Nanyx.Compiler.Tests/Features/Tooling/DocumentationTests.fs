module Nanyx.Compiler.Tests.Features.Tooling.Documentation

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

/// A link target that names the file and the line, so a test can tell where a link goes.
let private linkTo = Func<SourceSpan, string>(fun span -> $"{Path.GetFileName span.File}:{span.StartLine + 1}")

/// The doc comment of the symbol named `name` in `result`, the compile of `source` as the file `file`, as Markdown.
let private docsOf (file: string) (source: string) (result: CompileResult) (name: string) =
    let typed = result.Typed.Value
    let symbol = typed.Symbols |> Map.values |> Seq.find (fun symbol -> symbol.Name = name)
    let reader = Documentation.Reader(file, Documentation.ofSource file source, result, source.Length, linkTo)
    reader.ForSymbol symbol

[<Fact>]
let ``A def's doc comment is found by its symbol, with links to names in scope`` () =
    let source =
        """--| One more than `n`.

  See [`dec`] and [`missing`].
def inc = { n -> n + 1 }

def dec = { n -> n - 1 }
def two = inc(1)
"""
    let result = Compiler.compileSource "main.nyx" source
    Assert.Equal(Some "One more than `n`.\n\nSee [`dec`](main.nyx:6) and `missing`.", docsOf (Path.GetFullPath "main.nyx") source result "inc")
    Assert.Equal(None, docsOf (Path.GetFullPath "main.nyx") source result "dec")

let private doubling =
    """--| Doubles `n`. See [`half`].
export def double = { n -> n * 2 }

def half = { n -> n / 2 }
"""

[<Fact>]
let ``The doc comment of a def in an imported file is read from that file, with links to its own names`` () =
    withTempDir (fun dir ->
        File.WriteAllText(Path.Combine(dir, "mod.nyx"), doubling)
        let mainPath = Path.Combine(dir, "main.nyx")
        let source = "import \"./mod.nyx\" as m\ndef four = m.double(2)\n"
        File.WriteAllText(mainPath, source)
        let result = Compiler.compileFile mainPath
        Assert.Equal(Some "Doubles `n`. See [`half`](mod.nyx:4).", docsOf mainPath source result "double"))

[<Fact>]
let ``A type's and a context member's doc comments are found by their symbols`` () =
    let source =
        """--| A point.
type Point = (x: int, y: int)

--| Things that count.
context Counter = (
  --| The next number.
  next: int -> int
)

def origin = Point(x = 0, y = 0)
def counted: [Counter] int -> int = { n -> next(n) }
"""
    let result = Compiler.compileSource "types.nyx" source
    let file = Path.GetFullPath "types.nyx"
    Assert.Equal(Some "A point.", docsOf file source result "Point")
    Assert.Equal(Some "Things that count.", docsOf file source result "Counter")
    Assert.Equal(Some "The next number.", docsOf file source result "next")

[<Fact>]
let ``A link to a reserved name of the standard library's is never made`` () =
    let written =
        """--| Compares as [`__equals`] does, unlike [`same`].
def same = { a, b -> a == b }
def h = int.hash(3)
"""
    let source = written.ReplaceLineEndings "\n"
    let result = Compiler.compileSource "main.nyx" source
    let file = Path.GetFullPath "main.nyx"
    Assert.Equal(Some "Compares as `__equals` does, unlike [`same`](main.nyx:2).", docsOf file source result "same")
    // Nor does a standard-library definition's doc comment mention one.
    let hash = docsOf file source result "int.hash"
    Assert.True(hash.IsSome)
    Assert.DoesNotContain(ReservedNames.prefix, hash.Value)

[<Fact>]
let ``A standard-library definition's doc comment is found by its symbol`` () =
    let source = "def lengths = list.map([[1], [2, 3]], list.length)\n"
    let result = Compiler.compileSource "main.nyx" source
    Assert.Equal(
        Some "`f` applied to each element, in order. Stack-safe: it works on a list of any length.",
        docsOf (Path.GetFullPath "main.nyx") source result "list.map")
