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

def transaction: [c] ([Tx, c] () -> a) -> a = { block -> use Tx() in block() }
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

def combineAll: [Monoid(a)] list(a) -> a = { xs -> xs \fold(empty) { acc, x -> combine(acc, x) } }
def additive: ([Monoid(int)] () -> b) -> b = { f -> use Monoid(int)(empty = 0, combine = { x, y -> x + y }) in f() }
def multiplicative: ([Monoid(int)] () -> b) -> b = { f -> use Monoid(int)(empty = 1, combine = { x, y -> x * y }) in f() }
def joined: ([Monoid(string)] () -> b) -> b = { f -> use Monoid(string)(empty = "", combine = { x, y -> x ++ y }) in f() }
"""

[<Fact>]
let ``Run - a handler gives its lambda a parameterised context's instance`` () =
    let source =
        monoids
        + """
println(additive { combineAll([1, 2, 3, 4]) })
println(multiplicative { combineAll([1, 2, 3, 4]) })
println(additive { combine(empty, 5) })
println(joined { combineAll(["a", "b"]) ++ additive { "{combineAll([4, 5])}" } })
"""
    runSource (normalized source) |> assertRan "10\n24\n5\nab9\n"

[<Fact>]
let ``Run - a lambda inside a handler's lambda uses the instance it is given`` () =
    let source =
        monoids
        + """
println(additive { [1, 2, 3] \list.map { x -> combine(x, combineAll([x, 10])) } \combineAll })
"""
    runSource (normalized source) |> assertRan "42\n"
// A lambda given an instance of a parameterised context finds that instance's members by their names, even inside a
// `use` of the same context at other type arguments, whose members the lambda captures as well (#2570).

let private gathering =
    """context Pushed(a) = (push: a -> ())

def gather: [c] ([c, Pushed(b)] () -> ()) -> list(b) = { block ->
  memory {
    mut found = []
    use Pushed(b)(push = { x -> found := [x, ...found] }) in block()
    list.reverse(found)
  }
}

def gatherInts: ([Pushed(int)] () -> ()) -> list(int) = { block ->
  memory {
    mut found = []
    use Pushed(int)(push = { x -> found := [x, ...found] }) in block()
    list.reverse(found)
  }
}

def pushTwice: [Pushed(int)] int -> () = { n ->
  push(n)
  push(n * 10)
}
"""

[<Fact>]
let ``Run - a lambda inside a use of the same context at another type finds the instance it is given`` () =
    let source =
        gathering
        + """
@entry
def main = {
  use Pushed(string)(push = { s -> println("outer {s}") }) in
    println(gather { push(1) })
    println(gatherInts { pushTwice(2) })
    push("still outer")
}
"""
    runSource (normalized source) |> assertRan "[1]\n[2, 20]\nouter still outer\n"

[<Fact>]
let ``Run - a use inside the lambda of the same context at another type is the one its own calls find`` () =
    let source =
        gathering
        + """
@entry
def main = {
  println(gather {
    push(0)
    use Pushed(string)(push = { s -> println("inner {s}") }) in push("a")
  })
}
"""
    runSource (normalized source) |> assertRan "inner a\n[0]\n"
