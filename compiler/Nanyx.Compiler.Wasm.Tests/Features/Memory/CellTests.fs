module TranspilerWasmTests.Features.Memory.Cells

open Xunit
open Transpiler.Wasm.CoreIr
open TranspilerWasmTestHelpers

// `memory { ... }` and its cells: one no lambda captures is a local, and one a lambda captures a box they share.

[<Fact>]
let ``Run - a cell is summed in a loop, and changed in an if, a match and a nested loop`` () =
    let source = """
import nanyx/console

def sum: list(int) -> int = { xs ->
  memory {
    def total = mut 0
    for xs | x -> total := total + x
    total
  }
}

def counts = { xs ->
  memory {
    def evens = mut 0
    def odds = mut 0
    for xs | x ->
      if x % 2 == 0 then evens := evens + 1 else odds := odds + 1
    (evens, odds)
  }
}

def longest: list(string) -> string = { names ->
  memory {
    def best = mut ""
    for names | name ->
      match name.length > best.length
        | true -> best := name
        | false -> ()
    best
  }
}

def pairs = { n ->
  memory {
    def found = mut 0
    for 1..n | i ->
      for 1..n | j -> if i < j then found := found + 1 else ()
    found
  }
}

@entry
def main = {
  println(sum([1, 2, 3, 4]))
  def (evens, odds) = counts([1, 2, 3, 4, 5])
  println("{evens} {odds}")
  println(longest(["ab", "abcd", "abc"]))
  println(pairs(4))
}
"""
    runSource source |> assertRan "10\n2 3\nabcd\n6\n"

[<Fact>]
let ``Run - a cell read into a def keeps the value it had`` () =
    let source = """
import nanyx/console

@entry
def main = {
  def (before, after) = memory {
    def x = mut "one"
    def kept = x
    x := "two"
    (kept, x)
  }
  println("{before} {after}")
}
"""
    runSource source |> assertRan "one two\n"

[<Fact>]
let ``Run - lambdas that capture a cell share it`` () =
    let source = """
import nanyx/console

def tally = { words ->
  memory {
    def seen = mut []
    def count = mut 0
    def note = { word ->
      seen := seen ++ [word]
      count := count + 1
    }
    for words | word -> note(word)
    note("end")
    "{count}: {seen}"
  }
}

@entry
def main = {
  println(tally(["a", "b"]))
}
"""
    runSource source |> assertRan "3: [a, b, end]\n"

[<Fact>]
let ``Counting - a captured cell's box is freed with what it holds`` () =
    let source = """
def build: int -> list(string) = { n ->
  memory {
    def names = mut []
    def add = { s -> names := names ++ [s] }
    add("a {n}")
    add("b {n}")
    names
  }
}
"""
    use program = instantiateWasm source [ IrList IrString ]
    program.Drop(IrList IrString, program.Call("build", 20))
    Assert.True(program.Allocations > 0, "nothing was allocated")
    Assert.Equal(0, program.LiveBlocks)

[<Fact>]
let ``Counting - a cell assigned in an arm of a match on it frees what it held`` () =
    let source = """
def build: int -> list(string) = { n ->
  memory {
    def names = mut ["a {n}", "b {n}", "c {n}"]
    match names
      | [_, ...rest] -> names := rest
      | [] -> ()
    names
  }
}
"""
    use program = instantiateWasm source [ IrList IrString ]
    program.Drop(IrList IrString, program.Call("build", 20))
    Assert.Equal(0, program.LiveBlocks)

[<Fact>]
let ``Counting - a local cell drops each value it is given`` () =
    let source = """
def build: int -> string = { n ->
  memory {
    def text = mut ""
    for 1..n | i -> text := text ++ "{i}"
    text
  }
}
"""
    use program = instantiateWasm source [ IrString ]
    program.Drop(IrString, program.Call("build", 5))
    Assert.Equal(0, program.LiveBlocks)
