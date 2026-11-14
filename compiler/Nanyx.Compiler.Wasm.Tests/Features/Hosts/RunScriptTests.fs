/// A loose script runs under the console host: its statements, and its values that need a context, are the entry point
/// passed to `run` (#1245, see docs/src/content/work-in-progress/hosts.md).
module TranspilerWasmTests.Features.Hosts.Scripts

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``A script's statements run through the host's run`` () =
    let source =
        """import nanyx/console

println("hi")
"""
    let wat = transpileWat source
    Assert.Contains("call $__host_run", wat)
    runSource source |> assertRan "hi\n"

[<Fact>]
let ``A script value that needs a context is computed once, where it is written`` () =
    let source =
        """import nanyx/console

println("start")

def line = readLine()

println("got {line}")
println("again {line}")
"""
    runSourceWithInput "a\nb\n" source |> assertRan "start\ngot #some(a)\nagain #some(a)\n"

[<Fact>]
let ``A script value built from one that needs a context runs in the entry point too`` () =
    let source =
        """import nanyx/console

def line = readLine()

def twice = "{line}{line}"

println(twice)
println(twice)
"""
    runSourceWithInput "a\nb\n" source |> assertRan "#some(a)#some(a)\n#some(a)#some(a)\n"

[<Fact>]
let ``A script value that needs no context stays at the top level, where a function can name it`` () =
    let source =
        """import nanyx/console

def limit = 10

def small = { n -> n < limit }

println(small(3))
println(small(12))
"""
    runSource source |> assertRan "true\nfalse\n"

[<Fact>]
let ``Statements beside an entry point run through the host before it`` () =
    let source =
        """import nanyx/console

def line = readLine()

println("first {line}")

@entry
def main = { println("then main") }
"""
    let wat = transpileWat source
    Assert.Equal(2, countOccurrences "call $__host_run" wat)
    runSourceWithInput "a\n" source |> assertRan "first #some(a)\nthen main\n"
