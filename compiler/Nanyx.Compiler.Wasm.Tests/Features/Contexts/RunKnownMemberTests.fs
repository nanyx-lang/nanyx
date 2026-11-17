/// A function that requires contexts is specialised on the members known where it is called: it calls a known member
/// directly, passes it on as known, and is not passed it (#744, see "Specialising on known members" in
/// `docs/src/content/compiler-internals/context-calls.md`).
module TranspilerWasmTests.Features.Contexts.KnownMembers

open System
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``A function that passes a known member on passes it as known`` () =
    let source = """context Show(a) = (show: a -> string)

type Point = (x: int, y: int)

def Point.show: Point -> string = { p -> "({p.x}, {p.y})" }

def describe: [Show(a)] a -> string = { value -> "<" ++ show(value) ++ ">" }

def label: [Show(a)] a -> string = { value -> "point " ++ describe(value) }

println(label(Point(x = 1, y = 2)))
"""
    let wat = transpileWatNotInlined source
    // The instance built from `Point.show` is known where `label` is called, and `label` passes it on as known to
    // `describe`, which calls it directly: neither is passed the member, and neither makes a closure.
    let label = functionText "label__rec_x_int_y_int__known" wat
    Assert.StartsWith("""(func $label__rec_x_int_y_int__known (param $value i32) (result i32)""", label)
    Assert.Contains("call $describe__rec_x_int_y_int__known", label)
    Assert.Contains("call $Point_show", functionText "describe__rec_x_int_y_int__known" wat)
    Assert.DoesNotContain("__fn_Point_show", wat)
    runSource source |> assertRan "point <(1, 2)>\n"

[<Fact>]
let ``A definition called with two instances has a specialisation for each`` () =
    let source = """context Show(a) = (show: a -> string)

type Point = (x: int, y: int)

type Size = (width: int, height: int)

def Point.show: Point -> string = { p -> "point {p.x},{p.y}" }

def Size.show: Size -> string = { s -> "size {s.width}x{s.height}" }

def describe: [Show(a)] a -> string = { value -> "<" ++ show(value) ++ ">" }

def main = {
  println(describe(Point(x = 1, y = 2)))
  println(describe(Size(width = 3, height = 4)))
}

main()
"""
    let wat = transpileWatNotInlined source
    Assert.Contains("""call $Point_show""", wat)
    Assert.Contains("""call $Size_show""", wat)
    Assert.Equal(2, countOccurrences "(func $describe__" wat)
    // Each specialisation calls its instance's member directly.
    for specialisation in [ "describe__rec_x_int_y_int__known"; "describe__rec_height_int_width_int__known" ] do
        Assert.DoesNotContain("call_indirect", functionText specialisation wat)
    runSource source |> assertRan "<point 1,2>\n<size 3x4>\n"

[<Fact>]
let ``A known member used as a value is made where it is used`` () =
    let source = """context Show(a) = (show: a -> string)

type Point = (x: int, y: int)

def Point.show: Point -> string = { p -> "({p.x}, {p.y})" }

def showAll: [Show(a)] list(a) -> list(string) = { values -> values \list.map(show) }

def main = {
  for showAll([Point(x = 1, y = 2), Point(x = 3, y = 4)]) | s -> println(s)
}

main()
"""
    let wat = transpileWatNotInlined source
    // `showAll` is not passed the member, but makes the closure over it that `list.map` takes.
    Assert.Contains("""(func $showAll__rec_x_int_y_int__known (param $values i32) (result i32)""", wat)
    Assert.Contains("__fn_Point_show", wat)
    runSource source |> assertRan "(1, 2)\n(3, 4)\n"

[<Fact>]
let ``A use of a lambda that captures a local is still passed as a closure`` () =
    let source = """context Greet = (greet: string -> string)

def shout: [Greet] string -> string = { name -> greet(name) ++ "!" }

def main = {
  def suffix = "?"
  use Greet(greet = { s -> s ++ suffix })
  println(shout("a"))
}

main()
"""
    let wat = transpileWatNotInlined source
    Assert.Contains("""(func $shout (param $name i32) (param $__ctx_greet i32) (result i32)""", wat)
    Assert.Contains("call_indirect", wat)
    runSource source |> assertRan "a?!\n"

[<Fact>]
let ``A use of a top-level function and a constant makes both members known`` () =
    let source =
        normalized """context Counter = (
  bump: int -> int
  limit: int
)

def stepOnce: [Counter] int -> int = { n ->
  bump(n) % limit
}

def run: [Counter] int, int -> int = { n, acc ->
  if n == 0 then acc else run(n - 1, stepOnce(acc + n))
}

def addThree: int -> int = { n -> n + 3 }

@entry
def main = {
  println(use Counter(bump = addThree, limit = 1000003) in run(2000, 0))
}
"""
    let wat = transpileWatNotInlined source
    // `run` and `stepOnce` are specialised on both members (#1299): `addThree` is called directly and the limit is a
    // constant, so neither is passed a member.
    Assert.Contains("""(func $run__known (param $n_i64 i64) (param $acc_i64 i64) (result i64)""", wat)
    let stepOnce = functionText "stepOnce__known" wat
    Assert.Contains("call $addThree", stepOnce)
    Assert.Contains("i64.const 1000003", stepOnce)
    Assert.DoesNotContain("call_indirect", stepOnce)
    Assert.DoesNotContain("call_indirect", functionText "run__known" wat)
    runSource source |> assertRan "6994\n"

[<Fact>]
let ``Two uses with different constants have a specialisation each`` () =
    let source =
        normalized """context Limit = (limit: int)

def clamp: [Limit] int -> int = { n -> if n > limit then limit else n }

def main = {
  println(use Limit(limit = 5) in clamp(9))
  println(use Limit(limit = 50) in clamp(9))
  def ten = 10
  println(use Limit(limit = ten) in clamp(90))
}

main()
"""
    let wat = transpileWatNotInlined source
    Assert.Equal(3, countOccurrences "(func $clamp__known" wat)
    runSource source |> assertRan "5\n9\n10\n"
[<Fact>]
let ``A use of a cell's value passes the value the cell holds then`` () =
    let source =
        normalized """context Limit = (limit: int)

def clamp: [Limit] int -> int = { n -> if n > limit then limit else n }

def main = {
  memory {
    mut cap = 5
    cap := 7
    println(use Limit(limit = cap) in clamp(9))
    cap := 8
    println(use Limit(limit = cap) in clamp(9))
  }
}

main()
"""
    runSource source |> assertRan "7\n8\n"
