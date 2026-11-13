module Nanyx.Compiler.Tests.Features.UseDischarge

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A `use` may give a context's member a lambda that needs more contexts than the member's type declares. They are
// required where the `use` is written (#1054).

let private log = """import nanyx/console

context Log = (log: string -> ())

def greet: [Log] string -> () = { name ->
  log("hello {name}")
}
"""

[<Fact>]
let ``A use may give a member a lambda that needs more contexts, which the def around the use requires`` () =
    let source =
        log
        + """
@entry
def main = {
  use Log(
    log = { message ->
      println("")
      println(message)
    }
  )
  greet("world")
}
"""
    let source = source.ReplaceLineEndings "\n"
    Assert.Equal(Some "def main: [Console] () -> ()", hoverType source "main")
    Assert.Equal(Some "message: string", hoverType source "message")

[<Fact>]
let ``A member's lambda calls the instance in scope where the use is written`` () =
    compileClean (
        log
        + """
def tagged: [Log] () -> () = {
  use Log(log = { m -> log("[app] {m}") })
  greet("world")
}
"""
        |> _.ReplaceLineEndings("\n")
    )
    |> ignore

[<Fact>]
let ``A context the member's lambda needs that the def does not declare is missing at the call that needs it`` () =
    let source =
        log
        + """
context Log2 = (log2: string -> ())

def main: [Log2] () -> () = {
  use Log(log = { m -> println(m) })
  greet("world")
}
"""
    let code, text = singleError (source.ReplaceLineEndings "\n")
    Assert.Equal(DiagnosticCodes.UndeclaredContext.Code, code)
    Assert.Equal("println", text)

[<Fact>]
let ``An instance built as a value cannot give a member a lambda that needs more contexts`` () =
    let source =
        log
        + """
def inst = Log(log = { m -> println(m) })
"""
    let code, text = singleError (source.ReplaceLineEndings "\n")
    Assert.Equal(DiagnosticCodes.ContextsNotAllowed.Code, code)
    Assert.Equal("Log(log = { m -> println(m) })", text)

[<Fact>]
let ``A def's result cannot be a lambda that needs a context its type does not say`` () =
    let source =
        """
import nanyx/console

def printer: [Console] () -> (string -> ()) = {
  { m: string -> println(m) }
}
"""
    let code, text = singleError (source.ReplaceLineEndings "\n")
    Assert.Equal(DiagnosticCodes.ContextsNotAllowed.Code, code)
    Assert.Equal("{ m: string -> println(m) }", text)
