/// A `for` loop whose body uses `try` or `return` inside `proc { ... }`, run by `proc`'s `for` member (#349).
module TranspilerWasmTests.Features.Workflows.RunLoop

open Xunit
open TranspilerWasmTestHelpers

let private parse =
    """import nanyx/console

def parse: string -> #some(int) | #error(string) = { s ->
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
