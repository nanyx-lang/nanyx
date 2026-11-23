/// A function that can wait is a state machine: it stops where a call it makes stops, keeps what it needs in a frame,
/// and carries on where it stopped when it is resumed (#1890, see "How tasks are compiled" in
/// docs/src/content/deep-dives/async.md). Nothing stops on its own yet, so these programs are compiled with every call
/// of a function value that can wait stopping once first (`WasmOptions.YieldAtEveryWait`), which runs every way a
/// state machine has of stopping and carrying on. Each must print what it prints without stopping, and leave no block
/// allocated: no frame is leaked, and nothing kept in one is freed twice.
module TranspilerWasmTests.Features.Async.RunStateMachine

open System.IO
open Xunit
open NyxCompiler
open Transpiler.Wasm.CodeGen
open TranspilerWasmTestHelpers

/// What `source` prints, with the blocks it left allocated, compiled with memory checks, inlined or not, and with every
/// wait stopping once when `yielding`. Any diagnostic fails.
let private run (inlining: bool) (yielding: bool) (source: string) =
    let typed =
        match Compiler.compile (withFinalNewline source) with
        | { Typed = Some typed; Diagnostics = [] } -> typed
        | result -> failwith $"""Compile error: {result.Diagnostics |> List.map _.Message |> String.concat "\n"}"""
    let options = { checkedOptions with Inline = inlining; YieldAtEveryWait = yielding }
    let wat = transpileTypedModuleWithOptions options typed |> fst
    match runCheckedHost [ "program" ] wat with
    | Ok (output, liveBlocks) -> output.ReplaceLineEndings "\n", liveBlocks
    | Error error -> failwith $"%A{error}"

/// Asserts that `source` prints `expected` and leaves nothing allocated, inlined and not, run straight through and
/// stopping at every wait.
let private assertRuns (expected: string) (source: string) =
    for inlining in [ true; false ] do
        for yielding in [ false; true ] do
            let output, liveBlocks = run inlining yielding source
            Assert.Equal(normalized expected, output)
            Assert.Equal(0, liveBlocks)

/// The functions the programs wait through: `call` calls the function it is given, which can wait.
let private waiting =
    """import nanyx/async as (Async)

def call: [Async] ([Async] () -> a) -> a = { f -> f() }
"""

[<Fact>]
let ``A function that waits carries on with its locals where it stopped`` () =
    assertRuns
        "hello world 3\n"
        (waiting
         + """
def greet: [Async] string -> string = { name ->
  def greeting = "hello"
  def count = [1, 2]
  def got = call({ name })
  "{greeting} {got} {list.length(count) + 1}"
}

println(greet("world"))""")

[<Fact>]
let ``A function that waits in each branch of a match and an if resumes in the branch it stopped in`` () =
    assertRuns
        "one\nmany: 2\nsmall\nbig\n"
        (waiting
         + """
def describe: [Async] list(int) -> string = { items ->
  match items
    | [ only ] -> call({ "one" })
    | many -> "many: {call({ list.length(many) })}"
}

def size: [Async] int -> string = { n ->
  if n < 10 then call({ "small" }) else call({ "big" })
}

println(describe([1]))
println(describe([1, 2]))
println(size(3))
println(size(30))""")

[<Fact>]
let ``A function that waits in a for loop resumes in the iteration it stopped in`` () =
    assertRuns
        ">a\n>b\n>c\n"
        (waiting
         + """
def show: [Async, Console] list(string) -> () = { names ->
  def prefix = ">"
  for names | name ->
    def got = call({ name })
    println("{prefix}{got}")
}

show(["a", "b", "c"])""")

[<Fact>]
let ``A function that waits in a memory block keeps its cells across the wait`` () =
    assertRuns
        "6\n"
        (waiting
         + """
def total: [Async] list(string) -> int = { names ->
  memory {
    mut sum = 0
    for names | name ->
      sum := sum + call({ string.length(name) })
    sum
  }
}

println(total(["ab", "cd", "ef"]))""")

[<Fact>]
let ``A function that waits calls itself, in a tail call and not`` () =
    assertRuns
        "55\n15\n"
        (waiting
         + """
def sumTo: [Async] int, int -> int = { n, acc ->
  if n == 0 then acc else sumTo(n - 1, acc + call({ n }))
}

def sumDown: [Async] int -> int = { n ->
  if n == 0 then 0 else call({ n }) + sumDown(n - 1)
}

println(sumTo(10, 0))
println(sumDown(5))""")

[<Fact>]
let ``A call that waits among a call's arguments keeps the arguments before it`` () =
    assertRuns
        "x-1-y\n"
        (waiting
         + """
def join3: string, string, string -> string = { a, b, c -> "{a}-{b}-{c}" }

def build: [Async] () -> string = { _ -> join3("x", call({ "1" }), call({ "y" })) }

println(build())""")

[<Fact>]
let ``list.map with a lambda that waits gives up each cell of the list it maps`` () =
    assertRuns
        "[#1, #2, #3]\n"
        (waiting
         + """
def labels: [Async] list(int) -> list(string) = { items -> items \list.map { n -> call({ "#{n}" }) } }

println(labels([1, 2, 3]))""")

[<Fact>]
let ``A call that waits in an interpolated string's hole keeps the holes before it`` () =
    assertRuns
        "Hello, Ada and Bo!\n[Hi, a, Hi, b]\n"
        (waiting
         + """
def named: [Async] string -> string = { name -> call({ name }) }

def greet: [Async] () -> string = { _ -> "Hello, {named("Ada")} and {named("Bo")}!" }

println(greet())
println(["a", "b"] \list.map { name -> "Hi, {named(name)}" })""")

[<Fact>]
let ``A program that writes, reads and walks files gives the same when every wait stops`` () =
    use directory = new TempDirectory()
    let source =
        """::dependency nanyx.fs

import (
  nanyx.fs/file as file
  nanyx.fs/path as path
)

def copyMore = { from, into ->
  match file.read(path.of(from))
    | #some(text) -> file.write(path.of(into), "{text}\nfour") \ignore
    | _ -> println("unread")
}

file.write(path.of("a.txt"), "one\ntwo\nthree") \ignore
copyMore("a.txt", "b.txt")

match file.lines(path.of("b.txt"))
  | #some(lines) -> println(lines \seq.map { line -> "> {line}" } \seq.toList)
  | _ -> println("unread")
"""
    File.WriteAllText(Path.Combine(directory.Path, "main.nyx"), source)
    for options in [ []; [ "--yield-at-every-wait" ] ] do
        let outcome = nanyxIn directory.Path ([ "run"; "--check-memory" ] @ options @ [ "main.nyx" ])
        Assert.Equal("", outcome.StandardError.ReplaceLineEndings "\n")
        Assert.Equal("[> one, > two, > three, > four]\n", outcome.StandardOutput.ReplaceLineEndings "\n")
        Assert.Equal(0, outcome.ExitCode)
