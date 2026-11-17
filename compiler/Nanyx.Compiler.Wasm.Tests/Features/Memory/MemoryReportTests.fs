module TranspilerWasmTests.Features.Memory.MemoryReport

open System.IO
open Wasmtime
open Xunit
open Transpiler.Wasm.CodeGen
open TranspilerWasmTestHelpers

// The counters the checked allocator keeps for `nanyx run --memory` (#1562), and the report the CLI makes of them.

/// The checked allocator alone, instantiated on Wasmtime, with `alloc`, `free` and the counters `name`.
let private withCheckedAllocator (test: (int -> int) -> (int -> unit) -> (string -> int64) -> unit) =
    use engine = new Engine()
    use store = new Store(engine)
    store.SetWasiConfiguration(WasiConfiguration())
    use module' = Module.FromText(engine, "allocator", allocatorModuleWat checkedOptions)
    use linker = new Linker(engine)
    linker.DefineWasi()
    let instance = linker.Instantiate(store, module')
    let counter (name: string) =
        match instance.GetGlobal(name).GetValue() with
        | :? int64 as value -> value
        | value -> int64 (value :?> int)
    test (instance.GetFunction<int, int>("alloc").Invoke) (instance.GetAction<int>("free").Invoke) counter

[<Fact>]
let ``Counters - add up the bytes handed out, the most held at once, and the highest the heap reached`` () =
    withCheckedAllocator (fun alloc free counter ->
        let heapStart = counter CheckedExports.peakHeap
        let a = alloc 8
        let b = alloc 14
        free a
        alloc 4 |> ignore
        free b
        // 14 bytes are rounded up to 16; each block has a size word before it, which the bytes leave out. The freed
        // block of 8 is too big for the 4: that one comes from the top of the heap.
        Assert.Equal(28L, counter CheckedExports.bytesAllocated)
        Assert.Equal(24L, counter CheckedExports.peakLiveBytes)
        Assert.Equal(heapStart + 4L + 8L + 4L + 16L + 4L + 4L, counter CheckedExports.peakHeap))

/// `nanyx run --memory` on a script of `source`.
let private runWithReport (source: string) =
    use directory = new TempDirectory()
    let path = Path.Combine(directory.Path, "main.nyx")
    File.WriteAllText(path, source)
    nanyxWithInput "" [ "run"; "--memory"; path ]

[<Fact>]
let ``Report - follows a clean run on stderr`` () =
    let outcome =
        runWithReport
            """@entry
def main = {
  println("a" ++ "b")
}
"""
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal("ab\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    // Two strings of two and three bytes, each with a count and a length before it and a 0 byte after it.
    Assert.Equal(
        """Memory
  allocations        2
  frees              2
  live at exit       0
  reused in place    0
  bytes allocated    24
  peak live bytes    24
""",
        outcome.StandardError.ReplaceLineEndings("\n").Substring(0, outcome.StandardError.ReplaceLineEndings("\n").IndexOf "  peak heap"))
    Assert.Matches(@"  peak heap +[\d,]+ bytes \(1 page\)\n$", outcome.StandardError.ReplaceLineEndings "\n")

[<Fact>]
let ``Report - counts a record updated in place`` () =
    let outcome =
        runWithReport
            """@entry
def main = {
  def r = (x = 1, y = "a" ++ "b")
  def s = r with (x = 2)
  println(s.y)
}
"""
    Assert.Equal(0, outcome.ExitCode)
    Assert.Contains("  reused in place    1\n", outcome.StandardError.ReplaceLineEndings "\n")

[<Fact>]
let ``Report - follows what stopped the program`` () =
    let outcome =
        runWithReport
            """@entry
def main = {
  def s = "a" ++ "b"
  println("{1 / (string.length(s) - 2)}")
}
"""
    Assert.Equal(1, outcome.ExitCode)
    let errors = outcome.StandardError.ReplaceLineEndings "\n"
    Assert.StartsWith("Runtime error:", errors)
    Assert.Contains("Runtime error: division by zero", errors)
    Assert.Contains("\nMemory\n  allocations        ", errors)
