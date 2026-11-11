module TranspilerWasmTests.Features.Memory.MemoryDeclaration

open System
open System.IO
open Xunit
open TranspilerWasmTestHelpers

// A module declares linear memory when any function in it reads or writes memory, not only when it allocates.

/// Runs `source` with plain `nanyx run`, without the memory checks, whose helpers would pull memory in on their own.
let private runUnchecked (source: string) =
    let inputPath = Path.Combine(Path.GetTempPath(), $"nanyx-run-{Guid.NewGuid():N}.nyx")
    File.WriteAllText(inputPath, source)
    try nanyxWithInput "" [ "run"; inputPath ]
    finally File.Delete inputPath

[<Theory>]
[<InlineData("""
def first: list(int) -> int = {
  | [] -> 0
  | [x, ..._] -> x
}
""")>]
[<InlineData("""
type Point = (x: int, y: int)
def getX: Point -> int = { p -> p.x + 1 }
""")>]
[<InlineData("""
def unwrap: #some(int) | #none -> int = {
  | #some(x) -> x
  | #none -> 0
}
""")>]
let ``Memory - a module of definitions that only read memory declares it`` (source: string) =
    Assert.Contains("(memory ", transpileWat source)
    runUnchecked source |> assertRan ""
    runSource source |> assertRan ""

[<Fact>]
let ``Memory - a module of a generic definition that reads a record field runs`` () =
    let source = """
def getX = { p -> p.x + 1 }
"""
    runUnchecked source |> assertRan ""
    runSource source |> assertRan ""
