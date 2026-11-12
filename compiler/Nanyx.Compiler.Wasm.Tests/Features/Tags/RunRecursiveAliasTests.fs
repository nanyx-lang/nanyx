module TranspilerWasmTests.Features.Tags.RunRecursiveAlias

open Xunit
open TranspilerWasmTestHelpers

// A tag union alias that names itself in its tags' payloads (#246). Each program runs with its memory checked, so a
// tree that is not freed, or freed twice, fails the test.

let private run (source: string) = runSource (source.ReplaceLineEndings "\n")

let private tree = """
import nanyx/console

type Tree(a) = #leaf | #node(Tree(a), a, Tree(a))

def insert: Tree(int), int -> Tree(int) = { t, v ->
  match t
    | #leaf -> #node(#leaf, v, #leaf)
    | #node(l, x, r) -> if v < x then #node(insert(l, v), x, r) else #node(l, x, insert(r, v))
}

def sum: Tree(int) -> int = {
  | #leaf -> 0
  | #node(l, x, r) -> sum(l) + x + sum(r)
}

def size: Tree(a) -> int = {
  | #leaf -> 0
  | #node(l, _, r) -> size(l) + 1 + size(r)
}

def mapTree: Tree(a), (a -> b) -> Tree(b) = { t, f ->
  match t
    | #leaf -> #leaf
    | #node(l, x, r) -> #node(mapTree(l, f), f(x), mapTree(r, f))
}

def inOrder: Tree(string) -> string = {
  | #leaf -> ""
  | #node(l, x, r) -> inOrder(l) ++ x ++ inOrder(r)
}
"""

[<Fact>]
let ``Run - a recursive alias's values are evaluated by a recursive function`` () =
    let source = """
import nanyx/console

type Expr = #num(int) | #add(Expr, Expr)

def eval: Expr -> int = {
  | #num(n) -> n
  | #add(a, b) -> eval(a) + eval(b)
}

println("{eval(#add(#num(2), #num(3)))}")
"""
    run source |> assertRan "5\n"

[<Fact>]
let ``Run - a generic recursive alias is built, matched, mapped and freed`` () =
    let source = tree + """
def t = insert(insert(insert(insert(#leaf, 5), 3), 8), 1)
def names = mapTree(t, { n -> "<{n}>" })
println("{sum(t)} {size(t)} {size(names)}")
println(inOrder(names))
"""
    run source |> assertRan "17 4 4\n<1><3><5><8>\n"

[<Fact>]
let ``Run - a large tree built in a loop is freed`` () =
    let source = tree + """
def build: int, Tree(int) -> Tree(int) = { n, t ->
  if n == 0 then t else build(n - 1, insert(t, (n * 37) % 101))
}

def t = build(200, #leaf)
println("{size(t)} {sum(t)}")
"""
    run source |> assertRan "200 10036\n"

[<Fact>]
let ``Run - values of a recursive alias are compared by their structure`` () =
    let source = """
import nanyx/console

type Expr = #num(int) | #neg(Expr) | #add(Expr, Expr)

def same: Expr, Expr -> string = { a, b -> if a == b then "same" else "different" }

println(same(#add(#num(1), #neg(#num(2))), #add(#num(1), #neg(#num(2)))))
println(same(#add(#num(1), #neg(#num(2))), #add(#num(1), #neg(#num(3)))))
"""
    run source |> assertRan "same\ndifferent\n"

[<Fact>]
let ``Run - a recursive alias holds another`` () =
    let source = tree + """
def forest: Tree(Tree(int)) = #node(#leaf, insert(insert(#leaf, 2), 4), #node(#leaf, insert(#leaf, 7), #leaf))

def total: Tree(Tree(int)) -> int = {
  | #leaf -> 0
  | #node(l, inner, r) -> total(l) + sum(inner) + total(r)
}

println("{total(forest)} {size(forest)}")
"""
    run source |> assertRan "13 2\n"

[<Fact>]
let ``Run - a list of a recursive alias's values`` () =
    let source = """
import nanyx/console

type Expr = #num(int) | #add(Expr, Expr)

def eval: Expr -> int = {
  | #num(n) -> n
  | #add(a, b) -> eval(a) + eval(b)
}

def sum: Expr = #add(#num(2), #num(3))
def nested: Expr = #add(#add(#num(4), #num(5)), #num(6))
def exprs: list(Expr) = [ #num(1), sum, nested ]

for exprs | e -> println("{eval(e)}")
"""
    run source |> assertRan "1\n5\n15\n"
