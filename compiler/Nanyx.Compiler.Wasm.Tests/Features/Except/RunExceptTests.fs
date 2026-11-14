module TranspilerWasmTests.Features.Except.Run

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - except replaces the tag it names and passes the others through`` () =
    let source =
        normalized """import nanyx/console

def parseNumber = { s -> int.fromString(s) except #invalidInt -> #invalidVersionNumber }

def show = {
  | #some(n) -> "number {n}"
  | #invalidVersionNumber -> "not a version number"
}

@entry
def main = {
  println(show(parseNumber("42")))
  println(show(parseNumber("x")))
}
"""
    runSource source |> assertRan "number 42\nnot a version number\n"

[<Fact>]
let ``Run - except with several arms, a nested tag and a binding`` () =
    let source =
        normalized """import nanyx/console
import nanyx/text

type Loaded = #some(int) | #error(#notFound | #io(string) | #denied)

def notFound: Loaded = #error(#notFound)
def failed: Loaded = #error(#io("disk"))
def denied: Loaded = #error(#denied)
def loaded: Loaded = #some(7)

def recover: Loaded -> #some(int) | #error(#denied) = { result ->
  result except
    | #error(#notFound) -> #some(0)
    | #error(#io(message)) -> #some(message \string.length)
}

def show: (#some(int) | #error(#denied)) -> string = {
  | #some(n) -> "{n}"
  | #error(#denied) -> "denied"
}

@entry
def main = {
  for [notFound, failed, denied, loaded] | result -> println(show(recover(result)))
}
"""
    runSource source |> assertRan "0\n4\ndenied\n7\n"

[<Fact>]
let ``Run - except leaves a single case for a destructuring def`` () =
    let source =
        normalized """import nanyx/console

def find: (list(int), int) -> #some(int) | #notFound = { xs, target ->
  match xs
    | [] -> #notFound
    | [x, ...rest] -> if x == target then #some(x) else find(rest, target)
}

@entry
def main = {
  def #some(found) = find([1, 2, 3], 9) except #notFound -> #some(-1)
  println("{found}")
}
"""
    runSource source |> assertRan "-1\n"
