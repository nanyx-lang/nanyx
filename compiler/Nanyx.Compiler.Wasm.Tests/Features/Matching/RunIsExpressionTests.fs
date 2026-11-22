module TranspilerWasmTests.Features.Matching.RunIsExpressions

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a test as a lambda's body finds an item`` () =
    // The example in #2274, which was a syntax error.
    let source =
        normalized """type Item = #foo(string) | #bar(string) | #baz | #quux

def items: list(Item) = [#foo("one"), #bar("two"), #baz, #quux]

items \list.find { x -> x is #quux } \dbg
"""
    runSource source |> withoutDbgLocations |> assertRan "#some(#quux)\n"

[<Fact>]
let ``Run - tests as a def's value, an argument, under not and with or`` () =
    let source =
        normalized """type Item = #foo(string) | #bar(string) | #baz | #quux

def items: list(Item) = [#foo("one"), #bar("two"), #baz, #quux]
def isQuux = #quux is #quux
def show = { b: bool -> if b then "yes" else "no" }
def either = { x: Item -> x is #baz or x is #quux }

println(show(isQuux))
println(show(#baz is #quux))
println("{items \list.filter { x -> not (x is #foo(_)) } \list.length}")
println("{items \list.filter(either) \list.length}")
"""
    runSource source |> assertRan "yes\nno\n3\n2\n"

[<Fact>]
let ``Run - an and chain uses the names its tests bind, outside an if`` () =
    let source =
        normalized """def big = { o: #some(int) | #none -> o is #some(v) and v > 3 }
def sum = { a: #some(int) | #none, b: #some(int) | #none -> a is #some(x) and b is #some(y) and x + y > 3 }
def show = { b: bool -> if b then "T" else "F" }

println(show(big(#some(5))) ++ show(big(#some(1))) ++ show(big(#none)))
println(show(sum(#some(1), #some(5))) ++ show(sum(#some(1), #some(1))) ++ show(sum(#none, #some(9))))
"""
    runSource source |> assertRan "TFF\nTFF\n"

[<Fact>]
let ``Run - an if's chain binds its names in the then branch, and its else runs once`` () =
    let source =
        normalized """def describe = { o: #some(int) | #none, flag: bool ->
  if flag and o is #some(v) and v > 3 then "big {v}" else "other"
}
def add = { a: #some(int) | #none, b: #some(int) | #none -> if a is #some(x) and b is #some(y) then x + y else 0 }
def classify = { o: #some(int) | #none ->
  if
    | o is #some(n) and n > 10 -> "large {n}"
    | o is #some(n) -> "small {n}"
    | else -> "none"
}

println(describe(#some(9), true))
println(describe(#some(9), false))
println(describe(#some(1), true))
println("{add(#some(1), #some(2))} {add(#none, #some(2))}")
println(classify(#some(11)))
println(classify(#some(1)))
println(classify(#none))
"""
    runSource source |> assertRan "big 9\nother\nother\n3 0\nlarge 11\nsmall 1\nnone\n"
