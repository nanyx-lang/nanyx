module TranspilerWasmTests.Features.Core.FunctionAndLocals

open System
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Transpile function with parameters`` () =
    let source =
        "module M\n\n" +
        "def add = { x, y -> x + y }"

    let wat = transpileWat source

    Assert.Contains("(func $add (param $x i32) (param $y i32) (result i32)", wat)
    Assert.Contains("local.get $x", wat)
    Assert.Contains("local.get $y", wat)
    Assert.Contains("i32.add", wat)

[<Fact>]
let ``Transpile function call with arguments`` () =
    let source =
        "module M\n\n" +
        "def add = { x, y -> x + y }\n\n" +
        "def main = add(1, 2)"

    let wat = transpileWat source

    Assert.Contains("(func $main (result i32)", wat)
    Assert.Contains("i32.const 1", wat)
    Assert.Contains("i32.const 2", wat)
    Assert.Contains("call $add", wat)

[<Fact>]
let ``Transpile attached function on a built-in type`` () =
    let source =
        "module M\n\n" +
        "def Int.double: int -> int = { n -> n * 2 }\n\n" +
        "def main = 3 \\ double"

    let result = NyxCompiler.Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let wat = Transpiler.Wasm.CodeGen.transpileTypedModuleToWat result.Typed.Value

    Assert.Contains("i32.const 3", wat)
    Assert.Contains("call $Int_double", wat)

/// What the backend reports for `source`, which type-checks but which it cannot compile: the diagnostic, and the line
/// and column (both from 1) it is reported at.
let private backendFailure (source: string) =
    let result = NyxCompiler.Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    match Transpiler.Wasm.CodeGen.tryTranspileTypedModuleToWat result.Typed.Value with
    | Ok _ -> failwith "Expected the backend to fail"
    | Error diagnostic ->
        Assert.Equal(NyxCompiler.DiagnosticCodes.UnsupportedByBackend.Code, diagnostic.Code)
        let span = diagnostic.Span.Value
        diagnostic.Message, span.StartLine + 1, span.StartColumn + 1

/// The line and column (both from 1) where `text` first appears in `source`.
let private placeOf (text: string) (source: string) =
    let index = source.IndexOf(text, StringComparison.Ordinal)
    let before = source.Substring(0, index)
    before.Split('\n').Length, index - (before.LastIndexOf('\n') + 1) + 1

[<Fact>]
let ``What the backend cannot compile is reported where it is written`` () =
    // An expression it cannot compile is reported at that expression, not the ones inside it, and named.
    let printed =
        "def shout: (int, int) -> int = { t ->\n" +
        "  def u = dbg(t)\n" +
        "  u.0\n" +
        "}\n"
    let message, line, column = backendFailure printed
    Assert.Equal("The WebAssembly backend cannot compile `dbg` of a value of type (0: int, 1: int) yet", message)
    Assert.Equal(placeOf "dbg(t)" printed, (line, column))
    let useIn =
        "context Ask = (ask: () -> int)\n" +
        "def twice: [Ask] () -> int = { ask() + ask() }\n" +
        "def main = {\n" +
        "  def n = use Ask(ask = { 21 }) in twice()\n" +
        "  n\n" +
        "}\n"
    let message, line, column = backendFailure useIn
    Assert.Equal("The WebAssembly backend cannot compile `use ... in` yet", message)
    Assert.Equal(placeOf "use Ask" useIn, (line, column))

[<Fact>]
let ``Transpile block local variable`` () =
    let source =
        "module M\n\n" +
        "def incTwice = { x ->\n" +
        "  def y = x + 1\n" +
        "  y + 1\n" +
        "}"

    let wat = transpileWat source

    Assert.Contains("(func $incTwice (param $x i32) (result i32) (local $y i32)", wat)
    Assert.Contains("local.set $y", wat)
    Assert.Contains("local.get $y", wat)

[<Fact>]
let ``Transpile top-level constant def`` () =
    let source =
        "module M\n\n" +
        "def one = 1"

    let wat = transpileWat source

    Assert.Contains("(func $one (result i32)", wat)
    Assert.Contains("i32.const 1", wat)
    Assert.Contains("(export \"one\" (func $one))", wat)

[<Fact>]
let ``Transpile magic dbg for int`` () =
    let source =
        "module M\n\n" +
        "def main = dbg(42)"

    let wat = transpileWat source

    Assert.Contains("(import \"wasi_snapshot_preview1\" \"fd_write\" (func $fd_write", wat)
    Assert.Contains("(func $dbg (param $x i32)", wat)
    Assert.Contains("i32.const 42", wat)
    Assert.Contains("call $dbg", wat)

[<Fact>]
let ``Transpile magic dbg for string literal`` () =
    let source =
        "module M\n\n" +
        "def main = dbg(\"hello\")"

    let wat = transpileWat source

    Assert.Contains("(func $dbg_str (param $ptr i32)", wat)
    Assert.Contains("hello", wat)
    Assert.Contains("call $dbg_str", wat)

[<Fact>]
let ``Transpile bare tag literal`` () =
    let source =
        "module M\n\n" +
        "def main = #ok"

    let wat = transpileWat source

    Assert.Contains("(func $main (result i32)", wat)
    Assert.Contains("i32.const 3", wat)

[<Fact>]
let ``Transpile magic dbg for bare tag literal`` () =
    let source =
        "module M\n\n" +
        "def main = dbg(#test)"

    let wat = transpileWat source

    Assert.Contains("(import \"env\" \"dbg_tag_test\" (func $dbg_tag_test))", wat)
    Assert.Contains("call $dbg_tag_test", wat)
    Assert.DoesNotContain("(import \"env\" \"dbg\" (func $dbg (param i32)))", wat)

[<Fact>]
let ``Transpile payload tag literal`` () =
    let source =
        "module M\n\n" +
        "def main = #some(42)"

    let wat = transpileWat source

    Assert.Contains("i32.const 3\n    i32.const 42\n    call $__nanyx_tag_box", wat)
    Assert.Contains("(func $__nanyx_tag_box", wat)

[<Fact>]
let ``Transpile magic dbg for payload tag literal`` () =
    let source =
        "module M\n\n" +
        "def main = dbg(#some(42))"

    let wat = transpileWat source

    Assert.Contains("(import \"env\" \"dbg_tag_payload_some\" (func $dbg_tag_payload_some (param i32)))", wat)
    Assert.Contains("call $dbg_tag_payload_some", wat)

[<Fact>]
let ``Transpile interpolated dbg string from file`` () =
    let dir = Path.Combine(Path.GetTempPath(), "nanyx-wasm-interp-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    try
        let childPath = Path.Combine(dir, "child.nyx")
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(childPath, "export def double = { x -> x * 2 }")
        File.WriteAllText(mainPath, "import \"child.nyx\" as child\n\ndef main = {\n  def result = child.double(21)\n  dbg(\"Result: {result}\")\n}")

        let wat = transpileWatFile mainPath

        Assert.Contains("call $dbg_str", wat)
        Assert.Contains("__interp_cursor", wat)
        Assert.Contains("i32.const 82", wat)
    finally
        if Directory.Exists(dir) then
            Directory.Delete(dir, true)
