/// A lambda passed where its parameter's type requires a context, which the function it is passed to supplies: the
/// reusable handler, `withLog { ... }` (#1218).
module TranspilerWasmTests.Features.Contexts.LambdaSuppliedContexts

open Xunit
open TranspilerWasmTestHelpers

let private withPrefix = """context Log = (log: string -> ())

def withPrefix: [Console, c] string, ([Log, c] () -> ()) -> () = { prefix, f ->
  use Log(log = { m -> println("[{prefix}] {m}") }) in f()
}

def withLog: [Console, c] ([Log, c] () -> ()) -> () = { f -> withPrefix("log", f) }

def greet: [Log] string -> () = { name -> log("hello {name}") }
"""

[<Fact>]
let ``Run - a handler's lambda calls what requires its context, and its own contexts bind the context variable`` () =
    let source =
        withPrefix
        + """
def main = {
  withLog {
    greet("Ada")
    println("plain")
  }
}

main()
"""
    runSource source |> assertRan "[log] hello Ada\nplain\n"

[<Fact>]
let ``Run - a handler's lambda calls the context's member, written in brackets or among other arguments`` () =
    let source =
        withPrefix
        + """
withLog { log("direct") }
withLog({ log("in brackets") })
withPrefix("app", { log("started") })
"""
    runSource source |> assertRan "[log] direct\n[log] in brackets\n[app] started\n"

[<Fact>]
let ``Run - the Contexts page's reusable handler`` () =
    let source = """context Log = (log: string -> ())

def greet: [Log] string -> () = { name ->
  log("hello {name}")
}

def withConsoleLog: [Console, c] ([Log, c] () -> a) -> a = { body ->
  use Log(log = { m -> println(m) })
  body()
}

@entry
def main = {
  withConsoleLog {
    greet("world")
    log("done")
  }
}
"""
    runSource source |> assertRan "hello world\ndone\n"

[<Fact>]
let ``Run - an inner handler's lambda uses the inner instance`` () =
    let source =
        withPrefix
        + """
def main = {
  withPrefix("outer") {
    greet("Ada")
    withPrefix("inner") { greet("Bob") }
    log("back")
  }
}

main()
"""
    runSource source |> assertRan "[outer] hello Ada\n[inner] hello Bob\n[outer] back\n"

[<Fact>]
let ``Run - a block that calls two functions requiring a scoped context is given it once`` () =
    let source =
        """scoped context Tx = private ()

def transaction: [c] ([Tx, c] () -> a) -> a = { block -> block() }
def step: [Tx] int -> int = { n -> n + 1 }

println(transaction { step(step(1)) })
"""
    runSource (normalized source) |> assertRan "3\n"

[<Fact>]
let ``Run - a module block's function supplies its context to a lambda in the module that imports the block`` () =
    let source =
        """module db =
  export context Tx = (commit: () -> ())
  export def transaction: [c, Console] ([Tx, c] () -> a) -> a = { body -> use Tx(commit = { println("committed") }) in body() }

import db

transaction { commit() }
"""
    runSource (normalized source) |> assertRan "committed\n"

let private monoids = """context Monoid(a) = (empty: a, combine: (a, a) -> a)

def reduce: [Monoid(a)] list(a) -> a = { xs -> xs \fold(empty) { acc, x -> combine(acc, x) } }
def additive: ([Monoid(int)] () -> b) -> b = { f -> use Monoid(int)(empty = 0, combine = { x, y -> x + y }) in f() }
def multiplicative: ([Monoid(int)] () -> b) -> b = { f -> use Monoid(int)(empty = 1, combine = { x, y -> x * y }) in f() }
def joined: ([Monoid(string)] () -> b) -> b = { f -> use Monoid(string)(empty = "", combine = { x, y -> x ++ y }) in f() }
"""

[<Fact>]
let ``Run - a handler gives its lambda a parameterised context's instance`` () =
    let source =
        monoids
        + """
println(additive { reduce([1, 2, 3, 4]) })
println(multiplicative { reduce([1, 2, 3, 4]) })
println(additive { combine(empty, 5) })
println(joined { reduce(["a", "b"]) ++ additive { "{reduce([4, 5])}" } })
"""
    runSource (normalized source) |> assertRan "10\n24\n5\nab9\n"

[<Fact>]
let ``Run - a lambda inside a handler's lambda uses the instance it is given`` () =
    let source =
        monoids
        + """
println(additive { [1, 2, 3] \list.map { x -> combine(x, reduce([x, 10])) } \reduce })
"""
    runSource (normalized source) |> assertRan "42\n"