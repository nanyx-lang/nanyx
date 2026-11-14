module TranspilerWasmTests.Features.Lists.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - list literals and patterns`` () =
    let source =
        normalized """import nanyx/console

def describe: list(int) -> string = { xs ->
  match xs
  | [] -> "empty"
  | [_x] -> "one"
  | [1, _y] -> "one then another"
  | [_x, ..._rest] -> "many"
}
def sum: list(int) -> int = { xs ->
  match xs
  | [] -> 0
  | [x, ...rest] -> x + sum(rest)
}
println(describe([]))
println(describe([7]))
println(describe([1, 2]))
println(describe([2, 2, 2]))
dbg(sum([1, 2, 3, 4]))
"""
    runSource source |> assertRan "empty\none\none then another\nmany\ndbg: 10\n"

[<Fact>]
let ``Run - a list literal's ] may go on a line of its own`` () =
    // #248: the `]` had to be on the same line as the last item.
    let source = """
import nanyx/console

def xs = [
  1,
  2
]

def count = { n ->
  def ys = [
    n,
    2,
    3
  ]
  list.length(ys)
}

println("{list.length(xs)} {count(1)}")
"""
    runSource source |> assertRan "2 3\n"

[<Fact>]
let ``Run - an unnamed rest matches a longer list`` () =
    // `[x, ...]` binds the head and ignores the rest, whatever its length.
    let source =
        normalized """import nanyx/console

def first: list(int) -> int = { xs ->
  match xs
  | [x, ...] -> x
  | [] -> 0
}
dbg(first([5, 6, 7]))
dbg(first([8]))
dbg(first([]))
"""
    runSource source |> assertRan "dbg: 5\ndbg: 8\ndbg: 0\n"

[<Fact>]
let ``Run - list spread in expressions`` () =
    // Regression: `[1, ...rest]` worked as a pattern but not as an expression (#44).
    let source =
        normalized """import nanyx/console
import nanyx/text

def sum: list(int) -> int = {
  | [] -> 0
  | [head, ...tail] -> head + sum(tail)
}
@entry
def main = {
  def xs = [1, ...[2, 3]]
  println("{sum(xs)}")
  def names = ["b"]
  println(string.join(["a", ...names, "c", ...[], ...["d"]], "-"))
}
"""
    runSource source |> assertRan "6\na-b-c-d\n"

[<Fact>]
let ``Run - ++ joins lists`` () =
    let source =
        normalized """import nanyx/console
import nanyx/text

def front = ["a", "b"]
println(string.join(front ++ ["c"] ++ [], "-"))
println(string.join([] ++ front, "-"))
dbg(string.length("ab" ++ "cd" ++ "e"))
"""
    runSource source |> assertRan "a-b-c\na-b\ndbg: 5\n"

[<Fact>]
let ``Run - ++ on a long list does not overflow the stack`` () =
    // `++` copied its first list by calling itself once per cell, which ran out of stack (#558). The list of strings
    // goes through the copy that takes a reference to each element.
    let source =
        """import nanyx/console

def build: int, list(int) -> list(int) = { n, acc -> if n == 0 then acc else build(n - 1, [n, ...acc]) }
def words: int, list(string) -> list(string) = { n, acc ->
  if n == 0 then acc else words(n - 1, ["w{n}", ...acc])
}
def xs = build(1000000, []) ++ [0]
def ws = words(200000, []) ++ ["end"]
@entry
def main = {
  println("{list.length(xs)} {list.length(ws)}")
  match list.last(xs)
    | #some(n) -> println("last {n}")
    | #emptyList -> println("empty")
  match list.last(ws)
    | #some(w) -> println("last {w}")
    | #emptyList -> println("empty")
}
"""
    runSource source |> assertRan "1000001 200001\nlast 0\nlast end\n"

