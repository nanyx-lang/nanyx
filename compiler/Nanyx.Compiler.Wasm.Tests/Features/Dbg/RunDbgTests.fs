module TranspilerWasmTests.Features.Dbg.Run

open Xunit
open Transpiler.Wasm.CodeGen
open TranspilerWasmTestHelpers

/// Runs `source` with its memory checked, and asserts its `dbg`s printed `expected`, to stderr, and it printed nothing
/// else. Both are normalised to `\n`, and the line break that starts `expected` is left out.
let private printsAs (expected: string) (source: string) =
    runSource (source.ReplaceLineEndings "\n") |> assertRanWithDbg "" (expected.ReplaceLineEndings("\n").TrimStart('\n'))

[<Fact>]
let ``Run - dbg as a pipe prints each kind of value`` () =
    """
"hi" \dbg
5 \dbg
#ok \dbg
#some(3) \dbg
"""
    |> printsAs """
[main.nyx:2] "hi"
[main.nyx:3] 5
[main.nyx:4] #ok
[main.nyx:5] #some(3)
"""

[<Fact>]
let ``Run - dbg piped from a call in a for loop`` () =
    """
def show = { i -> "{i}" }

for 1..3 | i ->
    show(i) \dbg
"""
    |> printsAs """
[main.nyx:5] "1"
[main.nyx:5] "2"
[main.nyx:5] "3"
"""

[<Fact>]
let ``Run - dbg prints ints, floats and bools as their literals`` () =
    """
def flag = 1 < 2
dbg(42)
dbg(-7)
dbg(1.5)
dbg(2.0)
dbg(flag)
dbg(false)
"""
    |> printsAs """
[main.nyx:3] 42
[main.nyx:4] -7
[main.nyx:5] 1.5
[main.nyx:6] 2.0
[main.nyx:7] true
[main.nyx:8] false
"""

[<Fact>]
let ``Run - dbg prints the bool a comparison gives`` () =
    """
type Point = (x: int, y: int)
def a = Point(x = 0, y = 0)
def b = Point(x = 0, y = 0)
dbg(a == b)
dbg(1 == 2)
"""
    |> printsAs "[main.nyx:5] true\n[main.nyx:6] false\n"

[<Fact>]
let ``Run - dbg prints a string quoted and escaped`` () =
    """
dbg("Ada")
dbg("say \"hi\"\n\ttab\\")
dbg("")
"""
    |> printsAs """
[main.nyx:2] "Ada"
[main.nyx:3] "say \"hi\"\n\ttab\\"
[main.nyx:4] ""
"""

[<Fact>]
let ``Run - dbg prints a tag with its payload`` () =
    """
def flag = 1 < 2
dbg(#some(5))
dbg(#some("hi"))
dbg(#some(flag))
dbg(if flag then #yes(false) else #no)
dbg(#pair(1, "a"))
dbg(#rect(width = 1.0, height = 2.0))
dbg(#done(()))
"""
    |> printsAs """
[main.nyx:3] #some(5)
[main.nyx:4] #some("hi")
[main.nyx:5] #some(true)
[main.nyx:6] #yes(false)
[main.nyx:7] #pair(1, "a")
[main.nyx:8] #rect(height = 2.0, width = 1.0)
[main.nyx:9] #done()
"""

[<Fact>]
let ``Run - dbg prints tuples, records and unit`` () =
    """
dbg((1, "a"))
dbg((name = "Ada", age = 36))
dbg(())
"""
    |> printsAs """
[main.nyx:2] (1, "a")
[main.nyx:3] (age = 36, name = "Ada")
[main.nyx:4] ()
"""

[<Fact>]
let ``Run - dbg prints lists, maps and sets`` () =
    """
def empty: list(int) = []
dbg([1, 2])
dbg(empty)
dbg(["tea" => 3, "cake" => 4])
def none: map(string, int) = map.empty
dbg(none)
dbg(set.fromList([3, 1, 3]))
def noInts: set(int) = set.empty
dbg(noInts)
"""
    |> printsAs """
[main.nyx:3] [1, 2]
[main.nyx:4] []
[main.nyx:5] Map["tea" => 3, "cake" => 4]
[main.nyx:7] Map[]
[main.nyx:8] Set[1, 3]
[main.nyx:10] Set[]
"""

[<Fact>]
let ``Run - dbg prints a nested value part by part`` () =
    """
dbg([#some((1, ["x"])), #none])
dbg((point = (x = 1.5, y = -2.0), tags = [#a, #b]))
"""
    |> printsAs """
[main.nyx:2] [#some(1, ["x"]), #none]
[main.nyx:3] (point = (x = 1.5, y = -2.0), tags = [#a, #b])
"""

[<Fact>]
let ``Run - dbg prints a value of a type that holds itself`` () =
    """
type Tree = #leaf | #node(Tree, int, Tree)
def tree: Tree = #node(#leaf, 1, #node(#leaf, 2, #leaf))
dbg(tree)
"""
    |> printsAs "[main.nyx:4] #node(#leaf, 1, #node(#leaf, 2, #leaf))\n"

[<Fact>]
let ``Run - dbg prints a nominal value by its name around its shape`` () =
    """
type Money = protected int
type Point = protected (x: int, y: int)
dbg(Money(250))
dbg(Point(x = 1, y = 2))
"""
    |> printsAs "[main.nyx:4] Money(250)\n[main.nyx:5] Point(x = 1, y = 2)\n"

