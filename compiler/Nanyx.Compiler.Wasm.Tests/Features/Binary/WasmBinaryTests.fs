module TranspilerWasmTests.Features.Binary.WasmBinary

open System
open System.IO
open Xunit
open Transpiler.Wasm
open TranspilerWasmTestHelpers

// The binary format the compiler writes from its WebAssembly text (`WasmBinary.assemble`, #1235): a program runs the
// same from either.

/// What running `program` with the CLI's runtime writes to stdout and stderr, and how it ended.
let private run (program: Nanyx.Runtime.Program) =
    let input = Path.GetTempFileName()
    try
        use output = new MemoryStream()
        use error = new MemoryStream()
        let io = Nanyx.Runtime.Redirected(input, output, error, [])
        let outcome =
            match program with
            | Nanyx.Runtime.Text wat -> Nanyx.Runtime.WasmRunner.run io [ "program" ] wat
            | Nanyx.Runtime.Binary wasm -> Nanyx.Runtime.WasmRunner.runBinary io [ "program" ] wasm
        let ended =
            match outcome with
            | Ok () -> "ok"
            // A trap's message has a backtrace whose offsets differ between the two; what trapped, and where, does not.
            | Error(Nanyx.Runtime.Trapped message) ->
                let lines = message.ReplaceLineEndings("\n").Split '\n'
                let caused = lines |> Array.tryFind (fun line -> line.Contains "wasm trap") |> Option.defaultValue ""
                let frames = lines |> Array.filter (fun line -> line.Contains "!") |> Array.map (fun line -> line.Substring(line.IndexOf '!'))
                let at = String.concat ", " frames
                $"trapped: {caused.Trim()} at {at}"
            | Error error -> $"%A{error}"
        Text.Encoding.UTF8.GetString(output.ToArray()), Text.Encoding.UTF8.GetString(error.ToArray()), ended
    finally
        File.Delete input

/// Asserts that the program `source` runs the same from its binary as from its text, and gives how it ran.
let private assertRunsTheSame (source: string) =
    let wat = transpileWat source
    let fromText = run (Nanyx.Runtime.Text wat)
    let fromBinary = run (Nanyx.Runtime.Binary(WasmBinary.assemble wat))
    Assert.Equal(fromText, fromBinary)
    wat, fromText

[<Fact>]
let ``A program prints the same`` () =
    let _, (output, _, ended) =
        assertRunsTheSame
            """@entry
def main = {
  def greet = { name: string -> println("Hello, {name}! {string.length(name)}") }
  greet("Ada")
  greet("Édouard")
  println("{1.5 * 3.0} {7 / 2} {-12 % 5}")
}
"""
    Assert.Equal("ok", ended)
    Assert.Contains("Hello, Édouard!", output)

[<Fact>]
let ``A program that traps traps the same, in the same function`` () =
    let _, (_, _, ended) =
        assertRunsTheSame
            """def divide = { a: int, b: int -> a / b }

@entry
def main = {
  println("{divide(1, string.length("") )}")
}
"""
    Assert.StartsWith("trapped: ", ended)
    Assert.Contains("divide by zero", ended)

[<Fact>]
let ``A tail call runs in constant stack from the binary too`` () =
    let wat, (output, _, ended) =
        assertRunsTheSame
            """def count = { n: int, total: int -> if n == 0 then total else count(n - 1, total + 1) }

@entry
def main = {
  println("{count(1000000, 0)}")
}
"""
    Assert.Contains("return_call", wat)
    Assert.Equal("ok", ended)
    Assert.Equal("1000000\n", output.ReplaceLineEndings "\n")

[<Fact>]
let ``Closures, records and tags run the same`` () =
    let _, (_, _, ended) =
        assertRunsTheSame
            """type Shape = #circle(float) | #square(float)

def area = { shape: Shape ->
  match shape
  | #circle(r) -> 3.0 * r * r
  | #square(side) -> side * side
}

@entry
def main = {
  def add = { n: int -> { x: int -> x + n } }
  def addTwo = add(2)
  def point = (x = 1, y = 2)
  def moved = point with (x = 5)
  println("{[1, 2, 3] \list.map(addTwo)} {moved.x} {area(#circle(1.0))} {area(#square(2.0))}")
}
"""
    Assert.Equal("ok", ended)

