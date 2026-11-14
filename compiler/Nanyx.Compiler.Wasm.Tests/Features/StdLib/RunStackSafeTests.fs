module TranspilerWasmTests.Features.StdLib.RunStackSafe

open Xunit
open TranspilerWasmTestHelpers

// The standard library's walks are stack-safe (docs/src/content/work-in-progress/tail-calls.md): each of these ran out of stack when
// the function recursed once per element other than in tail position, as every one of them traps on 20,000 elements
// written that way. The lists are 20,000 long, not more, only to keep the suite quick.

/// The start of each program: `xs` is the list 1 to 20,000, and `show` gives a list's length, first element and
/// last element.
let private withList (body: string) =
    normalized """import nanyx/console
import nanyx/text

def upTo: int, list(int) -> list(int) = { n, acc -> if n == 0 then acc else upTo(n - 1, [n, ...acc]) }
def first: list(int) -> int = {
  | [x, ..._] -> x
  | [] -> 0
}
def show: list(int) -> string = { ys ->
  "{list.length(ys)} {first(ys)} {list.fold(ys, 0, { _, y -> y })}"
}
def xs = upTo(20000, [])
"""
    + body

[<Fact>]
let ``Run - List.map works on 20,000 elements`` () =
    runSource (withList "println(show(list.map(xs, { x -> x * 2 })))\n") |> assertRan "20000 2 40000\n"

[<Fact>]
let ``Run - List.minBy, maxBy, distinctBy and findIndex work on 20,000 elements`` () =
    let body = """
println(show(list.distinctBy(xs, { x -> x % 10 })))
def index = { found -> found \match | #some(i) -> i | #notFound -> -1 }
def element = { found -> found \match | #some(x) -> x | #emptyList -> -1 }
println("{element(list.minBy(xs, { x -> 0 - x }))} {element(list.maxBy(xs, { x -> 0 - x }))}")
println("{index(list.findIndex(xs, { x -> x == 20000 }))}")
"""
    runSource (withList (body.ReplaceLineEndings "\n")) |> assertRan "10 1 10\n20000 1\n19999\n"

[<Fact>]
let ``Run - List.filter works on 20,000 elements`` () =
    runSource (withList "println(show(list.filter(xs, { x -> x > 1 })))\n") |> assertRan "19999 2 20000\n"

[<Fact>]
let ``Run - List.filterMap works on 20,000 elements`` () =
    let body = "println(show(list.filterMap(xs, { x -> if x > 1 then #some(x * 2) else #skip })))\n"
    runSource (withList body) |> assertRan "19999 4 40000\n"

[<Fact>]
let ``Run - List.take works on 20,000 elements`` () =
    runSource (withList "println(show(list.take(xs, 19999)))\n") |> assertRan "19999 1 19999\n"

[<Fact>]
let ``Run - List.flatten works on 20,000 lists and on long lists`` () =
    let body =
        normalized """println(show(list.flatten(list.map(xs, { x -> [x] }))))
println(show(list.flatten([xs, [0], xs])))
"""
    runSource (withList body) |> assertRan "20000 1 20000\n40001 1 20000\n"

[<Fact>]
let ``Run - List.zip works on 20,000 elements`` () =
    let body =
        normalized """def pairs = list.zip(xs, list.reverse(xs))
match pairs
  | [(a, b), ..._] -> println("{list.length(pairs)} {a} {b}")
  | [] -> println("none")
"""
    runSource (withList body) |> assertRan "20000 1 20000\n"

[<Fact>]
let ``Run - List.unzip works on 20,000 elements`` () =
    let body =
        normalized """def (firsts, seconds) = list.unzip(list.zip(xs, list.reverse(xs)))
println(show(firsts))
println(show(seconds))
"""
    runSource (withList body) |> assertRan "20000 1 20000\n20000 20000 1\n"

[<Fact>]
let ``Run - List.partition works on 20,000 elements`` () =
    let body =
        normalized """def (passed, failed) = list.partition(xs, { x -> x > 2 })
println(show(passed))
println(show(failed))
"""
    runSource (withList body) |> assertRan "19998 3 20000\n2 1 2\n"

[<Fact>]
let ``Run - List.skipWhile works on 20,000 elements`` () =
    runSource (withList "println(show(list.skipWhile(xs, { x -> x < 20000 })))\n") |> assertRan "1 20000 20000\n"

[<Fact>]
let ``Run - List.get works on 20,000 elements`` () =
    let body =
        normalized """match list.get(xs, 19999)
  | #some(x) -> println("{x}")
  | #outOfRange -> println("out of range")
"""
    runSource (withList body) |> assertRan "20000\n"

[<Fact>]
let ``Run - List.sortBy works on 20,000 elements`` () =
    runSource (withList "println(show(list.sortBy(xs, { x -> 0 - x })))\n") |> assertRan "20000 20000 1\n"

[<Fact>]
let ``Run - List.unfold works for 20,000 elements`` () =
    let body = "println(show(list.unfold(1, { i -> if i <= 20000 then #some(i, i + 1) else #done })))\n"
    runSource (withList body) |> assertRan "20000 1 20000\n"

[<Fact>]
let ``Run - List.append works on 20,000 elements`` () =
    runSource (withList "println(show(list.append(xs, 0)))\n") |> assertRan "20001 1 0\n"

[<Fact>]
let ``Run - math.pow works for an exponent of 100,000`` () =
    let source =
        normalized """import nanyx/console
import nanyx/math as math

println("{math.pow(1, 100000)} {math.pow(-1, 99999)} {math.pow(3, 4)} {math.pow(2, 0)}")
"""
    runSource source |> assertRan "1 -1 81 1\n"

[<Fact>]
let ``Run - String.repeat works 100,000 times`` () =
    let body =
        normalized """def s = string.repeat("ab", 100000)
println("{string.length(s)} {string.count(s, "ab")}")
println(string.repeat("xy", 5) ++ string.repeat("z", 1))
"""
    runSource (withList body) |> assertRan "200000 100000\nxyxyxyxyxyz\n"

[<Fact>]
let ``Run - string.chars and string.reverse work on 100,000 characters`` () =
    let body = """def s = string.repeat("é", 100000)
match string.chars(s)
  | [c, ..._] -> println(c)
  | [] -> println("none")
println("{string.reverse(s) == s} {string.reverse(string.repeat("ab", 100000)) == string.repeat("ba", 100000)}")
println("{list.length(string.chars(string.repeat("a€", 50000)))}")
"""
    runSource (withList body) |> assertRan "é\ntrue true\n100000\n"

[<Fact>]
let ``Run - String.split and String.replace work on 20,000 pieces`` () =
    let body =
        normalized """def s = string.repeat("a,", 20000)
def pieces = string.split(s, on = ",")
println("{list.length(pieces)} {string.join(pieces, ",") == s}")
def replaced = string.replace(s, pattern = ",", replacement = ";")
println("{string.length(replaced)} {string.count(replaced, ";")} {string.count(replaced, ",")}")
"""
    runSource (withList body) |> assertRan "20001 true\n40000 20000 0\n"

[<Fact>]
let ``Run - String.lines and String.words work on 20,000 pieces`` () =
    let body =
        "def lines = string.lines(string.repeat(\"a\r\n\", 20000))\n" +
        normalized """println("{list.length(lines)} {list.all(lines, { line -> line == "a" })}")
def words = string.words(string.repeat(" a", 20000))
println("{list.length(words)} {list.all(words, { word -> word == "a" })}")
"""
    runSource (withList body) |> assertRan "20000 true\n20000 true\n"
