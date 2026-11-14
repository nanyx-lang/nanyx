module TranspilerWasmTests.Features.Tags.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - an unannotated function whose branches return different tags`` () =
    let source =
        normalized """import nanyx/console

def getMessage = { flag ->
  def message = "Hello, from Nanyx!"
  if flag then #some(message)
  else #noMessage
}

def describe = { flag ->
  match getMessage(flag)
    | #some(_) -> println("There was a message")
    | #noMessage -> println("No message available")
}

describe(true)
describe(false)
"""
    runSource source |> assertRan "There was a message\nNo message available\n"

[<Fact>]
let ``Run - a piped lambda can match only the tags left of a union's rest`` () =
    // The rest `ω` is `#invalidInt` from the piped value, and the lambda's input is `#invalidInt` from its pattern:
    // one type, a tag alone and a union of one (#457).
    let source =
        normalized """import nanyx/console

def mapError = {
  | #some(x), _ -> #some(x)
  | other, f -> f(other)
}

def show: #some(int) | #bad(string) -> string = {
  | #some(n) -> "some {n}"
  | #bad(s) -> s
}

println(show(int.fromString("12") \mapError({ #invalidInt -> #bad("bad") })))
println(show(int.fromString("zz") \mapError({ #invalidInt -> #bad("bad") })))
"""
    runSource source |> assertRan "some 12\nbad\n"

[<Fact>]
let ``Run - a tag union rest passes the tags a match does not name through`` () =
    let source =
        normalized """import nanyx/console

def mapSome
  : (#some(a) | r), (a -> b) -> (#some(b) | r)
  = { | #some(a), f -> #some(f(a))
      | other, _ -> other }

def show: (#some(string) | #notFound | #error) -> string = { | #some(s) -> s
                                                       | #notFound -> "notFound"
                                                       | #error -> "error" }

@entry
def main = {
  def x: (#some(int) | #notFound | #error) = #some(20)
  def y: (#some(int) | #notFound | #error) = #error
  println(show(mapSome(x, { n -> "{n + 1}" })))
  println(show(mapSome(y, { n -> "{n + 1}" })))
}
"""
    runSource source |> assertRan "21\nerror\n"

[<Fact>]
let ``Run - interpolating tags without a payload`` () =
    let source =
        normalized """def flip = { | #on -> #off | #off -> #on }

@entry
def main = {
  def c = if 1 > 2 then #ok else #none
  println("{c} {flip(#on)} {#done}")
}
"""
    runSource source |> assertRan "#none #off #done\n"

[<Fact>]
let ``Run - interpolating a tag whose type names a tag no expression writes`` () =
    let source =
        normalized """import nanyx/console

def show: (#red | #green) -> string = { c -> "colour {c}" }

println(show(#red))
"""
    runSource source |> assertRan "colour #red\n"

[<Fact>]
let ``Run - a tag keeps a payload that does not fit in 16 bits`` () =
    let source =
        normalized """import nanyx/console

def show: [Console] (#some(int) | #none) -> () = {
  | #some(70000) -> println("seventy thousand")
  | #some(n) -> println("#some({n})")
  | #none -> println("#none")
}

show(#some(-17))
show(#some(70000))
show(#some(4464))
show(#none)
println("{#some(70000) == #some(70000)} {#some(70000) == #some(4464)} {#some(1) != #some(2)}")
dbg(#some(-17))
"""
    runSource source |> assertRan "#some(-17)\nseventy thousand\n#some(4464)\n#none\ntrue false true\ndbg: #some(-17)\n"

[<Fact>]
let ``Run - a tag's string payload allocated past 64 KB of heap`` () =
    let source =
        normalized """import nanyx/console

def grow: (string, int) -> string = { s, n -> if n == 0 then s else grow(s ++ "x", n - 1) }

def show: [Console] (#name(string) | #anon) -> () = {
  | #name(s) -> println("name {s}")
  | #anon -> println("anon")
}

@entry
def main = {
  -- About 80 KB of strings, so what comes next is allocated above 64 KB.
  def filler = grow("", 400)
  show(#name("Ada" ++ "!"))
  println("filler is empty: {filler == ""}")
}
"""
    runSource source |> assertRan "name Ada!\nfiller is empty: false\n"

[<Fact>]
let ``Run - a list literal of different tags, and branches whose tags hold different tags`` () =
    let source =
        """
import nanyx/console

type Color = #red | #green

def name: Color -> string = {
  | #red -> "red"
  | #green -> "green"
}

def names: list(Color) -> string = {
  | [] -> ""
  | [c, ...rest] -> name(c) ++ " " ++ names(rest)
}

def load = {
  | 0 -> #error(#notFound)
  | 2 -> #error(#denied)
  | n -> #some(n)
}

def describe = { n ->
  match load(n)
    | #error(#notFound) -> "not found"
    | #error(#denied) -> "denied"
    | #some(v) -> "some {v}"
}

println(names([#red, #green, #red]))
println(describe(0))
println(describe(1))
println(describe(2))
"""
    runSource source |> assertRan "red green red \nnot found\nsome 1\ndenied\n"
[<Fact>]
let ``Run - a union's rest bound to a type with a tag the union names`` () =
    // `bind` is `#some(a) | z, (a -> z) -> z`: at `x`, `z` is `#some(int)`, which the input's `#some(a)` names too, so
    // the input alone leaves `z` no tags (#1027).
    let source =
        """
import nanyx/console

def bind = {
  | #some(v), f -> f(v)
  | other, _ -> other
}

def x = bind(#some(2), { n -> #some(n * 10) })
def y = bind(#none, { n -> #some(n * 10) })
println("{x}")
println("{y}")
"""
    runSource source |> assertRan "#some(20)\n#none\n"
