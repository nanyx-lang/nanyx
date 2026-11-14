module TranspilerWasmTests.Features.Workflows.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a builder block that uses no workflow keyword runs as its block`` () =
    let source =
        normalized """import nanyx/console

def total = proc {
  def a = 20
  a + 22
}

@entry
def main = {
  proc {
    def names = ["Ada", "Grace"]
    for names | name -> println("Hello, " ++ name)
  }
  def three = proc { 1 + 2 }
  println("{total} {three}")
}
"""
    runSource source |> assertRan "Hello, Ada\nHello, Grace\n42 3\n"

[<Fact>]
let ``Run - workflow keywords in the branches of an if or match end or continue the whole block`` () =
    let source =
        normalized """import nanyx/console

def halve: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }

def check: int -> #some(int) | #negative | #odd = { n -> proc {
  if n < 0 then try #negative
  if n == 0 then return 0
  def half = try halve(n)
  return half
} }

def pick: [Console] string, int -> #some(string) | #odd | #unknown = { which, n -> proc {
  match which
    | "half" ->
      def h = try halve(n)
      if h > 2 then println("big half {h}") else try halve(h)
    | "same" -> println("kept {n}")
    | _ -> try #unknown
  return "done with {which}"
} }

def showCheck: #some(int) | #negative | #odd -> string = {
  | #some(v) -> "some({v})"
  | #negative -> "negative"
  | #odd -> "odd"
}

def showPick: #some(string) | #odd | #unknown -> string = {
  | #some(v) -> "some({v})"
  | #odd -> "odd"
  | #unknown -> "unknown"
}

@entry
def main = {
  println(showCheck(check(10)))
  println(showCheck(check(-4)))
  println(showCheck(check(0)))
  println(showCheck(check(7)))
  println(showPick(pick("half", 12)))
  println(showPick(pick("half", 4)))
  println(showPick(pick("half", 2)))
  println(showPick(pick("same", 9)))
  println(showPick(pick("other", 9)))
}
"""
    runSource source
    |> assertRan (
        withFinalNewline """some(5)
negative
some(0)
odd
big half 6
some(done with half)
some(done with half)
odd
kept 9
some(done with same)
unknown""")

[<Fact>]
let ``Run - an if or match with a keyword in a branch, nested in a statement, ends or continues the whole block`` () =
    // The value of a destructuring def, and an operand of `+`, are not statements of their own.
    let source = """
import nanyx/console

def halve: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }

def pair: int -> #some(int) = { n -> proc {
  def (a, b) = if n < 0 then return 0 else (n, n * 2)
  return a + b
} }

def plusHalf: int -> #some(int) | #odd = { n -> proc {
  def m = 1 + (match n
    | 0 -> return 100
    | _ -> try halve(n))
  return m
} }

def show: #some(int) | #odd -> string = {
  | #some(v) -> "some({v})"
  | #odd -> "odd"
}

println("{pair(3)} {pair(-1)}")
println("{show(plusHalf(4))} {show(plusHalf(0))} {show(plusHalf(3))}")
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "#some(9) #some(0)\nsome(3) some(100) odd\n"

[<Fact>]
let ``Run - the branches' values that go on to the rest of the block are joined, as an except's are (#348)`` () =
    // `except #error(#notFound) -> return 0` passes `#some(n)` on and returns from the `#error`: the value the rest of
    // the block gets is the union of what each branch gives, not the first branch's type.
    let source = """
import nanyx/console

def f: int -> #some(int) | #error(#notFound) = { n -> if n > 0 then #some(n) else #error(#notFound) }

def destructured = { n -> proc {
  def #some(result) = f(n) except #error(#notFound) -> return 0
  return result
} }

def matched = { n -> proc {
  def r = f(n) except #error(#notFound) -> return 0
  match r
    | #some(m) -> return m
} }

def sized = { n -> proc {
  def size = match n
    | 0 -> return "none"
    | 1 -> #one
    | _ -> #many(n)
  match size
    | #one -> return "one"
    | #many(k) -> return "{k}"
} }

