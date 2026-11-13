module TranspilerWasmTests.Features.Core.FunctionAndLocals

open System
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Transpile function with parameters`` () =
    let source =
        "module M\n\n" +
        "def add: int, int -> int = { x, y -> x + y }"

    let wat = transpileWat source

    Assert.Contains("(func $add (param $x_i64 i64) (param $y_i64 i64) (result i64)", wat)
    Assert.Contains("local.get $x_i64", wat)
    Assert.Contains("local.get $y_i64", wat)
    Assert.Contains("call $__nanyx_int_add", wat)

[<Fact>]
let ``Transpile function call with arguments`` () =
    let source =
        "module M\n\n" +
        "def add = { x, y -> x + y }\n\n" +
        "def main = add(1, 2)"

    let wat = transpileWat source

    Assert.Contains("(func $main (result i64)", wat)
    Assert.Contains("i64.const 1", wat)
    Assert.Contains("i64.const 2", wat)
    Assert.Contains("call $add", wat)

[<Fact>]
let ``Transpile attached function on a built-in type`` () =
    let source =
        "module M\n\n" +
        "def int.double: int -> int = { n -> n * 2 }\n\n" +
        "def main = 3 \\ double"

    let result = NyxCompiler.Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let wat = Transpiler.Wasm.CodeGen.transpileTypedModuleToWat result.Typed.Value

    Assert.Contains("i64.const 3", wat)
    Assert.Contains("call $int_double", wat)

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
    let written =
        """
type Nest(a) = #leaf(a) | #deeper(Nest(list(a)))

def depth: Nest(int) -> int = { t ->
  def u = t
  1
}
"""
    let source = written.ReplaceLineEndings "\n"
    let message, line, column = backendFailure source
    Assert.Equal("The WebAssembly backend cannot compile a recursive type that holds itself at other type arguments yet", message)
    Assert.Equal(placeOf "{ t ->" source, (line, column))

[<Fact>]
let ``Transpile block local variable`` () =
    let source =
        "module M\n\n" +
        "def incTwice = { x ->\n" +
        "  def y = x + 1\n" +
        "  y + 1\n" +
        "}"

    let wat = transpileWat source

    Assert.Contains("(func $incTwice (param $x_i64 i64) (result i64) (local $y_i64 i64)", wat)
    Assert.Contains("local.set $y_i64", wat)
    Assert.Contains("local.get $y_i64", wat)

[<Fact>]
let ``Transpile top-level constant def`` () =
    let source =
        "module M\n\n" +
        "def one = 1"

    let wat = transpileWat source

    Assert.Contains("(func $one (result i64)", wat)
    Assert.Contains("i64.const 1", wat)
    Assert.Contains("(export \"one\" (func $one))", wat)

[<Fact>]
let ``Transpile magic dbg for int`` () =
    let source =
        "module M\n\n" +
        "def main = dbg(42)"

    let wat = transpileWat source

    Assert.Contains("(import \"wasi_snapshot_preview1\" \"fd_write\" (func $fd_write", wat)
    Assert.Contains("(func $__dbg_show_int (param $__dbg_value_i64 i64)", wat)
    Assert.Contains("i64.const 42", wat)
    Assert.Contains("call $dbg_str", wat)

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

    // Printed through a printer of its type, which needs nothing from the host.
    Assert.Contains("call $__dbg_show_tag_test", wat)
    Assert.DoesNotContain("(import \"env\"", wat)

[<Fact>]
let ``Transpile payload tag literal`` () =
    let source =
        "module M\n\n" +
        "def main = #some(42)"

    let wat = transpileWat source

    Assert.Contains("i32.const 3\n    i64.const 42\n    call $__nanyx_tag_box_i64", wat)
    Assert.Contains("(func $__nanyx_tag_box_i64", wat)

[<Fact>]
let ``Transpile magic dbg for payload tag literal`` () =
    let source =
        "module M\n\n" +
        "def main = dbg(#some(42))"

    let wat = transpileWat source

    Assert.Contains("call $__dbg_show_tag_some_int", wat)
    Assert.Contains("call $__dbg_show_int", wat)
    Assert.DoesNotContain("(import \"env\"", wat)

[<Fact>]
let ``Transpile interpolated dbg string from file`` () =
    use dir = new TempDirectory()
    let childPath = Path.Combine(dir.Path, "child.nyx")
    let mainPath = Path.Combine(dir.Path, "main.nyx")
    File.WriteAllText(childPath, "export def double = { x -> x * 2 }")
    File.WriteAllText(mainPath, "import \"child.nyx\" as child\n\ndef main = {\n  def result = child.double(21)\n  dbg(\"Result: {result}\")\n}")

    let wat = transpileWatFile mainPath

    Assert.Contains("call $dbg_str", wat)
    Assert.Contains("__interp_cursor", wat)
    Assert.Contains("i32.const 82", wat)

[<Fact>]
let ``Run - a local generic function frees what it is given, at each type it is used at`` () =
    // A closure for each type it is used at: the string it is given and ignores is freed, the int left alone (#616).
    """
import nanyx/console

@entry
def main = {
  def first = { x, y -> x }
  println(first("a" ++ "b", "c" ++ "d"))
  println(first("e", 1))
  println(first(2, "f" ++ "g"))
}
"""
    |> runSource
    |> assertRan "ab\ne\n2\n"
