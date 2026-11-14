module TranspilerWasmTests.Features.StdLib.RunOptionResult

open Xunit
open TranspilerWasmTestHelpers

// `show...` match on the value: `dbg` of a tag returned from a call prints its raw encoding.
let private header =
    normalized """import nanyx/console
import nanyx/text

def showInts: [Console] list(int) -> () = { xs ->
  println("[" ++ string.join(xs \list.map(int.toString), ", ") ++ "]")
}
def showOption: [Console] (#some(int) | #none) -> () = {
  | #some(n) -> println("#some({n})")
  | #none -> println("#none")
}
def showResult: [Console] Result(int, string) -> () = {
  | #some(n) -> println("#some({n})")
  | #error(e) -> println("#error({e})")
}

def present: #some(int) | #none = #some(3)
def absent: #some(int) | #none = #none
def ok: Result(int, string) = #some(4)
def bad: Result(int, string) = #error("bad")

"""

[<Fact>]
let ``Run - someToList gives the value of #some as a list, and the empty list otherwise`` () =
    let source =
        header +
        normalized """@entry
def main = {
  showInts(someToList(present))
  showInts(absent \someToList)
  def notFound: #some(int) | #notFound = #notFound
  showInts([1, ...(notFound \someToList)])
}
"""
    runSource source |> assertRan "[3]\n[]\n[1]\n"

[<Fact>]
let ``Run - somes keeps the values of the #somes in order and drops the other tags`` () =
    let source =
        header +
        normalized """@entry
def main = {
  showInts(somes([present, absent, #some(5), absent, #some(7)]))
  showInts([absent, absent] \somes)
  showInts(somes([]))
}
"""
    runSource source |> assertRan "[3, 5, 7]\n[]\n[]\n"

[<Fact>]
let ``Run - somes of a long list does not overflow the stack`` () =
    let source =
        header +
        normalized """def options: int, list(#some(int) | #none) -> list(#some(int) | #none) = {
  | 0, acc -> acc
  | n, acc -> options(n - 1, [if n % 2 == 0 then #some(n) else #none, ...acc])
}
@entry
def main = {
  def values = somes(options(200000, []))
  dbg(list.length(values))
  dbg(someOr(list.head(values), 0))
}
"""
    runSource source |> assertRan "dbg: 100000\ndbg: 2\n"

[<Fact>]
let ``Run - Result.mapOr applies the function to a success and gives the default for an error`` () =
    let source =
        header +
        normalized """@entry
def main = {
  dbg(Result.mapOr(ok, 0, { n -> n * 10 }))
  dbg(bad \Result.mapOr(0, { n -> n * 10 }))
  println(Result.mapOr(bad, "none", int.toString))
}
"""
    runSource source |> assertRan "dbg: 40\ndbg: 0\nnone\n"

[<Fact>]
let ``Run - Result.orElse keeps a success and passes an error to the fallback`` () =
    let source =
        header +
        normalized """def retry: string -> Result(int, string) = { e -> #error("retried {e}") }
@entry
def main = {
  showResult(Result.orElse(ok, { e -> #some(0) }))
  showResult(bad \Result.orElse { e -> #some(string.length(e)) })
  showResult(bad \Result.orElse(retry))
  showResult(bad \Result.orElse { e -> #error("recovered from {e}") })
}
"""
    runSource source |> assertRan "#some(4)\n#some(3)\n#error(retried bad)\n#error(recovered from bad)\n"

[<Fact>]
let ``Run - orElseSome keeps a #some and calls the fallback only for another tag (#480)`` () =
    let source =
        header + """def lookup: () -> #some(int) | #none = { #some(9) }
@entry
def main = {
  showOption(orElseSome(present, { #some(8) }))
  showOption(absent \orElseSome { #some(8) })
  showOption(absent \orElseSome(lookup))
  showOption(absent \orElseSome { absent })
  showOption(present \orElseSome {
    println("not called")
    #none
  })
}
"""
    runSource source |> assertRan "#some(3)\n#some(8)\n#some(9)\n#none\n#some(3)\n"

[<Fact>]
let ``Run - mapSomeOrElse maps a #some and calls the fallback only for another tag (#182)`` () =
    let source =
        header + """def seven: () -> int = { 7 }
@entry
def main = {
  dbg(mapSomeOrElse(#none, { 7 }, { n -> n + 1 }))
  dbg(mapSomeOrElse(present, { dbg(0) }, { n -> n + 1 }))
  dbg(absent \mapSomeOrElse(seven, { n -> n + 1 }))
}
"""
    runSource source |> assertRan "dbg: 7\ndbg: 4\ndbg: 7\n"

// #183: two steps that fail with different tags.
let private steps =
    normalized """import nanyx/console

def parseDigit: string -> #some(int) | #invalidInt = {
  | "1" -> #some(1)
  | _ -> #invalidInt
}
def find: int -> #some(string) | #notFound = {
  | 1 -> #some("1")
  | 2 -> #some("x")
  | _ -> #notFound
}
"""

[<Fact>]
let ``Run - bindSome keeps the tags both steps fail with, matched with no catch-all`` () =
    let source =
        steps +
        normalized """def piped: int -> string = { n ->
  match find(n) \bindSome(parseDigit)
    | #some(digit) -> "#some({digit})"
    | #notFound -> "#notFound"
    | #invalidInt -> "#invalidInt"
}
def called: int -> string = { n ->
  def b = bindSome(find(n), parseDigit)
  match b
    | #some(digit) -> "#some({digit})"
    | #notFound -> "#notFound"
    | #invalidInt -> "#invalidInt"
}
@entry
def main = {
  println(piped(1))
  println(piped(2))
  println(piped(3))
  println(called(1))
  println(called(2))
  println(called(3))
}
"""
    runSource source |> assertRan "#some(1)\n#invalidInt\n#notFound\n#some(1)\n#invalidInt\n#notFound\n"

[<Fact>]
let ``Run - Result.bind continues a success and stops at the first error`` () =
    let source =
        header +
        normalized """def half: int -> Result(int, string) = { n -> if n % 2 == 0 then #some(n / 2) else #error("odd") }
@entry
def main = {
  showResult(ok \Result.bind(half))
  showResult(Result.bind(ok, half) \Result.bind(half))
  showResult(Result.bind(bad, half))
}
"""
    runSource source |> assertRan "#some(2)\n#some(1)\n#error(bad)\n"
