module TranspilerWasmTests.Features.StdLib.SeqFunctions

open Xunit
open TranspilerWasmTestHelpers

// The `seq` functions that have `list`'s names (#2020): the lazy ones on endless sequences cut with `take`, the ones
// that stop early finding their answer in an endless sequence, and the ones that read the whole of a sequence.

[<Fact>]
let ``Run - the lazy functions work on endless sequences cut with take`` () =
    let source = """
@entry
def main = {
  println((0..) \seq.flatMap { n -> seq.range(n, n + 1) } \seq.take(5) \seq.toList)
  println((0..) \seq.filterMap { n -> if n % 2 == 0 then #some(n * 10) else #skip } \seq.take(3) \seq.toList)
  println((0..) \seq.select { n -> seq { if n % 3 == 0 then yield n } } \seq.take(3) \seq.toList)
  println((0..) \seq.mapIndexed { i, x -> i + x } \seq.take(3) \seq.toList)
  println((0..) \seq.skipWhile { n -> n < 3 } \seq.take(2) \seq.toList)
  println((1..) \seq.scan(0) { total, n -> total + n } \seq.take(4) \seq.toList)
  println(seq.zipWith(1.., 10.., { a, b -> a + b }) \seq.take(3) \seq.toList)
  println((1..) \seq.intersperse(0) \seq.take(5) \seq.toList)
  println((1..) \seq.chunks(2) \seq.take(2) \seq.toList)
  println((0..) \seq.windows(3) \seq.take(2) \seq.toList)
  println((0..) \seq.map { n -> n % 3 } \seq.distinct \seq.take(3) \seq.toList)
  println((0..) \seq.distinctBy { n -> n % 2 } \seq.take(2) \seq.toList)
  println((0..) \seq.map { n -> (0..n) } \seq.flatten \seq.take(4) \seq.toList)
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan
        "[0, 1, 1, 2, 2]\n[0, 20, 40]\n[0, 3, 6]\n[0, 2, 4]\n[3, 4]\n[0, 1, 3, 6]\n[11, 13, 15]\n[1, 0, 2, 0, 3]\n[[1, 2], [3, 4]]\n[[0, 1, 2], [1, 2, 3]]\n[0, 1, 2]\n[0, 1]\n[0, 0, 1, 0]\n"

[<Fact>]
let ``Run - the makers and unzip give list's answers`` () =
    let source = """
@entry
def main = {
  println(seq.init(3, { i -> i * 2 }) \seq.toList)
  println(seq.replicate(0, count = 3) \seq.toList)
  println(seq.replicate(0, count = 0) \seq.toList)
  def (numbers, letters) = seq.unzip(list.toSeq([(1, "a"), (2, "b")]))
  println((seq.toList(numbers), seq.toList(letters)))
  println([[1, 2], [3]] \list.map(list.toSeq) \list.toSeq \seq.flatten \seq.toList)
  println((1..5) \seq.chunks(2) \seq.toList)
  println((1..2) \seq.windows(3) \seq.toList)
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "[0, 2, 4]\n[0, 0, 0]\n[]\n([1, 2], [a, b])\n[1, 2, 3]\n[[1, 2], [3, 4], [5]]\n[]\n"

[<Fact>]
let ``Run - the functions that stop early find their answer in an endless sequence`` () =
    let source = """
@entry
def main = {
  println((1..) \seq.find { n -> n * n > 50 })
  println((1..) \seq.findIndex { n -> n > 4 })
  println((1..) \seq.indexOf(7))
  println((1..) \seq.contains(7))
  println((1..) \seq.any { n -> n > 100 })
  println((1..) \seq.all { n -> n < 5 })
  println((1..) \seq.head)
  println((1..) \seq.get(4))
  println((1..) \seq.isEmpty)
  println((1..) \seq.selectOne { n -> seq { if n > 9 then yield n * 2 } })
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "#some(8)\n#some(4)\n#some(6)\ntrue\ntrue\nfalse\n#some(1)\n#some(5)\nfalse\n#some(20)\n"

[<Fact>]
let ``Run - the functions that stop early answer for an empty or short sequence`` () =
    let source = """
@entry
def main = {
  def none: seq(int) = seq.empty()
  println(none \seq.find { n -> n > 0 })
  println(none \seq.head)
  println(none \seq.isEmpty)
  println((1..3) \seq.get(9))
  println((1..3) \seq.get(-1))
  println(none \seq.all { n -> n > 0 })
  println(none \seq.selectOne { n -> seq { yield n } })
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "#notFound\n#emptyList\ntrue\n#outOfRange\n#outOfRange\ntrue\n#notFound\n"

[<Fact>]
let ``Run - the functions that read the whole sequence`` () =
    let source = """
@entry
def main = {
  println((1..10) \seq.count { n -> n % 2 == 0 })
  println((1..10) \seq.length)
  println((1..10) \seq.last)
  println(seq.empty() \seq.last)
  println([3, 9, 2, 9] \list.toSeq \seq.max)
  println([3, 9, 2] \list.toSeq \seq.min)
  println(["aa", "b", "ccc"] \list.toSeq \seq.maxBy { s -> string.length(s) })
  println(["aa", "b", "ccc", "d"] \list.toSeq \seq.minBy { s -> string.length(s) })
  println((1..4) \seq.sum)
  println((1..6) \seq.partition { n -> n % 2 == 0 })
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "5\n10\n#some(10)\n#emptyList\n#some(9)\n#some(2)\n#some(ccc)\n#some(b)\n10\n([2, 4, 6], [1, 3, 5])\n"

[<Fact>]
let ``Run - a lazy function pulls only what is needed from a sequence whose steps print`` () =
    let source = """
def noisy: [Console] () -> [Console] seq(int) = { _ ->
  seq.map(0.., { n ->
    println("pulled {n}")
    n
  })
}

@entry
def main = {
  def found = noisy() \seq.filterMap { n -> if n > 1 then #some(n * 10) else #skip } \seq.find { n -> n > 0 }
  println(found)
  println(noisy() \seq.skipWhile { n -> n < 1 } \seq.head)
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "pulled 0\npulled 1\npulled 2\n#some(20)\npulled 0\npulled 1\n#some(1)\n"

[<Fact>]
let ``Run - flatMap, filterMap and find read as the editor's search for a line does`` () =
    let source = """
def isSignificant: string -> bool = { line -> string.trim(line) != "" }

def iterLinesOutwards: list(string), int -> seq((#some(string) | #none, #some(string) | #none)) = { lines, at ->
  (1..) \seq.map { step -> (lines \list.get(at - step) \option, lines \list.get(at + step) \option) }
}

def option: (#some(string) | #outOfRange) -> #some(string) | #none = {
  | #some(line) -> #some(line)
  | #outOfRange -> #none
}

@entry
def main = {
  def document = ["first", "", "  ", "middle", "", "last"]

  def found =
    iterLinesOutwards(document, 3)
      \seq.take(5)
      \seq.flatMap { (back, forward) -> [back, forward] \list.toSeq \seq.filterMap { x -> match x
        | #some(line) -> #some(line)
        | #none -> #skip } }
      \seq.find { line -> line \isSignificant }
      except #notFound -> #some("middle")

  println(found)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "#some(last)\n"
