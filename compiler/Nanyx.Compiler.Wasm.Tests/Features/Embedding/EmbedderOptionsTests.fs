/// What a module compiled for an embedder other than the CLI's runtime, such as a page, asks for (#2150): its start
/// exported rather than run when it is instantiated (`WasmOptions.ExportStart`), and what `dbg` prints given to the
/// embedder's `nanyx:debug` `dbg` with no probes (`WasmOptions.DbgToEmbedder`).
module TranspilerWasmTests.Features.Embedding.EmbedderOptions

open System
open System.IO
open Xunit
open NyxCompiler
open Transpiler.Wasm.CodeGen
open TranspilerWasmTestHelpers

[<Fact>]
let ``A module whose start is exported doesn't run when instantiated, and runs when the embedder calls it`` () =
    let wat = transpileWatWith { WasmOptions.Default with ExportStart = true } (normalized "println(\"hi\")\n")
    Assert.Contains($"""(export "{CheckedExports.start}" (func """, wat)
    Assert.DoesNotContain("(start ", wat)
    let input = Path.GetTempFileName()
    try
        use output = new MemoryStream()
        use error = new MemoryStream()
        let ran = Nanyx.Runtime.WasmRunner.runReading (Nanyx.Runtime.Redirected(input, output, error, [])) [ "main" ] [] CheckedExports.start [||] wat
        Assert.True(Result.isOk ran, $"%A{ran}")
        Assert.Equal("hi\n", Text.Encoding.UTF8.GetString(output.ToArray()))
    finally
        File.Delete input

[<Fact>]
let ``A module that gives dbg to the embedder imports nanyx:debug's dbg alone, with no probes`` () =
    let wat = transpileWatWith { WasmOptions.Default with DbgToEmbedder = true } (normalized "dbg(1)\nprintln(2)\n")
    Assert.Contains("""(import "nanyx:debug" "dbg" """, wat)
    for probeImport in [ "enabled"; "probe"; "local" ] do
        Assert.DoesNotContain($"""(import "nanyx:debug" "{probeImport}" """, wat)
    // What the embedder leaves to the program it writes itself.
    Assert.Contains("call $dbg_str", wat)

[<Fact>]
let ``A module that gives dbg to the embedder imports nothing for it when it has no dbg`` () =
    let wat = transpileWatWith { WasmOptions.Default with DbgToEmbedder = true } (normalized "println(2)\n")
    Assert.DoesNotContain("nanyx:debug", wat)
