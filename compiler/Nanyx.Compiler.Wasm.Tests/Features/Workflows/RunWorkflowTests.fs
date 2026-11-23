module TranspilerWasmTests.Features.Workflows.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a builder block that uses no workflow keyword is an ordinary call of the builder`` () =
    let source =
        normalized """def total = proc {
  def a = 20
  #some(a + 22)
}

@entry
def main = {
  def greeted = proc {
    def names = ["Ada", "Grace"]
    for names | name -> println("Hello, " ++ name)
    #some(list.length(names))
  }
  println("{total} {greeted}")
}
"""
    runSource source |> assertRan "Hello, Ada\nHello, Grace\n#some(42) #some(2)\n"

[<Fact>]
let ``Run - a builder takes parameters before its block`` () =
    // `retrying(3) { ... }` is `retrying(3, { ... })`: the builder runs the block up to three times, under its context.
    let source =
        normalized """context Retry = (
  try: [c] ((#some(a) | #failed), ([c] a -> #some(b) | #failed)) -> #some(b) | #failed
  return: a -> #some(a) | #failed
)

def retrying: [c] int, ([Retry, c] () -> #some(b) | #failed) -> #some(b) | #failed = { times, block ->
  use Retry(
    try = {
      | #some(value), continue -> continue(value)
      | #failed, _ -> #failed
    }
    return = { value -> #some(value) }
  ) in (
    match block()
      | #some(value) -> #some(value)
      | #failed -> (if times > 1 then retrying(times - 1, block) else #failed)
  )
}

def flaky: [Console] int -> #some(int) | #failed = { n ->
  println("trying {n}")
  if n > 0 then #failed else #some(n)
}

@entry
def main = {
  def result = retrying(2) {
    def x = try flaky(1)
    return x
  }
  def other = retrying(3) {
    def x = try flaky(0)
    return x + 5
  }
  println("{result} {other}")
}
"""
    runSource source |> assertRan "trying 1\ntrying 1\ntrying 0\n#failed #some(5)\n"

[<Fact>]
let ``Run - a builder may require contexts of its own`` () =
    // `logged` requires `Log`, which its caller supplies, and the block's keywords call `Steps`'s members.
    let source =
        normalized """import nanyx/text

context Log = (log: [Console] string -> ())

context Steps = (
  step: [c] (string, ([c] string -> int)) -> int
  return: int -> int
)

def logged: [c, Log, Console] ([Steps, c] () -> int) -> int = { block ->
  log("start")
  use Steps(
    step = { name, continue ->
      log("step " ++ name)
      continue(name)
    }
    return = { value -> value }
  ) in block()
}

@entry
def main = {
  use Log(log = { message -> println(message) })
  def total = logged {
    def a = step! "one"
    def b = step! "three"
    return string.length(a) + string.length(b)
  }
  println("{total}")
}
"""
    runSource source |> assertRan "start\nstep one\nstep three\n8\n"

[<Fact>]
let ``Run - workflow keywords in the branches of an if or match end or continue the whole block`` () =
    let source =
        normalized """def halve: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }

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
        normalized """def f: int -> #some(int) | #error(#tooBig) = { n -> if n > 5 then #error(#tooBig) else #some(n) }
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
    runSource source |> withoutDbgLocations |> assertRan "n = 1\nerror\nerror\nsum = 3\nerror\n1\n3\n"

[<Fact>]
let ``Run - a proc block gives back whichever failure stopped it`` () =
    let source =
        normalized """def name: int -> #some(string) | #nil = { n -> if n > 5 then #nil else #some("n{n}") }
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
let ``Run - the prelude's proc and option helpers need no import`` () =
    let source =
        normalized """def half: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }
def quarter = { n -> proc {
  def h = try half(n)
  return try half(h)
} }

def show: [Console] (#some(int) | #odd) -> () = {
  | #some(n) -> println("some {n}")
  | #odd -> println("odd")
}

show(quarter(8))
show(quarter(6))
show(half(4) \ mapSome({ n -> n * 10 }))
def fallback = someOr(half(3), 0)
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
    runSource source |> assertRan "some 2\nodd\nsome 20\n0\nabc!\n7 3\n"

[<Fact>]
let ``Run - a proc block that ends without return runs for what it does and is #some()`` () =
    let source =
        normalized """def halve: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }

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

[<Fact>]
let ``Run - a builder imported from another module gives its keywords their own types, at each use`` () =
    // The context's members are numbered by the module that declares it; here they are fresh, so `try` binds an `int`
    // and then a `string`, and isn't the prelude's `try` for results.
    let lib =
        """module box

export type Box(a) = private (v: a)

export def box: a -> Box(a) = { x -> Box(v = x) }

export def unbox: Box(a) -> a = { b -> b.v }

export context Boxed = (
  try: [c] (Box(a), ([c] a -> Box(b))) -> Box(b)
  return: a -> Box(a)
)

export def boxed: [c] ([Boxed, c] () -> Box(b)) -> Box(b) = { block ->
  use Boxed(try = { b, continue -> continue(b.v) }, return = { x -> Box(v = x) }) in block()
}
"""
    let main =
        """import box

def r = boxed {
  def n = try box(1)
  def s = try box("a")
  return "{s}{n}"
}

println(unbox(r))
"""
    use directory = new TempDirectory()
    File.WriteAllText(Path.Combine(directory.Path, "box.nyx"), lib.ReplaceLineEndings "\n")
    let mainPath = Path.Combine(directory.Path, "main.nyx")
    File.WriteAllText(mainPath, main.ReplaceLineEndings "\n")
    runFile mainPath |> assertRan "a1\n"

[<Fact>]
let ``Run - a try in each argument of a call binds it, on one line or one per line`` () =
    // `try` in an argument ends at its `,` or line break. A block written on one line, `proc { return ... }`, has the
    // rest of the block after its keywords as one on several lines does (#2411).
    let source =
        normalized """type P = (a: int, b: int)

def num = { x: int -> if x > 0 then #some(x) else #none }

def oneLine = { x: int, y: int -> proc { return P(a = try num(x), b = try num(y)) } }

def onePerLine = { x: int, y: int ->
  proc {
    return P(
      a = try num(x)
      b = try num(y)
    )
  }
}

def positional = { x: int -> proc { return list.length([try num(x), try num(x + 1)]) } }

@entry
def main = {
  println("{oneLine(1, 2)} {oneLine(1, -2)} {oneLine(-1, 2)}")
  println("{onePerLine(1, 2)} {onePerLine(-1, 2)}")
  println("{positional(1)} {positional(-1)}")
}
"""
    runSource source |> assertRan "#some((a = 1, b = 2)) #none #none\n#some((a = 1, b = 2)) #none\n#some(2) #none\n"