[<Fact>]
let ``Run - the prelude's List functions`` () =
    let source =
        normalized """import nanyx/console

def show: list(int) -> string = {
  | [] -> "[]"
  | [y, ...rest] -> "{y} " ++ show(rest)
}
def xs = [3, 1, 2]
println(show(xs \ map({ x -> x * 2 })))
println(show(xs \ filter({ x -> x > 1 })))
println(show(list.reverse(xs)))
println(show(list.flatten([[1, 2], [3], []])))
println(show(list.take(xs, 2)) ++ " | " ++ show(list.skip(xs, 2)) ++ " | " ++ show(list.append(xs, 9)))
def total: int = list.fold(xs, 0, { acc, x -> acc + x })
def back: int = list.foldBack([1, 2, 3], 0, { acc, x -> acc - x })
def count: int = list.length(xs)
def pairs: int = list.length(list.zip(xs, ["a", "b"]))
println("{total} {back} {count} {pairs}")
println(if list.all(xs, { x -> x > 0 }) then "all" else "not all")
println(if list.any(xs, { x -> x > 5 }) then "any" else "none")
def found = xs \ find({ x -> x < 3 })
def empty: list(int) = []
def first = list.head(empty)
@entry
def main = {
  match found
    | #some(n) -> println("found {n}")
    | #notFound -> println("not found")
  match first
    | #some(n) -> println("head {n}")
    | #emptyList -> println("empty")
}
"""
    runSource source
    |> assertRan (
        withFinalNewline """6 2 4 []
3 2 []
2 1 3 []
1 2 3 []
3 1 [] | 2 [] | 3 1 2 9 []
6 -6 3 2
all
none
found 1
empty""")

/// Prints a list of ints, for the tests of the prelude's List functions below.
let private showInts =
    normalized """import nanyx/console
import nanyx/text

def show: list(int) -> string = {
  | [] -> "[]"
  | [y, ...rest] -> "{y} " ++ show(rest)
}
"""

[<Fact>]
let ``Run - List.distinct keeps the first of each value`` () =
    // The tags are bound to annotated names first: a list literal of different tags doesn't type-check yet (#481).
    let source =
        showInts +
        normalized """type Color = #red | #green
def red: Color = #red
def green: Color = #green
println(show(list.distinct([1, 2, 2, 3, 1])))
println(string.join(list.distinct(["b", "a", "b"]), ","))
dbg(list.length(list.distinct([red, green, red, red])))
dbg(list.length(list.distinct([true, true])))
println(show(list.distinct([])))
"""
    runSource source |> assertRan "1 2 3 []\nb,a\ndbg: 2\ndbg: 1\n[]\n"

[<Fact>]
let ``Run - list.distinct compares records, tuples and lists by their structure`` () =
    // #191: `distinct` is `[Eq(a)] list(a) -> list(a)`; it took numbers, bools, strings and tags only.
    let source =
        showInts
        + """type Person = (name: string, age: int)
def people: list(Person) = [
  Person(name = "Ann", age = 40), Person(name = "Bob", age = 30), Person(name = "Ann", age = 40)]
println(string.join(list.map(list.distinct(people), { p -> p.name }), ","))
println(string.join(list.map(list.distinct([(1, "a"), (2, "a"), (1, "a")]), { p -> "{p.0}{p.1}" }), ","))
println("{list.length(list.distinct([[1, 2], [2, 1], [1, 2], []]))}")
"""
    runSource source |> assertRan "Ann,Bob\n1a,2a\n3\n"

[<Fact>]
let ``Run - List.filterMap keeps the #some values in order and leaves out #skip`` () =
    let source =
        showInts
        + """type Color = #red | #green | #other(string)
def colors: list(Color) = [#red, #other("teal"), #green, #other("plum")]
def empty: list(int) = []
println(show(list.filterMap([1, 2, 3, 4, 5], { x -> if x % 2 == 1 then #some(x * 10) else #skip })))
def otherNames = colors \list.filterMap {
  | #other(name) -> #some(name)
  | _ -> #skip
}
println(string.join(otherNames, ","))
println(show(list.filterMap(empty, { x -> #some(x) })))
println(show(list.filterMap([1, 2], { x -> #skip })))
"""
    runSource source |> assertRan "10 30 50 []\nteal,plum\n[]\n[]\n"

[<Fact>]
let ``Run - List.sortBy sorts by an int key and keeps equal keys in order`` () =
    let source =
        showInts +
        normalized """type Person = (name: string, age: int)
def people: list(Person) = [
  Person(name = "Ann", age = 40), Person(name = "Bob", age = 30),
  Person(name = "Cy", age = 40), Person(name = "Di", age = 20)]
println(show(list.sortBy([5, 3, 9, 1, 3, 0, 7], { x -> x })))
println(string.join(list.sortBy(["bbb", "a", "cc"], { .length }), ","))
println(string.join(list.map(people \ sortBy({ .age }), { p -> p.name }), ","))
println(show(list.sortBy([], { x -> x })))
"""
    runSource source |> assertRan "0 1 3 3 5 7 9 []\na,cc,bbb\nDi,Bob,Ann,Cy\n[]\n"

