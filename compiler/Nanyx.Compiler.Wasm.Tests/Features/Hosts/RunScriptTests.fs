/// A loose script runs under the console host: its statements, and its values that need a context, are the entry point
/// passed to `run` (#1245, see docs/src/content/deep-dives/hosts.md).
module TranspilerWasmTests.Features.Hosts.Scripts

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``A script's statements run through the host's run`` () =
    let source =
        """println("hi")
"""
    // Inlined, the host's run is part of the start function (#1469).
    let wat = transpileWatNotInlined source
    Assert.Contains("call $__host_run", wat)
    runSource source |> assertRan "hi\n"

[<Fact>]
let ``A script value that needs a context is computed once, where it is written`` () =
    let source =
        """println("start")

def line = readLine()

println("got {line}")
println("again {line}")
"""
    runSourceWithInput "a\nb\n" source |> assertRan "start\ngot #some(a)\nagain #some(a)\n"

[<Fact>]
let ``A script value built from one that needs a context runs in the entry point too`` () =
    let source =
        """def line = readLine()

def twice = "{line}{line}"

println(twice)
println(twice)
"""
    runSourceWithInput "a\nb\n" source |> assertRan "#some(a)#some(a)\n#some(a)#some(a)\n"

[<Fact>]
let ``A script value that needs no context stays at the top level, where a function can name it`` () =
    let source =
        """def limit = 10

def small = { n -> n < limit }

println(small(3))
println(small(12))
"""
    runSource source |> assertRan "true\nfalse\n"

[<Fact>]
let ``Statements beside an entry point run through the host before it`` () =
    let source =
        """def line = readLine()

println("first {line}")

@entry
def main = { println("then main") }
"""
    let wat = transpileWatNotInlined source
    // A call of the host's `run`, not its resume entry's tail call of it (see `TaskLowering`).
    Assert.Equal(2, countOccurrences "    call $__host_run" wat)
    runSourceWithInput "a\n" source |> assertRan "first #some(a)\nthen main\n"

[<Fact>]
let ``A script's own def named like a member of Console is the one its calls run`` () =
    let source =
        """def write = { s: string -> println("mine: " ++ s) }
def flush = { s: string -> "flushed " ++ s }

write("x")
println(flush("y"))
println(["z"] \list.map(flush))
"""
    runSource source |> assertRan "mine: x\nflushed y\n[flushed z]\n"

[<Fact>]
let ``An entry point's module's own def named like a member of Console is the one its calls run`` () =
    let source =
        """module main

def write = { s: string -> println("mine: " ++ s) }

@entry
def main = {
  write("x")
}
"""
    runSource source |> assertRan "mine: x\n"
[<Fact>]
let ``A script value that holds a lambda requiring Console runs it where a statement calls it`` () =
    // Building `handlers` calls nothing, so it needs no context; calling `log` does (#1522).
    let source =
        """def handlers = (log = { m: string -> println(m) }, count = { n: int -> n + 1 })

def pureCaller: int -> int = { n ->
  def f = handlers.count
  f(n)
}

def log = handlers.log
log("count {pureCaller(1)}")
"""
    runSource source |> assertRan "count 2\n"

[<Fact>]
let ``A script value that needs a context is the one its statements name, though the prelude exports the name`` () =
    let source =
        """def start = ticks()
def await = ticks() - start

println(await >= 0)
"""
    runSource source |> assertRan "true\n"
