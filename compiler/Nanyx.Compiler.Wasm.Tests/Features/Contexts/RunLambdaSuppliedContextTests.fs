/// A lambda passed where its parameter's type requires a context, which the function it is passed to supplies: the
/// reusable handler, `withLog { ... }` (#1218).
module TranspilerWasmTests.Features.Contexts.LambdaSuppliedContexts

open Xunit
open TranspilerWasmTestHelpers

let private withPrefix = """import nanyx/console

context Log = (log: string -> ())

def withPrefix: [Console & c] string, ([Log & c] () -> ()) -> () = { prefix, f ->
  use Log(log = { m -> println("[{prefix}] {m}") }) in f()
}

def withLog: [Console & c] ([Log & c] () -> ()) -> () = { f -> withPrefix("log", f) }

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
let ``Run - the Context escape page's reusable handler`` () =
    let source = """import nanyx/console

context Log = (log: string -> ())

def greet: [Log] string -> () = { name ->
  log("hello {name}")
}

def withConsoleLog: [Console & c] ([Log & c] () -> a) -> a = { body ->
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
