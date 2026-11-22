module TranspilerWasmTests.Features.Memory.MutableCollections

open Xunit
open TranspilerWasmTestHelpers

// `MStack(a)` in `nanyx/mstack` and `MList(a)` in `nanyx/mlist`, written over `MRef`. The tests run with memory checks
// on, so an element a collection does not give up is reported.

[<Fact>]
let ``Run - a stack is pushed, popped and peeked, last in first out`` () =
    let source = """
import nanyx/mstack

@entry
def main = {
  def result = memory {
    def stack = MStack.empty()
    stack \push("a")
    stack \push("b")
    def popped = stack \pop
    def peeked = stack \peek
    def count = MStack.length(stack)
    def last = stack \pop
    def empty = stack \pop
    (popped, peeked, count, last, empty, MStack.isEmpty(stack), MStack.toList(MStack.fromList([1, 2])))
  }
  println(result)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "(#some(b), #some(a), 1, #some(a), #none, true, [1, 2])\n"

[<Fact>]
let ``Run - a reverse Polish calculator keeps its numbers on a stack`` () =
    let source = """
import nanyx/mstack
import nanyx/text

def apply: string, float, float -> #some(float) | #none = { op, a, b ->
  match op
    | "+" -> #some(a + b)
    | "-" -> #some(a - b)
    | "*" -> #some(a * b)
    | "/" -> #some(a / b)
    | _ -> #none
}

def evaluate: string -> #some(float) | #none = { text ->
  memory {
    def stack: MStack(float) = MStack.empty()
    mut ok = true
    for text \split(on = " ") | token ->
      match float.fromString(token)
        | #some(x) -> stack \push(x)
        | #invalidFloat ->
          def b = stack \pop
          def a = stack \pop
          match (a, b)
            | (#some(x), #some(y)) ->
              match apply(token, x, y)
                | #some(answer) -> stack \push(answer)
                | #none -> ok := false
            | _ -> ok := false
    if ok and MStack.length(stack) == 1 then stack \pop else #none
  }
}

@entry
def main = {
  println(evaluate("3 4 + 2 *"))
  println(evaluate("5 1 2 + 4 * + 3 -"))
  println(evaluate("1 +"))
  println(evaluate("2 3 ^"))
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "#some(14.0)\n#some(14.0)\n#none\n#none\n"

[<Fact>]
let ``Run - a mutable list grows at the end, and its elements are read and set by index`` () =
    let source = """
import nanyx/mlist

@entry
def main = {
  def result = memory {
    def items = MList.fromList(["a", "b"])
    items \add("c")
    def changed = items \set(1, "B")
    def missed = items \set(3, "d")
    (MList.toList(items), MList.length(items), items \at(0), items \at(2), items \at(3), items \at(-1), changed, missed)
  }
  println(result)
  def empty = memory {
    def items: MList(int) = MList.empty()
    (MList.length(items), MList.toList(items))
  }
  println(empty)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "([a, B, c], 3, #some(a), #some(c), #notFound, #notFound, #ok, #outOfRange)\n(0, [])\n"

[<Fact>]
let ``Run - += adds to a mutable list, and gives ()`` () =
    let source = """
import nanyx/mlist

def addAll: [Memory] MList(int), list(int) -> () = { items, xs ->
  list.fold(xs, (), { _, x -> items += x })
}

@entry
def main = {
  def result = memory {
    def items: MList(int) = [1]
    items += 2 + 3
    def added = items += 4
    addAll(items, [5, 6])
    (MList.toList(items), added)
  }
  println(result)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "([1, 5, 4, 5, 6], ())\n"
