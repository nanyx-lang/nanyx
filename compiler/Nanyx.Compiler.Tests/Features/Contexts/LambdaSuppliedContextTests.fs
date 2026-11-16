module Nanyx.Compiler.Tests.Features.LambdaSuppliedContexts

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A lambda passed where its parameter's type requires a context can use that context: the function it is passed to
// supplies it, as `withLog` supplies `Log` to its block (#1218).

let private withLog = """context Log = (log: string -> ())

def withLog: [Console, c] ([Log, c] () -> ()) -> () = { f ->
  use Log(log = { m -> println("[log] {m}") }) in f()
}

def greet: [Log] string -> () = { name -> log("hello {name}") }
"""

[<Fact>]
let ``A lambda passed where the parameter requires a context can call what requires it and its members`` () =
    compileClean (
        withLog
        + """
def main: [Console] () -> () = {
  withLog {
    greet("Ada")
    log("direct")
    println("plain")
  }
  withLog({ log("in brackets") })
}
"""
    )
    |> ignore

[<Fact>]
let ``A lambda passed as one of several arguments can use the context its parameter requires`` () =
    compileClean """context Log = (log: string -> ())

def withPrefix: [Console, c] string, ([Log, c] () -> ()) -> () = { prefix, f ->
  use Log(log = { m -> println("[{prefix}] {m}") }) in f()
}

def main: [Console] () -> () = { withPrefix("app", { log("started") }) }
"""
    |> ignore

[<Fact>]
let ``A lambda passed where the parameter does not require the context still cannot use it`` () =
    let source =
        withLog
        + """
def run: [c] ([c] () -> ()) -> () = { f -> f() }

def main: [Console] () -> () = { run { greet("Ada") } }
"""
    Assert.Equal((DiagnosticCodes.MissingContextMembers.Code, "greet"), singleError source)

[<Fact>]
let ``A context the lambda uses besides its parameter's is an error where the parameter has no context variable`` () =
    let source = """context Log = (log: string -> ())

def withLog: ([Log] () -> ()) -> () = { f ->
  use Log(log = { m -> () }) in f()
}

def main = {
  withLog {
    log("a")
    println("extra")
  }
}
"""
    let code, _ = singleError source
    Assert.Equal(DiagnosticCodes.ContextsNotAllowed.Code, code)

[<Fact>]
let ``An inner handler's lambda uses the inner handler's instance`` () =
    compileClean (
        withLog
        + """
def main: [Console] () -> () = {
  withLog {
    greet("Ada")
    withLog { greet("Bob") }
    log("back")
  }
}
"""
    )
    |> ignore

[<Fact>]
let ``A module block's function supplies its context to a lambda in the module that imports the block`` () =
    let source = """
module db =
  export context Tx = (commit: () -> ())
  export def transaction: [c] ([Tx, c] () -> a) -> a = { body -> use Tx(commit = { () }) in body() }

import db

def f: [Tx] () -> () = { commit() }
def main = transaction { f() }
def direct = transaction { commit() }
"""
    let typed = compileClean (source.ReplaceLineEndings "\n")
    Assert.Equal("()", Unifier.tyToString typed.Types.["main"])

[<Fact>]
let ``So does an imported module's function`` () =
    let moduleSource = """module mod

export context Tx = (commit: () -> ())
export def transaction: [c] ([Tx, c] () -> a) -> a = { body -> use Tx(commit = { () }) in body() }
"""
    let mainSource = """import mod

def f: [Tx] () -> () = { commit() }
def main = transaction { f() }
"""
    let result = compileImporting (moduleSource.ReplaceLineEndings "\n") (mainSource.ReplaceLineEndings "\n")
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

// A parameterised context the parameter's type requires is given at the types it is applied to (#1512).

let private withMonoid = """context Monoid(a) = (empty: a, combine: (a, a) -> a)

def e: [Monoid(a)] () -> a = { empty }
def twice: [Monoid(a)] a -> a = { x -> combine(x, x) }
def withM: ([Monoid(int)] () -> b) -> b = { f -> use Monoid(int)(empty = 0, combine = { x, y -> x + y }) in f() }
"""

[<Fact>]
let ``A lambda passed where the parameter requires a parameterised context is given its instance`` () =
    let source =
        withMonoid
        + """
def c: int = withM { e() }
def d: int = withM { combine(empty, e()) }
def nested: list(int) = withM { [1, 2] \list.map { x -> combine(x, e()) } }
"""
    let typed = compileClean (source.ReplaceLineEndings "\n")
    Assert.Equal("int", Unifier.tyToString typed.Types.["c"])

[<Fact>]
let ``The instance a lambda is given is at the parameter's types only`` () =
    let source =
        withMonoid
        + """
def s: string = withM { twice("a") }
"""
    Assert.Equal((DiagnosticCodes.MissingContextMembers.Code, "twice"), singleError (source.ReplaceLineEndings "\n"))