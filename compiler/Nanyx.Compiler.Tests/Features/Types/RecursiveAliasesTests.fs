module Nanyx.Compiler.Tests.Features.Types.RecursiveAliases

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// `source`, its line endings made `\n`, compiled with no errors.
let private clean (source: string) = compileClean (source.ReplaceLineEndings "\n")

/// The code of the one diagnostic `source`, its line endings made `\n`, compiles to, and the text it is reported at.
let private error (source: string) = singleError (source.ReplaceLineEndings "\n")

let private expr = """
type Expr = #num(int) | #add(Expr, Expr)

def eval: Expr -> int = {
  | #num(n) -> n
  | #add(a, b) -> eval(a) + eval(b)
}
"""

let private tree = """
type Tree(a) = #leaf | #node(Tree(a), a, Tree(a))

def size: Tree(a) -> int = {
  | #leaf -> 0
  | #node(l, _, r) -> size(l) + 1 + size(r)
}

def insert: Tree(int), int -> Tree(int) = { t, v ->
  match t
    | #leaf -> #node(#leaf, v, #leaf)
    | #node(l, x, r) -> if v < x then #node(insert(l, v), x, r) else #node(l, x, insert(r, v))
}

def mapTree: Tree(a), (a -> b) -> Tree(b) = { t, f ->
  match t
    | #leaf -> #leaf
    | #node(l, x, r) -> #node(mapTree(l, f), f(x), mapTree(r, f))
}
"""

[<Fact>]
let ``A tag union alias can name itself in its tags' payloads`` () =
    let typed = clean (expr + "def result = eval(#add(#num(2), #add(#num(3), #num(4))))\n")
    Assert.Equal("Expr -> int", asciiTypeIn typed "eval")
    Assert.Equal("int", asciiTypeIn typed "result")

[<Fact>]
let ``A recursive alias with a type parameter is built, matched and recursed over`` () =
    let source = tree + """
def t = insert(insert(#leaf, 5), 3)
def names = mapTree(t, { n -> "n{n}" })
def count = size(names)
"""
    let typed = clean source
    Assert.Equal("Tree(a) -> int", asciiTypeIn typed "size")
    Assert.Equal("Tree(a), (a -> b) -> Tree(b)", asciiTypeIn typed "mapTree")
    Assert.Equal("Tree(int)", asciiTypeIn typed "t")
    Assert.Equal("Tree(string)", asciiTypeIn typed "names")
    Assert.Equal("int", asciiTypeIn typed "count")

[<Fact>]
let ``A value that does not fit a recursive alias is a mismatch`` () =
    let code, _ = error (expr + "def result = eval(#add(#num(2), #neg(#num(3))))\n")
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)

[<Fact>]
let ``A recursive alias is the same type at the same arguments only`` () =
    let code, _ = error (tree + (withFinalNewline """def wrong: Tree(int) = mapTree(insert(#leaf, 1), { n -> "{n}" })"""))
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)

[<Fact>]
let ``A match on a recursive alias must cover each of its tags`` () =
    let source = """
type Expr = #num(int) | #add(Expr, Expr)

def eval: Expr -> int = {
  | #num(n) -> n
}
"""
    let code, _ = error source
    Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, code)

[<Fact>]
let ``Two recursive aliases of the same shape are the same type`` () =
    let source = """
type Expr = #num(int) | #add(Expr, Expr)
type Term = #num(int) | #add(Term, Term)

def asTerm: Expr -> Term = { e -> e }
def roundTrip: list(Term) -> list(Expr) = { terms -> terms }
"""
    let typed = clean source
    Assert.Equal("Expr -> Term", asciiTypeIn typed "asTerm")

[<Fact>]
let ``An alias of a recursive alias stands for it`` () =
    let source = tree + """
type Numbers = Tree(int)

def total: Numbers -> int = {
  | #leaf -> 0
  | #node(l, x, r) -> total(l) + x + total(r)
}

def result = total(insert(#leaf, 1))
"""
    let typed = clean source
    Assert.Equal("int", asciiTypeIn typed "result")

[<Fact>]
let ``A recursive alias can be imported`` () =
    let library = """
module mod

export type Expr = #num(int) | #add(Expr, Expr)

export def eval: Expr -> int = {
  | #num(n) -> n
  | #add(a, b) -> eval(a) + eval(b)
}
"""
    let main = """
module main

import mod

def double: Expr -> Expr = { e -> #add(e, e) }
def result = eval(double(#num(2)))
"""
    let result = compileImporting (library.ReplaceLineEndings "\n") (main.ReplaceLineEndings "\n")
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal("int", Unifier.tyToString result.Typed.Value.Types.["result"])

[<Fact>]
let ``An alias that names itself outside a tag is an error`` () =
    let code, text = error "type Pair = (int, Pair)\n"
    Assert.Equal(DiagnosticCodes.UnguardedRecursiveAlias.Code, code)
    Assert.Equal("Pair", text)

[<Fact>]
let ``An alias that is one of its own tags' alternatives is an error`` () =
    let code, text = error "type Loop = #stop | Loop\n"
    Assert.Equal(DiagnosticCodes.UnguardedRecursiveAlias.Code, code)
    Assert.Equal("Loop", text)

// A type written above the alias it names read the name before the alias was declared, so its uses didn't match the
// alias's union (#1838).
[<Fact>]
let ``A type can name a recursive alias declared below it`` () =
    clean """
type Box(a) = private (run: Shape -> a)

def area: Box(float) = Box(run = { _ -> 1.0 })

def runBox: Box(a), Shape -> a = { box, shape ->
  def run = box.run
  run(shape)
}

def probe = runBox(area, #group([#circle(1.0)]))

type Shape = #circle(float) | #group(list(Shape))
"""
    |> ignore

// A record alias that names itself was built from its closed definition, so `S(...)` was a type mismatch with `S`
// (#2213).
[<Fact>]
let ``A recursive record alias is built by its name`` () =
    let typed =
        clean """
type S = (next: () -> #next(int, S) | #done)

def range: int, int -> S = { first, last ->
  S(next = { if first > last then #done else #next(first, range(first + 1, last)) })
}

def s = range(1, 10)
"""
    Assert.Equal("S", asciiTypeIn typed "s")
