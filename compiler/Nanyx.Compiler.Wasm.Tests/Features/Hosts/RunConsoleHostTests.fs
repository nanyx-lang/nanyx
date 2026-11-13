/// The program's entry point runs through `nanyx/console.host`'s `run`, which builds `Console` on `HostRuntime` (#1245,
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
let ``The start calls the host's run with the entry point`` () =
    let wat = transpileWat "@entry\ndef main = { println(\"hi\") }\n"
    Assert.Contains("(func $__host_run", wat)
    Assert.Contains("call $__host_run", wat)