[<Fact>]
let ``Every end-to-end test's module assembles to one that validates`` () =
    let directory = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "E2E")
    use engine = new Wasmtime.Engine()
    for path in Directory.GetFiles(directory, "expected.wat", SearchOption.AllDirectories) do
        let wasm =
            try WasmBinary.assemble (File.ReadAllText path)
            with WasmBinary.WasmAssemblyError message -> failwith $"{path}: {message}"
        use module' =
            try Wasmtime.Module.FromBytes(engine, "program", wasm)
            with :? Wasmtime.WasmtimeException as ex -> failwith $"{path}: {ex.Message}"
        Assert.NotNull module'

[<Theory>]
[<InlineData("directives.nyx")>]
[<InlineData("money.nyx")>]
[<InlineData("shop.nyx")>]
let ``An example runs the same from its binary, with its memory checked`` (example: string) =
    let path = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "..", "examples", example)
    let wat = transpileWatFileWith checkedOptions path
    let counted (program: Nanyx.Runtime.Program) =
        use output = new MemoryStream()
        use error = new MemoryStream()
        let input = Path.GetTempFileName()
        try
            let counters = [| Transpiler.Wasm.CodeGen.CheckedExports.liveBlocks; Transpiler.Wasm.CodeGen.CheckedExports.allocations |]
            let start = Transpiler.Wasm.CodeGen.CheckedExports.start
            let io = Nanyx.Runtime.Redirected(input, output, error, [])
            let outcome =
                match program with
                | Nanyx.Runtime.Text wat -> Nanyx.Runtime.WasmRunner.runReading io [ example ] start counters wat
                | Nanyx.Runtime.Binary wasm -> Nanyx.Runtime.WasmRunner.runBinaryReading io [ example ] start counters wasm
            $"%A{outcome}", Text.Encoding.UTF8.GetString(output.ToArray())
        finally
            File.Delete input
    let fromText = counted (Nanyx.Runtime.Text wat)
    Assert.Equal(fromText, counted (Nanyx.Runtime.Binary(WasmBinary.assemble wat)))
    Assert.StartsWith("Ok [|0;", fst fromText)

[<Fact>]
let ``nanyx build writes a .wasm that runs, and --wat writes the text`` () =
    let files = [ "main.nyx", "module main\n\n@entry\ndef main = { println(\"built\") }\n" ]
    use directory = new TempDirectory()
    let root = directory.Path
    Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
    File.WriteAllText(Path.Combine(root, "app.nyxp"), $"(package = (name = \"app\", version = \"0.1.0\", sources = [\"src/**/*.nyx\"]), {consoleHostEntry})")
    for path, text in files do
        File.WriteAllText(Path.Combine(root, "src", path), text)
    let built = nanyxIn root [ "build" ]
    Assert.Equal(0, built.ExitCode)
    let wasm = File.ReadAllBytes(Path.Combine(root, "out", "app.wasm"))
    Assert.Equal<byte array>([| 0uy; 0x61uy; 0x73uy; 0x6Duy |], wasm.[.. 3])
    let output, _, ended = run (Nanyx.Runtime.Binary wasm)
    Assert.Equal(("built\n", "ok"), (output.ReplaceLineEndings "\n", ended))
    let text = nanyxIn root [ "build"; "--wat" ]
    Assert.Equal(0, text.ExitCode)
    Assert.StartsWith("(module", File.ReadAllText(Path.Combine(root, "out", "app.wat")))

[<Fact>]
let ``The text's numbers, strings and comments are read as WebAssembly reads them`` () =
    let wasm =
        WasmBinary.assemble
            """(module ;; a comment
  (memory 1) (; a (; nested ;) block comment ;)
  (export "memory" (memory 0))
  (global $big (mut i32) (i32.const 0xDEADBEEF))
  (global $wide i64 (i64.const 18446744073709551615))
  (data (i32.const 16) "a\0a\u{e9}\"\\")
  (func $get (export "get") (result i32) global.get $big)
  (func $wide (export "wide") (result i64) (global.get $wide)))
"""
    use engine = new Wasmtime.Engine()
    use store = new Wasmtime.Store(engine)
    use module' = Wasmtime.Module.FromBytes(engine, "numbers", wasm)
    let instance = Wasmtime.Instance(store, module', [||])
    Assert.Equal(0xDEADBEEF, instance.GetFunction<int>("get").Invoke())
    Assert.Equal(-1L, instance.GetFunction<int64>("wide").Invoke())
    let memory = instance.GetMemory "memory"
    Assert.Equal<byte array>([| 97uy; 10uy; 0xC3uy; 0xA9uy; 34uy; 92uy |], memory.GetSpan(16L, 6).ToArray())
