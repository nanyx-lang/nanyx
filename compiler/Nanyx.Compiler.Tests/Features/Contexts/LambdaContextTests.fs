module Nanyx.Compiler.Tests.Features.LambdaContexts

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A lambda's type carries the contexts it uses, which it requires where it is called: `{ n -> println(n) }` is
// `[Console] int -> ()`. A function that requires fewer contexts fits where one that requires more is expected.

let private typeOf (source: string) (name: string) =
    Unifier.tyToString (compileClean source).Types.[name]

let private mk =
    """
import nanyx/console

def mk: [Console] () -> ([Console] int -> int) = {
  { n ->
    println("side effect {n}")
    n
  }
}
"""

let private withMk (source: string) = $"{mk}{source}"

[<Fact>]
let ``A lambda's type carries the contexts it uses`` () =
    Assert.Equal("() -> ([Console] int -> int)", typeOf mk "mk")
    Assert.Equal(Some "[Console] int -> int", hoverType mk "{ n ->")

[<Fact>]
let ``A function that requires a context cannot be passed where a pure one is expected`` () =
    let code, text =
        singleError (
            withMk
                """
def runPure: (int -> int) -> int = { g -> g(5) }

def main: [Console] () -> int = { runPure(mk()) }
"""
        )
    Assert.Equal(DiagnosticCodes.ContextsNotAllowed.Code, code)
    Assert.Equal("mk()", text)

[<Fact>]
let ``A function that takes a context variable runs a lambda that requires a context`` () =
    compileClean (
        withMk
            """
def run: [c] ([c] int -> int) -> int = { g -> g(5) }

def main: [Console] () -> int = { run(mk()) }
"""
    )
    |> ignore

[<Fact>]
let ``Calling a function value requires the contexts its type says`` () =
    let code, text =
        singleError
            """
import nanyx/console

def call: ([Console] int -> int) -> int = { g -> g(5) }
"""
    Assert.Equal(DiagnosticCodes.UndeclaredContext.Code, code)
    Assert.Equal("g", text)

[<Fact>]
let ``A lambda that returns a function requiring a context must say so`` () =
    let code, _ =
        singleError
            """
import nanyx/console

def mk: [Console] () -> (int -> int) = {
  { n ->
    println("side effect {n}")
    n
  }
}
"""
    Assert.Equal(DiagnosticCodes.ContextsNotAllowed.Code, code)

[<Fact>]
let ``A context at a concrete type is settled where the lambda is made`` () =
    // The interpolation finds its `ToString(int)` where the lambda is made, so the lambda's type does not carry it.
    let source =
        """
def describe = { n: int -> "{n}" }
"""
    Assert.Equal("int -> string", typeOf source "describe")

[<Fact>]
let ``A function named as a value has its contexts in its type`` () =
    let source =
        """
import nanyx/console

def show: [Console] int -> () = { n -> println(n) }

def main: [Console] () -> () = {
  def f = show
  f(1)
}
"""
    Assert.Equal(Some "f: [Console] int -> ()", hoverType source "f(1)")

[<Fact>]
let ``println passes to list.map in a def that declares Console`` () =
    compileClean
        """
import nanyx/console

def main: [Console] list(int) -> list(()) = { xs -> xs \list.map(println) }

def each: [Console] list(int) -> list(int) = { xs ->
  xs \list.map { n ->
    println(n)
    n + 1
  }
}
"""
    |> ignore

[<Fact>]
let ``A lambda that requires Console cannot be mapped in a def that does not declare it`` () =
    let code, text =
        singleError
            """
import nanyx/console

def each: list(int) -> list(int) = { xs ->
  xs \list.map { n ->
    println(n)
    n + 1
  }
}
"""
    Assert.Equal(DiagnosticCodes.UndeclaredContext.Code, code)
    Assert.Equal("println", text)

[<Fact>]
let ``A function value that requires Console makes list.map require it`` () =
    let code, text =
        singleError
            """
import nanyx/console

def each: ([Console] int -> ()) -> list(()) = { g -> [1, 2] \list.map(g) }
"""
    Assert.Equal(DiagnosticCodes.UndeclaredContext.Code, code)
    Assert.Equal("list.map", text)

[<Fact>]
let ``A pure lambda passes to each of the prelude's higher-order functions`` () =
    compileClean
        """
def xs = [3, 1, 2]
def mapped = xs \list.map { x -> x + 1 }
def kept = xs \list.filter { x -> x > 1 }
def total = xs \list.fold(0, { acc, x -> acc + x })
def found = xs \list.find { x -> x == 2 }
def some = xs \list.any { x -> x > 2 }
def every = xs \list.all { x -> x > 0 }
def counted = xs \list.count { x -> x > 1 }
def sorted = xs \list.sortBy { x -> 0 - x }
def grouped = xs \list.groupBy { x -> x % 2 }
def flat = xs \list.flatMap { x -> [x, x] }
def indexed = xs \list.mapIndexed { i, x -> i + x }
def parts = xs \list.partition { x -> x > 1 }
def option = #some(1) \mapSome { x -> x + 1 }
def result: Result(int, string) = Result.map(#some(1), { x -> x + 1 })
def counts = map.fromList([("a", 1)]) \map.map { v -> v + 1 }
def odd = set.fromList(xs) \set.filter { x -> x % 2 == 1 }
def cell = memory { 1 }
"""
    |> ignore

[<Fact>]
let ``An unannotated higher-order function infers a context variable`` () =
    let source =
        """
import nanyx/console

def apply = { f, x -> f(x) }

def twice = { f, xs -> list.map(xs, f) }

def show: [Console] int -> () = { n -> println(n) }
"""
    Assert.Equal(Some "def apply: [c] ([c] α -> β), α -> β", hoverType source "apply =")
    Assert.Equal(Some "def twice: [c] ([c] α -> β), list(α) -> list(β)", hoverType source "twice =")

[<Fact>]
let ``Calling an inferred higher-order function with a function that requires Console requires it`` () =
    let code, text =
        singleError
            """
import nanyx/console

def apply = { f, x -> f(x) }

def run: ([Console] int -> ()) -> () = { g -> apply(g, 1) }
"""
    Assert.Equal(DiagnosticCodes.UndeclaredContext.Code, code)
    Assert.Equal("apply", text)

[<Fact>]
let ``Calling a member that declares a context requires it`` () =
    let source =
        """
import nanyx/console

context Log = (log: [Console] string -> ())

def heading: [Log] string -> () = { title -> log(title) }
"""
    let code, text = singleError source
    Assert.Equal(DiagnosticCodes.UndeclaredContext.Code, code)
    Assert.Equal("log", text)
    compileClean (
        source.Replace("def heading: [Log]", "def heading: [Log & Console]")
        + """
def main: [Console] () -> () = {
  use Log(log = { (s: string) -> println(s) })
  heading("hi")
}
"""
    )
    |> ignore
[<Fact>]
let ``A lambda does not require a context that a use inside it supplies`` () =
    let source =
        """
context Log = (log: string -> ())

def greet: [Log] string -> () = { name -> log("hello {name}") }

def nested = {
  { use Log(log = { m -> () }) in greet("inner") }
}

def blocky = {
  {
    use Log(log = { m -> () })
    greet("block")
  }
}

def before: [Log] () -> ([Log] () -> ()) = {
  {
    greet("first")
    use Log(log = { m -> () })
    greet("second")
  }
}
"""
    let source = source.ReplaceLineEndings "\n"
    Assert.Equal(Some "def nested: () -> () -> ()", hoverType source "nested")
    Assert.Equal(Some "def blocky: () -> () -> ()", hoverType source "blocky")