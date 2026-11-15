/// The program's entry point runs through the console host's `run` (`nanyx.console/console.host`), which builds `Console` on `HostRuntime` (#1245,
/// see docs/src/content/work-in-progress/hosts.md).
module TranspilerWasmTests.Features.Hosts.ConsoleHost

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``An entry point that declares Console reads and writes through the host's instance`` () =
    let source =
        """module main

def greet: [Console] string -> () = { name -> println("hello {name}") }

@entry
def main: [Console] () -> () = {
  match readLine()
    | #some(line) -> greet(line)
    | #error(_) -> eprintln("no input")
  match readToEnd()
    | #some(rest) -> print("rest: {rest}")
    | #error(_) -> eprintln("no input")
  match readLine()
    | #some(_) -> println("more")
    | #error(#endOfInput) -> println("ended")
}
"""
    runSourceWithInput "Ada\r\nb\nc" source |> assertRan "hello Ada\nrest: b\ncended\n"

[<Fact>]
let ``The host writes with its foreign function of fd_write, which a deferred error shares, imported once`` () =
    let wat = transpileWat (withFinalNewline """println(if 1 > 2 then "a" else panic("no"))""")
    Assert.Equal(1, countOccurrences "(import \"wasi_snapshot_preview1\" \"fd_write\"" wat)
    Assert.Contains("""(import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))""", wat)
    Assert.DoesNotContain("__nanyx_write_stdout", wat)

[<Fact>]
let ``The start calls the host's run with the entry point`` () =
    let wat = transpileWat (withFinalNewline """@entry
def main = { println("hi") }""")
    Assert.Contains("(func $__host_run", wat)
    Assert.Contains("call $__host_run", wat)

[<Fact>]
let ``An entry point that does not write its type is passed the host's instance`` () =
    let source =
        """module main

def greet = { name -> println("hello {name}") }

@entry
def main = {
  match readLine()
    | #some(line) -> greet(line)
    | #error(_) -> eprintln("no input")
}
"""
    let wat = transpileWatNotInlined source
    // `main` takes the members of `Console`, which `run` passes from its `use`: it is not closed over
    // `nanyx/console`'s instance and adapted to the type `run` takes.
    Assert.Contains("(func $main (param $__ctx_flush i32)", wat)
    Assert.DoesNotContain("$__adapt_fn_unit_to_unit_to_fn_unit_to_unit_ctx", wat)
    runSourceWithInput "Ada\n" source |> assertRan "hello Ada\n"
