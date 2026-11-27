module Nanyx.Compiler.Tests.Features.Types.MutuallyRecursiveTypes

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// Types that name each other are closed together, as a group (see `RecursiveAliases`, #3186).

/// `source`, its line endings made `\n`, compiled with no errors.
let private clean (source: string) = compileClean (source.ReplaceLineEndings "\n")

let private syntax = """
type Expr = (id: int, kind: #num(int) | #lambda(list(Pattern), Expr) | #block(list(Statement)))

type Pattern = (id: int, kind: #bind(string) | #guard(Expr) | #pair(Pattern, Pattern))

type Statement = (id: int, kind: #def(Pattern, Expr) | #expr(Expr))

def count: Expr -> int = { e ->
  match e.kind
    | #num(_) -> 1
    | #lambda(ps, body) -> 1 + list.sum(list.map(ps, countPattern)) + count(body)
    | #block(ss) -> 1 + list.sum(list.map(ss, countStatement))
}

def countPattern: Pattern -> int = { p ->
  match p.kind
    | #bind(_) -> 1
    | #guard(e) -> 1 + count(e)
    | #pair(a, b) -> 1 + countPattern(a) + countPattern(b)
}

def countStatement: Statement -> int = { s ->
  match s.kind
    | #def(p, e) -> 1 + countPattern(p) + count(e)
    | #expr(e) -> 1 + count(e)
}
"""

[<Fact>]
let ``Aliases that name each other through records and lists are one recursive type each`` () =
    let source = syntax + """
def x: Expr = (id = 1, kind = #num(1))
def p: Pattern = (id = 2, kind = #pair((id = 3, kind = #bind("a")), (id = 4, kind = #guard(x))))
def tree = count((id = 5, kind = #block([(id = 6, kind = #def(p, x)), (id = 7, kind = #expr(x))])))
"""
    let typed = clean source
    Assert.Equal("Expr -> int", asciiTypeIn typed "count")
    Assert.Equal("Pattern -> int", asciiTypeIn typed "countPattern")
    Assert.Equal("int", asciiTypeIn typed "tree")

[<Fact>]
let ``A type can name one of a group declared below it`` () =
    let typed =
        clean """
type Program = (main: Statement)

type Statement = #print(Value) | #block(list(Statement))

type Value = #text(string) | #run(Statement)

def depth: Statement -> int = {
  | #print(#run(s)) -> 1 + depth(s)
  | #print(_) -> 1
  | #block(ss) -> 1 + list.sum(list.map(ss, depth))
}

def program: Program = (main = #block([#print(#text("hi")), #print(#run(#block([])))]))
def result = depth(program.main)
"""
    Assert.Equal("int", asciiTypeIn typed "result")

[<Fact>]
let ``Generic aliases that name each other are the same type at the same arguments only`` () =
    let source = """
type Tree(a) = #leaf(a) | #forest(Forest(a))

type Forest(a) = (trees: list(Tree(a)))

def leaves: Tree(a) -> int = {
  | #leaf(_) -> 1
  | #forest(f) -> list.sum(list.map(f.trees, leaves))
}

def labels: Tree(a), (a -> b) -> Tree(b) = { t, f ->
  match t
    | #leaf(x) -> #leaf(f(x))
    | #forest(forest) -> #forest((trees = list.map(forest.trees, { tree -> labels(tree, f) })))
}

def numbers: Tree(int) = #forest((trees = [#leaf(1), #forest((trees = [#leaf(2)]))]))
def names = labels(numbers, { n -> "{n}" })
"""
    let typed = clean source
    Assert.Equal("Tree(a) -> int", asciiTypeIn typed "leaves")
    Assert.Equal("Tree(string)", asciiTypeIn typed "names")
    let code, _ = singleError ((source + "def wrong: Tree(int) = names\n").ReplaceLineEndings "\n")
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)

[<Fact>]
let ``Nominal types that name each other are built and taken apart by their names`` () =
    let typed =
        clean """
type Expr = protected (id: int, kind: #num(int) | #block(list(Statement)))

type Statement = protected (id: int, expr: Expr)

def size: Expr -> int = { e ->
  match e.kind
    | #num(_) -> 1
    | #block(ss) -> 1 + list.sum(list.map(ss, { s -> size(s.expr) }))
}

def one = Expr(id = 1, kind = #num(1))
def result = size(Expr(id = 2, kind = #block([Statement(id = 3, expr = one)])))
"""
    Assert.Equal("Expr", asciiTypeIn typed "one")
    Assert.Equal("int", asciiTypeIn typed "result")

[<Fact>]
let ``A nominal type and an alias can name each other`` () =
    let typed =
        clean """
type Node = protected (value: int, children: Children)

type Children = #none | #some(list(Node))

def total: Node -> int = { n ->
  match n.children
    | #none -> n.value
    | #some(ns) -> n.value + list.sum(list.map(ns, total))
}

def result = total(Node(value = 1, children = #some([Node(value = 2, children = #none)])))
"""
    Assert.Equal("int", asciiTypeIn typed "result")

[<Fact>]
let ``Types that name each other can be imported`` () =
    let library = """
module mod

export type Expr = #num(int) | #block(list(Statement))

export type Statement = (expr: Expr, label: string)

export def size: Expr -> int = {
  | #num(_) -> 1
  | #block(ss) -> 1 + list.sum(list.map(ss, { s -> size(s.expr) }))
}
"""
    let main = """
module main

import mod

def labelled: Expr -> Statement = { e -> (expr = e, label = "x") }
def result = size(#block([labelled(#num(1))]))
"""
    let result = compileImporting (library.ReplaceLineEndings "\n") (main.ReplaceLineEndings "\n")
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal("int", Unifier.tyToString result.Typed.Value.Types.["result"])

[<Fact>]
let ``Aliases that name each other with no tag in between are an error at each name, naming the cycle`` () =
    let result = Compiler.compile ("type A = (int, B)\n\ntype B = (A, string)\n".ReplaceLineEndings "\n")
    let errors = result.Diagnostics |> List.filter Diagnostics.isError
    Assert.Equal<string list>([ DiagnosticCodes.UnguardedRecursiveAlias.Code; DiagnosticCodes.UnguardedRecursiveAlias.Code ], errors |> List.map _.Code)
    Assert.All(errors, fun error -> Assert.Contains("The aliases 'A', 'B' name each other with no tag in between", error.Message))
