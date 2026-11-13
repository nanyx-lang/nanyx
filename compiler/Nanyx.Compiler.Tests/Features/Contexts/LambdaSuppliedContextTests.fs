module Nanyx.Compiler.Tests.Features.LambdaSuppliedContexts

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A lambda passed where its parameter's type requires a context can use that context: the function it is passed to
// supplies it, as `withLog` supplies `Log` to its block (#1218).

let private withLog = """import nanyx/console

context Log = (log: string -> ())

def withLog: [Console & c] ([Log & c] () -> ()) -> () = { f ->
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
    compileClean """import nanyx/console

context Log = (log: string -> ())

def withPrefix: [Console & c] string, ([Log & c] () -> ()) -> () = { prefix, f ->
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
    let source = """import nanyx/console

context Log = (log: string -> ())

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