println("{destructured(5)} {destructured(-1)} {matched(7)} {matched(-1)}")
println("{sized(0)} {sized(1)} {sized(3)}")
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "#some(5) #some(0) #some(7) #some(0)\n#some(none) #some(one) #some(3)\n"

[<Fact>]
let ``Run - a proc block continues past each #some and stops at the first #error`` () =
    // `run(7)` fails at its first `try`, so nothing after it runs; `run(3)` fails at its second. `sum` binds two
    // `try`s nested in its `return`.
    let source =
        normalized """import nanyx/console
import nanyx/result

def f: int -> #some(int) | #error(#tooBig) = { n -> if n > 5 then #error(#tooBig) else #some(n) }
def g: int -> #some(string) | #error(#tooBig) = { n -> if n == 3 then #error(#tooBig) else #some("n = {n}") }

def run: int -> #some(string) | #error(#tooBig) = { x -> proc {
  def a = try f(x)
  dbg(a)
  def s = try g(a)
  return s
} }

def add: (int, int) -> int = { a, b -> a + b }
def sum: int -> #some(int) | #error(#tooBig) = { x -> proc {
  return add(try f(x), try f(x + 1))
} }

def show: [Console] (#some(string) | #error(#tooBig)) -> () = {
  | #some(s) -> println(s)
  | #error(_) -> println("error")
}

def showInt: [Console] (#some(int) | #error(#tooBig)) -> () = {
  | #some(n) -> println("sum = {n}")
  | #error(_) -> println("error")
}

show(run(1))
show(run(3))
show(run(7))
showInt(sum(1))
showInt(sum(5))
"""
    runSource source |> assertRan "dbg: 1\nn = 1\ndbg: 3\nerror\nerror\nsum = 3\nerror\n"

[<Fact>]
let ``Run - a proc block gives back whichever failure stopped it`` () =
    let source =
        normalized """import nanyx/console
import nanyx/result

def name: int -> #some(string) | #nil = { n -> if n > 5 then #nil else #some("n{n}") }
def find: string -> #some(int) | #notFound = { s -> if s == "n3" then #notFound else #some(7) }

def run = { x -> proc {
  def s = try name(x)
  def i = try find(s)
  return i
} }

def show: [Console] (#some(int) | #nil | #notFound) -> () = {
  | #some(_) -> println("found")
  | #nil -> println("nil")
  | #notFound -> println("not found")
}

show(run(1))
show(run(3))
show(run(7))
"""
    runSource source |> assertRan "found\nnot found\nnil\n"

[<Fact>]
let ``Run - the prelude's proc, Result and Option helpers need no import`` () =
    let source =
        normalized """import nanyx/console

def half: int -> Result(int, string) = { n -> if n % 2 == 0 then #some(n / 2) else #error("odd") }
def quarter = { n -> proc {
  def h = try half(n)
  return try half(h)
} }

def show: [Console] Result(int, string) -> () = {
  | #some(n) -> println("some {n}")
  | #error(e) -> println("error {e}")
}

show(quarter(8))
show(quarter(6) \ Result.mapError({ e -> "ERR: " ++ e }))
show(half(4) \ map({ n -> n * 10 }))
def fallback = Result.defaultValue(half(3), 0)
println("{fallback}")
def name: #some(string) | #empty = #some("abc")
def shouted = name \ mapSome({ s -> s ++ "!" })
match shouted
  | #some(s) -> println(s)
  | #empty -> println("empty")
def seven = someOr(#none, 7)
def three = mapSomeOr(name, 0, { _s -> 3 })
println("{seven} {three}")
"""
    runSource source |> assertRan "some 2\nerror ERR: odd\nsome 20\n0\nabc!\n7 3\n"

[<Fact>]
let ``Run - a proc block that ends without return runs for what it does and is #some()`` () =
    let source =
        normalized """import nanyx/console

def halve: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }

def show: [Console] int -> #some() | #odd = { n -> proc {
  def h = try halve(n)
  println("half of {n} is {h}")
} }

@entry
def main = {
  match show(10)
    | #some() -> println("shown")
    | #odd -> println("odd")
  match show(3)
    | #some() -> println("shown")
    | #odd -> println("odd")
}
"""
    runSource source |> assertRan "half of 10 is 5\nshown\nodd\n"
