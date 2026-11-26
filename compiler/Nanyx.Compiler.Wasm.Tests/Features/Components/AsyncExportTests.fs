/// Exports marked `@component` that can wait (#2915): one whose type requires `Async` is an `async func` in the WIT
/// `nanyx build` writes, lifted with the callback ABI, and gives its result with `task.return`. The host is a fake
/// (`FakeWasiHost`), which keeps a clock of its own.
module TranspilerWasmTests.Features.Components.AsyncExports

open System.IO
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

/// A library package under the console host, `acme.time`, whose `stamp.nyx` is `stamp`, built: the WIT `nanyx build`
/// wrote, and the component's core module.
let private built (stamp: string) =
    use directory = new TempDirectory()
    Directory.CreateDirectory(Path.Combine(directory.Path, "src")) |> ignore
    let manifest = $"""(package = (name = "acme.time", version = "0.1.0", sources = ["src/**/*.nyx"]), {consoleHostEntry})"""
    File.WriteAllText(Path.Combine(directory.Path, "acme.time.nyxp"), manifest + "\n")
    File.WriteAllText(Path.Combine(directory.Path, "src", "stamp.nyx"), stamp)
    // The component, which the componentizer validates, and the WIT beside it; then its core module, as text.
    for arguments in [ [ "build"; "--release" ]; [ "build"; "--release"; "--wat" ] ] do
        let outcome = nanyxIn directory.Path arguments
        Assert.Equal("", outcome.StandardError)
        Assert.Equal(0, outcome.ExitCode)
    File.ReadAllText(Path.Combine(directory.Path, "out", "wit", "component.wit")), File.ReadAllText(Path.Combine(directory.Path, "out", "acme.time.wat"))

let private sleeper =
    "module stamp\n\n@component\nexport def later: [Async, Clock] int -> int = { ms ->\n  sleep(ms * 1ms)\n  ms + 1\n}\n\n@component\nexport def since: [Clock] int -> int = { start -> ticks() // 1ms - start }\n"

[<Fact>]
let ``An export whose type requires Async is an async func, and one that can't wait a func`` () =
    let wit, _ = built sleeper
    Assert.Contains("later: async func(ms: s64) -> s64;", wit)
    Assert.Contains("since: func(start: s64) -> s64;", wit)

[<Fact>]
let ``An export that sleeps returns to the host while it waits, and gives its result with task.return`` () =
    // It used not to build at all: the export ran the def's state machine to the end with a local named twice over.
    let _, wat = built sleeper
    let host = FakeWasiHost()
    let engine, store, instance = instantiateCoreWith host.Implement wat
    use _ = engine
    use _ = store
    host.Call(instance, "acme:time/stamp@0.1.0#later", Wasmtime.ValueBox.op_Implicit 30L)
    Assert.Equal(0, host.BlockingWaits)
    Assert.Equal(1, host.Waited)
    Assert.Equal(30_000_000L, host.Now)
    let given = host.TaskReturns.["[task-return]later"]
    Assert.Equal(31L, (Assert.Single given).AsInt64())
    // An export that can't wait is lifted synchronously, as before.
    Assert.Equal(20L, instance.GetFunction<int64, int64>("acme:time/stamp@0.1.0#since").Invoke(10L))

[<Fact>]
let ``An export that waits and gives a string gives it with task.return as an address and a length`` () =
    let echo =
        "module stamp\n\n@component\nexport def echo: [Async, Clock] string -> string = { text ->\n  sleep(5ms)\n  text ++ \"!\"\n}\n"
    let wit, wat = built echo
    Assert.Contains("echo: async func(text: string) -> string;", wit)
    let host = FakeWasiHost()
    let engine, store, instance = instantiateCoreWith host.Implement wat
    use _ = engine
    use _ = store
    // The argument, in a block the module allocates for the host, as a host does with `cabi_realloc`.
    let text = System.Text.Encoding.UTF8.GetBytes "hello"
    let block = instance.GetFunction<int, int, int, int, int>("cabi_realloc").Invoke(0, 0, 1, text.Length)
    let memory = instance.GetMemory("memory")
    text |> Array.iteri (fun index byte -> memory.WriteByte(int64 (block + index), byte))
    host.Call(instance, "acme:time/stamp@0.1.0#echo", Wasmtime.ValueBox.op_Implicit block, Wasmtime.ValueBox.op_Implicit text.Length)
    Assert.Equal(1, host.Waited)
    Assert.Equal("hello!", host.TaskReturnTexts.["[task-return]echo"])