[<Fact>]
let ``Run - dbg prints a protected value's shape outside its module`` () =
    """
module ids =
  export type UserId = private int
  export def make = { n: int -> UserId(n) }

dbg(ids.make(42))
"""
    |> printsAs "[main.nyx:6] ids.UserId(42)\n"

[<Fact>]
let ``Run - dbg ignores the ToString a type has`` () =
    """
type Money = protected int
def Money.toString: Money -> string = { m ->
  def Money(pence) = m
  "£{pence}"
}
def price = Money(250)
dbg(price)
dbg("{price}")
"""
    |> printsAs "[main.nyx:8] Money(250)\n[main.nyx:9] \"£250\"\n"

[<Fact>]
let ``Run - dbg prints a value of a generic function's type at the type it is called at`` () =
    """
def trace = { x -> dbg(x) }
trace((trace(1), trace([#ok])))
"""
    |> printsAs """
[main.nyx:2] 1
[main.nyx:2] [#ok]
[main.nyx:2] (1, [#ok])
"""

[<Fact>]
let ``Run - dbg prints a for loop's elements and a match's bindings`` () =
    """
for [[1], [2, 3]] | xs ->
  dbg(xs)
match #some((1, "x"))
  | #some(pair) -> dbg(pair)
"""
    |> printsAs """
[main.nyx:3] [1]
[main.nyx:3] [2, 3]
[main.nyx:5] (1, "x")
"""

[<Fact>]
let ``Run - dbg of a function says that it is one and of what type, and gives the function`` () =
    """
def increment = { n -> n + 1 }
dbg(increment)
def again = dbg({ n: int -> "{n * 2}" })
dbg(again(4))
"""
    |> printsAs """
[main.nyx:3] <function int -> int>
[main.nyx:4] <function int -> string>
[main.nyx:5] "8"
"""

[<Fact>]
let ``Run - dbg gives back the value it prints`` () =
    """
def total = dbg(3) + 1
dbg(total)
def names = dbg(["a", "b"])
dbg(names)
"""
    |> printsAs """
[main.nyx:2] 3
[main.nyx:3] 4
[main.nyx:4] ["a", "b"]
[main.nyx:5] ["a", "b"]
"""

[<Fact>]
let ``Run - dbg piped at the end of a pipeline written over several lines names the line of the pipe`` () =
    """
def doubled =
  [1, 2, 3]
    \list.map({ n -> n * 2 })
    \dbg
println(list.length(doubled))
"""
    |> normalized
    |> runSource
    |> assertRanWithDbg "3\n" "[main.nyx:5] [2, 4, 6]\n"

[<Fact>]
let ``Run - dbg in a package names its file by its path in the package`` () =
    let source = """
module main

@entry
def main = {
  dbg(42)
}
"""
    fst (nanyxOnPackage [ "main.nyx", normalized source ] [ "run"; "{package}" ]) |> assertRanWithDbg "" "[src/main.nyx:6] 42\n"

/// `source` compiled as a release build is, and run: what it printed.
let private releasePrints (expected: string) (source: string) =
    let wat = transpileWatWith { WasmOptions.Default with Release = true } (normalized source)
    Assert.DoesNotContain("$dbg_str", wat)
    match runHost [ "main" ] wat with
    | Ok output -> Assert.Equal(expected, output)
    | Error error -> failwith $"The program failed: {error}"

[<Fact>]
let ``Run - a release build strips dbg, which evaluates its argument once and prints nothing`` () =
    """
def say = { text: string ->
  println(text)
  text
}
def shown = dbg(say("once"))
println(shown)
"piped" \dbg \println
"""
    |> releasePrints "once\nonce\npiped\n"
[<Fact>]
let ``Run - dbg writes to stderr, in order with what the program writes there itself (#2058)`` () =
    """
dbg(1)
eprintln("between")
println("out")
dbg(2)
"""
    |> normalized
    |> runSource
    |> assertRanWithDbg "out\n" "[main.nyx:2] 1\nbetween\n[main.nyx:5] 2\n"

[<Fact>]
let ``A module the runtime runs gives what dbg prints to nanyx:debug first; one written to run elsewhere writes it itself (#2058)`` () =
    let source = normalized "dbg(1)\n"
    let runByRuntime = transpileWatWith { WasmOptions.Default with Places = true; Probes = true } source
    Assert.Contains("""(import "nanyx:debug" "dbg" """, runByRuntime)
    Assert.Contains("call $dbg_str", runByRuntime)
    let elsewhere = transpileWat source
    Assert.DoesNotContain("nanyx:debug", elsewhere)
    Assert.Contains("call $dbg_str", elsewhere)

[<Fact>]
let ``Run - dbg of a call whose union is still open prints its tags, as it does once the value is named (#2280)`` () =
    // `list.map`'s result leaves the rest of `allSome`'s union open, which adds no tags to print.
    """
dbg(["1", "2", "x"] \list.map(int.fromString) \allSome)
dbg(allSome(list.map(["1"], { s -> int.fromString(s) })))
def named = ["1", "2", "x"] \list.map(int.fromString) \allSome
dbg(named)
"""
    |> printsAs """
[main.nyx:2] #invalidInt
[main.nyx:3] #some([1])
[main.nyx:5] #invalidInt
"""

[<Fact>]
let ``Run - dbg of a value no printer can write yet is reported at the dbg (#2280)`` () =
    let source = normalized "type Point = (x: int, y: int)\n\ndbg(type Point)\n"
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("", outcome.StandardOutput)
    assertDiagnostic "NYX0301" "Not supported by the WebAssembly backend" ":3:1" "It can't compile `dbg` of a value of type 'Type(Point)' yet" outcome.StandardError
