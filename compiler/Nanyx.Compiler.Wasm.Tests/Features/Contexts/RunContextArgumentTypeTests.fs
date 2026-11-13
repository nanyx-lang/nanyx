/// A function can require a context applied to any type its annotation writes, not only a primitive or one of its type
/// variables: a type the module defines, an applied type, or a structural one (#998, #884).
module TranspilerWasmTests.Features.Contexts.ContextArgumentTypes

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a function that requires a context applied to a type the module defines`` () =
    let source = """import nanyx/console

context ToText(a) = (textlen: a -> int)

type Word = (text: string)

def Word.textlen: Word -> int = { w -> 7 }

def viaCall: [ToText(Word)] Word -> string = { w -> "{ToText(Word).textlen(w)}" }

@entry
def main = {
  println(viaCall(Word(text = "hi")))
}
"""
    runSource source |> assertRan "7\n"

[<Fact>]
let ``Run - a function that requires a context applied to an applied type`` () =
    let source = """import nanyx/console

context Size(a) = (size: a -> int)

def list.size: list(int) -> int = { xs -> 3 }

def measure: [Size(list(int))] list(int) -> string = { xs -> "{Size(list(int)).size(xs)}" }

@entry
def main = {
  println(measure([1, 2]))
}
"""
    runSource source |> assertRan "3\n"

[<Fact>]
let ``Run - a function that requires a context applied to a tag union`` () =
    let source = """import nanyx/console

def f: [ToString(#x(a) | #y)] (#x(a) | #y) -> string = { v -> "{v}" }

@entry
def main = {
  println(f(#x(1)))
}
"""
    runSource source |> assertRan "#x(1)\n"

[<Fact>]
let ``Run - a function that requires one context applied to two structural types`` () =
    // Each instance's members are known by a key of their own, not both by `ToString_unknown_toString`.
    let source = """import nanyx/console

def f: [ToString(#x(a) | #y) & ToString((int, bool))] (#x(a) | #y), (int, bool) -> string = { v, p -> "{v} {p}" }

@entry
def main = {
  println(f(#x(1), (1, true)))
}
"""
    runSource source |> assertRan "#x(1) (1, true)\n"
