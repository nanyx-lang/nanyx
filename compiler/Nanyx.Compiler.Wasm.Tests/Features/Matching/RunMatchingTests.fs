module TranspilerWasmTests.Features.Matching.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - matching on string literals`` () =
    // Regression: `| "Sat" -> ...` failed with "Unsupported match pattern" (#32).
    let source =
        normalized """import nanyx/console

def kind: string -> string = { day ->
  match day
    | "Sat" -> "weekend"
    | "Sun" -> "weekend"
    | _ -> "weekday"
}
def greet: #name(string) | #anon -> string = { who ->
  match who
    | #name("Ada") -> "hello, Ada!"
    | #name(_) -> "hello, someone"
    | #anon -> "hello, stranger"
}
def suffix = "t"
println(kind("Sat") ++ " " ++ kind("Sun") ++ " " ++ kind("Mon"))
println(kind("Sa" ++ suffix))
println(greet(#name("Ada")) ++ " / " ++ greet(#name("Bob")) ++ " / " ++ greet(#anon))
"""
    runSource source |> assertRan "weekend weekend weekday\nweekend\nhello, Ada! / hello, someone / hello, stranger\n"

[<Fact>]
let ``Run - tuple patterns in match arms`` () =
    // Regression: `| (0, _) -> ...` failed with "Unsupported match pattern" (#33).
    let source =
        normalized """import nanyx/console

def describe: (int, int) -> string = { p ->
  match p
    | (0, _) -> "x zero"
    | (_, 0) -> "y zero"
    | _ -> "other"
}
def swapped: (int, int) -> string = { p ->
  match p
    | (a, b) -> "{b} {a}"
}
def firstName: list((string, int)) -> string = { people ->
  match people
    | [(name, _), ..._rest] -> name
    | [] -> "nobody"
}
@entry
def main = {
  println(describe((0, 1)) ++ " " ++ describe((1, 0)) ++ " " ++ describe((1, 1)))
  println(swapped((1, 2)))
  println(firstName([("Ada", 36), ("Grace", 45)]) ++ " " ++ firstName([]))
}
"""
    runSource source |> assertRan "x zero y zero other\n2 1\nAda nobody\n"

[<Fact>]
let ``Run - a name alone in a pattern binds the record field of that name`` () =
    let source =
        normalized """import nanyx/console

def a: #some(x: string, y: string) = #some(x = "left", y = "right")
def g: typeof a -> string = { #some(y, x) -> "x = {x}, y = {y}" }
def describe: (name: string, age: int) -> string = { r -> match r
  | (age, name) -> "{name} is {age}" }
@entry
def main = {
  println(g(a))
  println(describe((name = "Ada", age = 36)))
}
"""
    runSource source |> assertRan "x = left, y = right\nAda is 36\n"

[<Fact>]
let ``Run - identifier patterns bind the scrutinee`` () =
    // Regression: `| n -> ...` and `| 0, y -> ...` failed with "Unsupported identifier" (#34).
    let source =
        normalized """import nanyx/console

def echo: int -> int = { v ->
  match v
    | 0 -> 100
    | n -> n + 1
}
def pick: (int, int) -> int = { a, b ->
  match a, b
    | 0, y -> y
    | x, _ -> x
}
def label: string -> string = { s ->
  match s
    | text -> "<" ++ text ++ ">"
}
dbg(echo(0))
dbg(echo(41))
dbg(pick(0, 5))
dbg(pick(7, 5))
println(label("x"))
"""
    runSource source |> assertRan "dbg: 100\ndbg: 42\ndbg: 5\ndbg: 7\n<x>\n"

[<Fact>]
let ``Run - if-matching`` () =
    // Regression: `if get(3) is #some(v) then ...` was a parse error (#42).
    let source =
        normalized """import nanyx/console

def get: int -> #some(int) | #none = { n -> #some(n) }
def nothing: int -> #some(int) | #none = { _n -> #none }
def describe: #some(int) | #none -> string = { r ->
  if r is #some(v) then "got {v}" else "nothing"
}
@entry
def main = {
  if get(3) is #some(v) then println("got {v}")
  if nothing(0) is #some(v) then println("unexpected {v}")
  println(describe(get(5)) ++ " " ++ describe(nothing(5)))
}
"""
    runSource source |> assertRan "got 3\ngot 5 nothing\n"

[<Fact>]
let ``Run - multi-branch if`` () =
    // #76: `if | cond -> ... | value is pattern -> ... | else -> ...` was a parse error at `|`.
    let source =
        normalized """import nanyx/console

def lookup: int -> #some(int) | #none = { n -> if n > 100 then #none else #some(n * 2) }
def classify: int -> string = { n ->
  if | n < 0 -> "negative"
     | n == 0 ->
       "zero"
     | lookup(n) is #some(d) -> "doubled {d}"
     | else -> "too big"
}
@entry
def main = {
  println(classify(0 - 4))
  println(classify(0))
  println(classify(21))
  println(classify(500))
  if | classify(1) == "zero" -> println("unexpected")
     | classify(0) == "zero" -> println("no else")
}
"""
    runSource source |> assertRan "negative\nzero\ndoubled 42\ntoo big\nno else\n"

