module TranspilerWasmTests.Features.Names.Run

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - names in backticks are defined, called and read as any other name`` () =
    let source =
        normalized """def `add points`: (int, int) -> int = { a, b -> a + b }
def `is it empty?`: string -> bool = { s -> s == "" }
def `match` = 7

@entry
def main = {
  def person = (`first name` = "Ada", age = 36)
  println("{`add points`(1, 2)} {`is it empty?`("")} {`match`} {person.`first name`}")
}
"""
    runSource source |> assertRan "3 true 7 Ada\n"

[<Fact>]
let ``Run - names that differ only in characters an identifier cannot hold stay apart`` () =
    let source =
        normalized """def `add points`: (int, int) -> int = { a, b -> a + b }
def add_points: (int, int) -> int = { a, b -> a * b }
def `<+>`: (int, int) -> int = { a, b -> a - b }
def `<*>`: (int, int) -> int = { a, b -> b - a }

@entry
def main = {
  println("{`add points`(2, 5)} {add_points(2, 5)} {`<+>`(2, 5)} {`<*>`(2, 5)}")
}
"""
    runSource source |> assertRan "7 10 -3 3\n"

[<Fact>]
let ``Run - a parameter in an imported module keeps its value where it shares a name with a definition there`` () =
    let files =
        [ "limits.nyx",
          normalized """module limits

export def low: int, int -> int = { a, b -> if a <= b then a else b }

export def atLeast: (int, low: int) -> int = { n, low -> if n < low then low else n }
"""
          "main.nyx",
          "module main\n\nimport limits as limits\n\n@entry\ndef main = {\n  println(\"{limits.atLeast(1, low = 5)} {limits.low(1, 5)}\")\n}\n" ]
    let outcome, _ = nanyxOnPackage files [ "run"; "{package}" ]
    assertRan "5 1\n" outcome

[<Fact>]
let ``Run - a def in a lambda that shadows a name the lambda captures reads the captured name in its own value`` () =
    let source = """
def h: [Console] int -> () = { x ->
  def f = { ->
    def x = x + 1
    println(x)
  }
  f()
  rec count = { n ->
    def x = x + n
    if n == 0 then x else count(n - 1)
  }
  def g = { y ->
    def x = x * y
    println(x + count(1))
  }
  g(10)
}

@entry
def main = {
  h(1)
}
"""
    runSource source |> assertRan "2\n11\n"
