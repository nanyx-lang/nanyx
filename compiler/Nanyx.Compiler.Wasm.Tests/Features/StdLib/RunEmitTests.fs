module TranspilerWasmTests.Features.StdLib.RunEmit

open Xunit
open TranspilerWasmTestHelpers

// The prelude's `Emit(a)`, whose `emit` pushes a value to the handler in scope (#2566). The builders run their block
// under a handler of their own, and a user's `use` handles it too. `yield` stays lazy: it makes a sequence.

let private ran (expected: string) (source: string) = runSource (normalized source) |> assertRan expected

[<Fact>]
let ``Run - a use of Emit handles what a function emits, through the functions it calls`` () =
    """type Tree = #leaf(int) | #node(Tree, Tree)

def walk: [Emit(int)] Tree -> () = {
  | #leaf(n) -> emit(n)
  | #node(left, right) ->
    walk(left)
    walk(right)
}

def visit: [c] Tree, ([c] int -> ()) -> () = { tree, f ->
  use Emit(int)(emit = { n -> f(n) }) in walk(tree)
}

@entry
def main = {
  def tree = #node(#leaf(1), #node(#leaf(2), #leaf(3)))
  use Emit(int)(emit = { n -> println("got {n}") }) in walk(tree)
  visit(tree, { n -> println(n * 10) })
  println(list.build { walk(tree) })
}
"""
    |> ran "got 1\ngot 2\ngot 3\n10\n20\n30\n[1, 2, 3]\n"

[<Fact>]
let ``Run - a builder runs its block now, emitting in order between the block's other effects`` () =
    """@entry
def main = {
  def xs = list.build {
    println("first")
    emit(1)
    println("second")
    emit(2)
  }
  println(xs)
}
"""
    |> ran "first\nsecond\n[1, 2]\n"

[<Fact>]
let ``Run - the code after a yield in a seq block runs only when the sequence is pulled that far`` () =
    """@entry
def main = {
  def s = seq {
    println("one")
    yield 1
    println("two")
    yield 2
  }
  println(s \seq.take(1) \seq.toList)
}
"""
    |> ran "one\n[1]\n"

[<Fact>]
let ``Check - a memory block giving back the MList an Emit handler adds to is NYX0233`` () =
    let source =
        normalized """import nanyx/mlist

def leak: () -> MList(int) = {
  memory {
    def items = MList.empty()
    use Emit(int)(emit = { x -> items += x }) in emit(1)
    items
  }
}
"""
    let outcome = checkScript source
    Assert.NotEqual(0, outcome.ExitCode)
    assertDiagnostic
        "NYX0233"
        "Scoped context escapes the call that supplies it"
        "main.nyx:6:33"
        "The result of this 'memory' call requires its 'Memory' through 'items'"
        outcome.StandardOutput
