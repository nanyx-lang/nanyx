/// A foreign function declared with `@extern` is an import of the module, called directly (#1424, see
/// docs/src/content/work-in-progress/foreign-functions.md).
module TranspilerWasmTests.Features.Hosts.Externs

open System.IO
open Xunit
open NyxCompiler
open Nanyx.Runtime
open Transpiler.Wasm.CodeGen
open TranspilerWasmTestHelpers

/// `source` checked as the standard library's `console.host`, a host, which may require `HostRuntime`, and compiled.
let private hostWat (source: string) =
    match Compiler.compileFrom Linters.none (StandardLibraryModule "console.host") (normalized source) with
    | { Typed = Some typed } as result when result.Diagnostics.IsEmpty -> transpileTypedModuleToWat typed
    | result -> failwith $"""Compile error: {result.Diagnostics |> List.map _.Message |> String.concat "\n"}"""

/// What the module `wat` writes to stdout when the CLI's runtime runs it, or why it did not run.
let private run (wat: string) =
    let input = Path.GetTempFileName()
    try
        use output = new MemoryStream()
        use error = new MemoryStream()
        WasmRunner.run (Redirected(input, output, error, [])) [ "test" ] wat
        |> Result.map (fun () -> System.Text.Encoding.UTF8.GetString(output.ToArray()))
    finally
        File.Delete input

/// A host that calls WASI's `args_sizes_get` twice, and writes what it returned each time: 0, for success.
let private argsSizes =
    """module console.host

import nanyx/host.runtime

@extern("wasi_snapshot_preview1", "args_sizes_get")
def argsSizesGet: [HostRuntime] (I32, I32) -> I32

def sizes = { -> argsSizesGet(0, 4) }

writeStdout("{sizes()} {argsSizesGet(0, 4)}")
"""

[<Fact>]
let ``An extern is one import of the module, however many places call it`` () =
    let wat = hostWat argsSizes
    Assert.Equal(1, countOccurrences "(import \"wasi_snapshot_preview1\" \"args_sizes_get\"" wat)
    Assert.Contains("(func $import_wasi_snapshot_preview1_args_sizes_get (param i32 i32) (result i32))", wat)
    Assert.Contains("call $import_wasi_snapshot_preview1_args_sizes_get", wat)

[<Fact>]
let ``A host calls an extern of a WASI function, which the CLI's runtime provides`` () =
    Assert.Equal(Ok "0 0", run (hostWat argsSizes))

[<Fact>]
let ``An extern takes and returns each of the value types`` () =
    let source =
        """module console.host

import nanyx/host.runtime

@extern("test", "numbers")
def numbers: [HostRuntime] (I32, I64, F32, F64) -> F64

@extern("test", "nothing")
def nothing: [HostRuntime] () -> ()

nothing()
writeStdout("{numbers(1, 2, 3.0, 4.0)}")
"""
    let wat = hostWat source
    Assert.Contains("""(import "test" "numbers" (func $import_test_numbers (param i32 i64 f32 f64) (result f64)))""", wat)
    Assert.Contains("""(import "test" "nothing" (func $import_test_nothing))""", wat)
    // The module is valid: it fails only because nothing provides the imports.
    match run wat with
    | Error (Trapped message) -> Assert.Contains("test", message)
    | other -> failwith $"Expected a failure to instantiate, got %A{other}"

[<Fact>]
let ``An extern the runtime doesn't provide fails when the module is instantiated, naming the import`` () =
    let source =
        """module console.host

import nanyx/host.runtime

@extern("elsewhere", "missing")
def missing: [HostRuntime] () -> I32

writeStdout("{missing()}")
"""
    match run (hostWat source) with
    | Error (Trapped message) -> Assert.Contains("elsewhere", message); Assert.Contains("missing", message)
    | other -> failwith $"Expected a failure to instantiate, got %A{other}"