[<Fact>]
let ``Run - List.unfold builds a list from a state until #done`` () =
    // The docs' `{ current, next -> ... }` takes the tuple apart in the parameters, which the backend can't call yet
    // (#441), so this one takes it apart in a tuple pattern.
    let source =
        showInts +
        normalized """def fibonacci = list.unfold((0, 1)) { (current, next) ->
  if current > 100 then #done
  else #some(current, (next, current + next))
}
println(show(fibonacci))
println(show(list.unfold(3, { n -> if n == 0 then #done else #some(n, n - 1) })))
println(show(list.unfold(0, { n -> #done })))
"""
    runSource source |> assertRan "0 1 1 2 3 5 8 13 21 34 55 89 []\n3 2 1 []\n[]\n"

[<Fact>]
let ``Run - List search and slicing helpers`` () =
    let source =
        showInts +
        normalized """def xs = [3, 1, 4, 1, 5]
def empty: list(int) = []
def describe: #some(int) | #notFound -> string = {
  | #some(n) -> "at {n}"
  | #notFound -> "not found"
}
def describeLast: #some(int) | #emptyList -> string = {
  | #some(n) -> "last {n}"
  | #emptyList -> "no last"
}
println(if list.contains(xs, 4) then "has 4" else "no 4")
println(if xs \ contains(9) then "has 9" else "no 9")
println(if list.contains(["a", "b"], "b") then "has b" else "no b")
println(describe(list.indexOf(xs, 1)))
println(describe(list.indexOf(xs, 9)))
dbg(list.count(xs, { x -> x > 1 }))
dbg(list.count(empty, { x -> x > 1 }))
println(if list.isEmpty(empty) then "empty" else "not empty")
println(if xs \ isEmpty then "empty" else "not empty")
println(describeLast(list.last(xs)))
println(describeLast(list.last(empty)))
println(show(list.dropLast(xs)))
println(show(list.dropLast(empty)))
println(show(list.takeWhile(xs, { x -> x < 4 })))
println(show(list.takeWhile(xs, { x -> x > 9 })))
println(show(list.mapIndexed(xs, { i, x -> i * 10 + x })))
println(show(list.flatMap([1, 2, 3], { x -> list.take([x, x, x], x) })))
println(show(list.flatMap(empty, { x -> [x] })))
"""
    runSource source
    |> assertRan (
        withFinalNewline """has 4
no 9
has b
at 1
not found
dbg: 3
dbg: 0
empty
not empty
last 5
no last
3 1 4 1 []
[]
3 1 []
[]
3 11 24 31 45 []
1 2 2 3 3 3 []
[]""")

[<Fact>]
let ``Run - List.skipWhile, partition, unzip and get`` () =
    let source =
        showInts +
        normalized """def xs = [1, 2, 3, 4]
def empty: list(int) = []
def describe: #some(int) | #outOfRange -> string = {
  | #some(n) -> "some {n}"
  | #outOfRange -> "out of range"
}
println(show(xs \ skipWhile { < 3 }))
println(show(list.skipWhile([1, 5, 1], { x -> x < 3 })))
println(show(list.skipWhile(xs, { x -> x < 9 })))
println(show(list.takeWhile(xs, { x -> x < 3 }) ++ list.skipWhile(xs, { x -> x < 3 })))
def (small, big) = xs \ partition { < 3 }
println(show(small) ++ show(big))
def (odd, even) = list.partition([5, 2, 3, 4, 1], { x -> x % 2 == 1 })
println(show(odd) ++ show(even))
def (none, all) = list.partition(empty, { x -> x > 0 })
println(show(none) ++ show(all))
def (numbers, names) = list.unzip([(1, "a"), (2, "b")])
println(show(numbers) ++ string.join(names, ","))
def (firsts, seconds) = list.unzip(list.zip(xs, [5, 6, 7, 8]))
println(show(firsts) ++ show(seconds))
println(describe([10, 20, 30] \ list.get(1)))
println(describe([10, 20, 30] \ get(0)))
println(describe(list.get([10, 20, 30], 2)))
println(describe(list.get([10, 20, 30], -1)))
println(describe(list.get([10, 20, 30], 3)))
println(describe(list.get(empty, 0)))
"""
    runSource source
    |> assertRan (
        withFinalNewline """3 4 []
5 1 []
[]
1 2 3 4 []
1 2 []3 4 []
5 3 1 []2 4 []
[][]
1 2 []a,b
1 2 3 4 []5 6 7 8 []
some 20
some 10
some 30
out of range
out of range
out of range""")
