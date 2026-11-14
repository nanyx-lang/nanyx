module TranspilerWasmTests.Features.Contexts.RunGenericContextMember

open Xunit
open TranspilerWasmTestHelpers

// A context member generic in type variables of its own has its value made once, where its instance is, with those
// values boxed, and each call boxes what it passes and unboxes what it is given back (#1160). `runSource` checks
// that every box is freed.

/// A program whose context `Proc` has the member types `members`, which a function loads with `use` and a block calls
/// through it, as a workflow builder's are (#1152).
let private procProgram (members: string) =
    """import nanyx/console

context Proc = (
MEMBERS
)

def procH: [c] ([c] () -> #some(int) | #error(string)) -> #some(int) | #error(string) = { f ->
  use Proc(
    bind = {
      | #some(value), continue -> continue(value)
      | failure, _ -> failure
    }
    ret = { value -> #some(value) }
  ) in f()
}

def parse: int -> #some(int) | #error(string) = { n -> if n % 1000 == 999 then #error("bad") else #some(n % 97) }

def step: [Proc] int -> #some(int) | #error(string) = { i ->
  procH({ bind(parse(i), { a -> ret(a + 1) }) })
}

@entry
def main = {
  use Proc(
    bind = { _, _ -> #error("outer") }
    ret = { _ -> #error("outer") }
  )
  match step(3)
    | #some(v) -> println("{v}")
    | #error(e) -> println(e)
}
"""
        .Replace("MEMBERS", members)
        .ReplaceLineEndings "\n"

let private genericProc = """context Proc = (
  bind: [c] ((#some(a) | r), ([c] a -> #some(b) | r)) -> #some(b) | r
  ret: a -> #some(a) | r
)
"""

[<Fact>]
let ``Run - a context member generic over its payload runs called at int`` () =
    procProgram
        """  bind: [c] ((#some(a) | r), ([c] a -> #some(b) | r)) -> #some(b) | r
  ret: a -> #some(a) | r"""
    |> runSource
    |> assertRan "4\n"

[<Fact>]
let ``Run - the same context member written at int runs`` () =
    procProgram
        """  bind: [c] ((#some(int) | #error(string)), ([c] int -> #some(int) | #error(string))) -> #some(int) | #error(string)
  ret: int -> #some(int) | #error(string)"""
    |> runSource
    |> assertRan "4\n"

[<Fact>]
let ``Run - a context member is called at a string payload and then an int one`` () =
    // #1304: each call has the member's own type variables at its own types.
    let source = "import nanyx/console\nimport nanyx/text\n\n" + genericProc + """
def name: int -> #some(string) | #missing = { i -> if i < 0 then #missing else #some("item {i}") }
def size: string -> #some(int) | #missing = { s -> #some(string.length(s)) }

def lookup: [Proc] int -> #some(int) | #missing = { i ->
  bind(name(i), { s -> bind(size(s), { n -> ret(n) }) })
}

def display: [Proc & Console] int -> () = { i ->
  match lookup(i)
    | #some(n) -> println("{n}")
    | #missing -> println("missing")
}

@entry
def main = {
  use Proc(
    bind = {
      | #some(value), continue -> continue(value)
      | failure, _ -> failure
    }
    ret = { value -> #some(value) }
  )
  display(3)
  display(-1)
  display(42)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "6\nmissing\n7\n"

[<Fact>]
let ``Run - a context member's boxed values may be records, strings and unit`` () =
    let source = "import nanyx/console\n\n" + genericProc + """
def pair: [Proc] string -> #some((name: string, size: int)) | #missing = { s ->
  bind(ret(s), { t -> bind(ret(()), { _ -> ret((name = t, size = 2)) }) })
}

@entry
def main = {
  use Proc(
    bind = {
      | #some(value), continue -> continue(value)
      | failure, _ -> failure
    }
    ret = { value -> #some(value) }
  )
  match pair("ab")
    | #some(p) -> println("{p.name} {p.size}")
    | #missing -> println("missing")
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "ab 2\n"

[<Fact>]
let ``Run - a context member's boxed values may be lists, converted element by element`` () =
    let source = """import nanyx/console

context Lists = (
  reversed: list(a) -> list(a)
  firstOr: (list(a), a) -> a
)

def describe: [Lists & Console] () -> () = {
  def numbers = reversed([1, 2, 3])
  def words = reversed(["x", "y"])
  println("{firstOr(numbers, 0)} {firstOr(words, "none")} {firstOr(reversed([]), 7)}")
}

@entry
def main = {
  use Lists(
    reversed = { xs -> xs \list.reverse }
    firstOr = {
      | [first, ..._], _ -> first
      | [], fallback -> fallback
    }
  )
  describe()
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "3 y 7\n"