[<Fact>]
let ``Run - a nested match ends at an arm indented back to the outer match`` () =
    // #249: `| _ -> "other"` was read as a third arm of the inner match.
    let source = """
import nanyx/console

def f: list(int) -> string = { xs ->
  match xs
    | [a] ->
        match a
          | 0 -> "zero"
          | _ -> "one"
    | _ -> "other"
}

def g: list(int) -> string = { xs ->
  match xs
    | [a] ->
        if
          | a == 0 -> "zero"
          | else -> "one"
    | _ -> "other"
}

println(f([1, 2]))
println(f([0]))
println(g([1, 2]))
println(g([1]))
"""
    runSource source |> assertRan "other\nzero\nother\none\n"

[<Fact>]
let ``Run - guard patterns in the documented form`` () =
    // Regression: the docs write guards as `| { < 0 } -> ...`; the parser only knew `| < 0 ->` (#46).
    let source =
        normalized """import nanyx/console

def describe: int -> string = { n ->
  match n
    | { < 0 } -> "negative"
    | 0 -> "zero"
    | _ -> "positive"
}
def limit = 10
def size: int -> string = { n ->
  match n
    | { >= limit * 2 } -> "huge"
    | { >= limit } -> "big"
    | else -> "small"
}
@entry
def main = {
  println(describe(-1) ++ " " ++ describe(0) ++ " " ++ describe(5))
  println(size(25) ++ " " ++ size(10) ++ " " ++ size(3))
}
"""
    runSource source |> assertRan "negative zero positive\nhuge big small\n"

[<Fact>]
let ``Run - any predicate lambda as a guard pattern`` () =
    // #428: a guard is any predicate on the value matched, not only one comparison against it.
    let source =
        normalized """import nanyx/console

def parity: int -> string = { n ->
  match n
    | { % 2 == 0 } -> "even"
    | _ -> "odd"
}
def size: string -> string = { s ->
  match s
    | { .length > 5 } -> "long"
    | { x -> x == "" } -> "empty"
    | _ -> "short"
}
def nested: (int, #some(string) | #none) -> string = { p ->
  match p
    | ({ < 0 }, _) -> "negative"
    | (n, #some({ .length > 2 })) -> "long at {n}"
    | _ -> "other"
}
def busy: list(int), int -> string = { xs, threshold ->
  match xs
    | { ys -> (ys \filter { > threshold }).length > 1 } -> "busy"
    | [] -> "empty"
    | _ -> "quiet"
}
@entry
def main = {
  println(parity(4) ++ " " ++ parity(7) ++ " " ++ parity(-2))
  println(size("marvellous") ++ " " ++ size("") ++ " " ++ size("hi"))
  println(nested((-1, #none)) ++ " " ++ nested((2, #some("abc"))) ++ " " ++ nested((5, #some("ab"))))
  println(busy([1, 9, 9], 5) ++ " " ++ busy([1, 9], 5) ++ " " ++ busy([], 5))
}
"""
    runSource source |> assertRan "even odd even\nlong empty short\nnegative long at 2 other\nbusy quiet empty\n"

[<Fact>]
let ``Run - or-patterns in match arms`` () =
    // #43: `| a | b -> ...` is one arm matching either alternative.
    let source =
        normalized """import nanyx/console

type Day = #mon | #tue | #wed | #thu | #fri | #sat | #sun
def kind: Day -> string = { day ->
  match day
    | #sat | #sun -> "weekend"
    | _ -> "weekday"
}
def size: int -> string = { n ->
  match n
    | 0
    | 1
    | 2 -> "small"
    | _ -> "big"
}
def axis: (int, int) -> string = {
  | 0, _ | _, 0 -> "axis"
  | _, _ -> "plane"
}
println(kind(#sat) ++ " " ++ kind(#sun) ++ " " ++ kind(#wed))
println(size(1) ++ " " ++ size(2) ++ " " ++ size(9))
println(axis(0, 5) ++ " " ++ axis(5, 0) ++ " " ++ axis(3, 3))
"""
    runSource source |> assertRan "weekend weekend weekday\nsmall small big\naxis axis plane\n"

[<Fact>]
let ``Run - a lone else in a match on several values`` () =
    // A lone `else` used to crash type checking in a match on several values.
    let source =
        normalized """import nanyx/console

def describe: int, int -> string = { x, y ->
  match x, y
    | 0, 0 -> "origin"
    | 0, _ -> "on the y axis"
    | else -> "elsewhere"
}
println(describe(0, 0) ++ ", " ++ describe(0, 5) ++ ", " ++ describe(3, 4))
"""
    runSource source |> assertRan "origin, on the y axis, elsewhere\n"

[<Fact>]
let ``Run - several values and a tuple of them are matched alike, brackets or not`` () =
    let source =
        normalized """import nanyx/console

match (1, 2)
  | (x, y) -> println("{x}, {y}")
match 1, 2
  | x, y -> println("{x}, {y}")
match (1, 2)
  | 0, _ -> println("zero")
  | x, y -> println("{x}, {y}")
match 1, 2
  | (0, _) -> println("zero")
  | (x, y) -> println("{x}, {y}")
"""
    runSource source |> assertRan "1, 2\n1, 2\n1, 2\n1, 2\n"

[<Fact>]
let ``Run - an arm for a tag the value cannot have never matches`` () =
    let source =
        normalized """import nanyx/console

def simple = #nil

def check = match simple
  | #nil -> "empty"
  | #some x -> "has value"
  | else -> "other"

println(check)
"""
    let outcome = runSource source
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal("empty\n", outcome.StandardOutput)