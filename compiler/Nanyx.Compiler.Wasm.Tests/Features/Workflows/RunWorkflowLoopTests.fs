/// A `for` loop whose body uses `try` or `return` inside `proc { ... }`, run by `proc`'s `for` member (#349).
module TranspilerWasmTests.Features.Workflows.RunLoop

open Xunit
open TranspilerWasmTestHelpers

let private parse =
    """def parse: string -> #some(int) | #error(string) = { s ->
  if s == "bad" then #error("bad input") else #some(s.length)
}

def show = {
  | #some(value) -> "some {value}"
  | #error(e) -> "error {e}"
}

def showDone = {
  | #some() -> "done"
  | #error(e) -> "error {e}"
}
"""

[<Fact>]
let ``Run - a failed try in a loop ends the block, skipping the items after it and what follows the loop`` () =
    let source =
        parse
        + """
def greetAll: [Console] list(string) -> #some() | #error(string) = { names -> proc {
  for names | name ->
    def n = try parse(name)
    println("{name} {n}")
  println("all greeted")
} }

@entry
def main = {
  println(showDone(greetAll(["ann", "bo"])))
  println(showDone(greetAll(["ann", "bad", "cy"])))
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "ann 3\nbo 2\nall greeted\ndone\nann 3\nerror bad input\n"

[<Fact>]
let ``Run - return in a loop ends the block with its value`` () =
    let source =
        parse
        + """
def firstLong: list(string) -> #some(string) | #error(string) = { names -> proc {
  for names | name ->
    if name.length > 3 then return name
  return "none"
} }

@entry
def main = {
  println(show(firstLong(["a", "abcd", "abcde"])))
  println(show(firstLong(["a"])))
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "some abcd\nsome none\n"

[<Fact>]
let ``Run - nested loops each go on to the rest of the one around them`` () =
    let source =
        parse
        + """
def check: int, int -> #some(int) | #error(string) = { x, y ->
  if x * y > 4 then #error("too big: {x} * {y}") else #some(x * y)
}

def grid: [Console] list(int) -> #some() | #error(string) = { xs -> proc {
  for xs | x ->
    for xs | y ->
      def p = try check(x, y)
      println("{x} {y} {p}")
    println("row {x}")
  println("end")
} }

@entry
def main = {
  println(showDone(grid([1, 2])))
  println(showDone(grid([1, 2, 3])))
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "1 1 1\n1 2 2\nrow 1\n2 1 2\n2 2 4\nrow 2\nend\ndone\n1 1 1\n1 2 2\n1 3 3\nrow 1\n2 1 2\n2 2 4\nerror too big: 2 * 3\n"

[<Fact>]
let ``Run - a loop over a million items runs in constant stack`` () =
    // Each body gives back `#__nextItem` and returns before `procFor` goes on to the next item, so no frame is kept for
    // an item, though the body's results are converted to and from the boxed ones of `for`'s type (#2683).
    let source =
        """def check: int -> #some(int) | #error(string) = { n ->
  if n < 0 then #error("negative") else #some(n)
}

def largest: list(int) -> #some(int) | #error(string) = { items -> proc {
  for items | item ->
    def n = try check(item)
    if n > 2_000_000 then return n

  return 0
}}

println(largest((0..1_000_000) \seq.toList))
"""
    runSource source |> assertRan "#some(0)\n"

[<Fact>]
let ``Run - a builder's own for decides what each item's result does`` () =
    // A `for` in the shape the desugared loop calls: the body gives back `#__nextItem` where it ends, a failure where a
    // `try` fails, or `#some` where it returns. This one goes on past a failure instead of ending the block.
    let source =
        parse
        + """
context Lenient = (
  try: [c] ((#some(a) | r), ([c] a -> #some(b) | r)) -> #some(b) | r
  zero: #some() | r
  for: [d] (list(x), ([d] x -> #__nextItem | #some(y) | s), ([d] () -> #some(y) | s)) -> #some(y) | s
)

def lenientFor
  : [c] list(a), ([c] a -> #__nextItem | #some(b) | r), ([c] () -> #some(b) | r) -> #some(b) | r
  = {
    | [], _, rest -> rest()
    | [item, ...others], body, rest ->
      match body(item)
        | #some(value) -> #some(value)
        | _ -> lenientFor(others, body, rest)
  }

def lenient: [c] ([Lenient, c] () -> #some(b) | r) -> #some(b) | r = { block ->
  use Lenient(
    try = {
      | #some(value), continue -> continue(value)
      | failure, _ -> failure
    }
    zero = #some()
    for = { items, body, rest -> lenientFor(items, body, rest) }
  ) in block()
}

def printAll: [Console] list(string) -> #some() | #error(string) = { names -> lenient {
  for names | name ->
    def n = try parse(name)
    println("{name}: {n}")
}}

println(showDone(printAll(["a", "bad", "abc"])))
"""
    runSource source |> assertRan "a: 1\nabc: 3\ndone\n"
