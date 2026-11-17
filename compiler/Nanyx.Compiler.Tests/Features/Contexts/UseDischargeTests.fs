module Nanyx.Compiler.Tests.Features.UseDischarge

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A `use` may give a context's member a lambda that needs more contexts than the member's type declares. They are
// required where the `use` is written (#1054).

let private log = """context Log = (log: string -> ())

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
let ``An instance built as a value is reported, even one whose member needs more contexts`` () =
    let source =
        log
        + """
def inst = Log(log = { m -> println(m) })

dbg(0)
"""
    let code, text = singleError (source.ReplaceLineEndings "\n")
    Assert.Equal(DiagnosticCodes.InstanceOutsideUse.Code, code)
    Assert.Equal("Log", text)

[<Fact>]
let ``A def's result cannot be a lambda that needs a context its type does not say`` () =
    let source =
        """
def printer: [Console] () -> (string -> ()) = {
  { m: string -> println(m) }
}
"""
    let code, text = singleError (source.ReplaceLineEndings "\n")
    Assert.Equal(DiagnosticCodes.ContextsNotAllowed.Code, code)
    Assert.Equal("{ m: string -> println(m) }", text)


let private choose = """context Choose = (choose: () -> bool)
context Move = (move: int -> int)

def chooser: [Choose] int -> int = { n -> if choose() then n else 1 }
"""

[<Fact>]
let ``A use may give a member a function by its name when an instance of each context it needs is in scope`` () =
    compileClean (
        choose
        + """
def moved = {
  use Choose(choose = { true })
  use Move(move = chooser)
  move(5)
}
"""
        |> _.ReplaceLineEndings("\n")
    )
    |> ignore

[<Fact>]
let ``A function named at a use that needs a context not in scope there is not a member's`` () =
    let source =
        choose
        + """
def moved = {
  use Move(move = chooser)
  move(5)
}
"""
    let code, text = singleError (source.ReplaceLineEndings "\n")
    Assert.Equal(DiagnosticCodes.ContextsNotAllowed.Code, code)
    Assert.Equal("Move(move = chooser)", text)
// So may `use C(T)(members)`, an instance of a parameterised context (#478).

[<Fact>]
let ``An instance of a parameterised context may give a member a lambda that needs more contexts`` () =
    let source = """context Y(b) = (y: b -> ())

def f: [Y(int)] () -> () = { y(1) }

def shown: [Console] () -> () = { use Y(int)(y = { x -> println(x) }) in f() }

def kept: () -> int = {
  memory {
    mut n = 0
    use Y(int)(y = { x -> n := x })
    f()
    n
  }
}
"""
    let source = source.ReplaceLineEndings "\n"
    let typed = compileClean source
    Assert.Equal(Some "def shown: [Console] () -> ()", hoverType source "shown")
    Assert.Equal("() -> int", Unifier.tyToString typed.Types.["kept"])

[<Fact>]
let ``What a parameterised instance's member needs besides is required where the use is written`` () =
    let source = """context Y(b) = (y: b -> ())

def f: [Y(int)] () -> () = { y(1) }

def shown: () -> () = { use Y(int)(y = { x -> println(x) }) in f() }
"""
    Assert.Equal((DiagnosticCodes.UndeclaredContext.Code, "println"), singleError (source.ReplaceLineEndings "\n"))
// A `for` loop's body runs where the loop is written, so a `use` around the loop supplies what it calls needs (#1757).

[<Fact>]
let ``A for loop's body may call a function value needing a context a use in the same block supplies`` () =
    let source = """context Emit(b) = (emit: b -> ())
context Note = (note: int -> ())

def each: list(int), ([Emit(int)] int -> ()) -> int = { xs, f ->
  memory {
    mut n = 0
    use Emit(int)(emit = { x -> n := n + 1 })
    for xs | x -> f(x)
    n
  }
}

def noted: list(int), ([Note] int -> ()) -> int = { xs, f ->
  memory {
    mut n = 0
    use Note(note = { x -> n := n + x })
    for xs | x -> f(x)
    n
  }
}
"""
    let typed = compileClean (source.ReplaceLineEndings "\n")
    Assert.Equal("list(int), ([Emit(int)] int -> ()) -> int", Unifier.tyToString typed.Types.["each"])
    Assert.Equal("list(int), ([Note] int -> ()) -> int", Unifier.tyToString typed.Types.["noted"])

[<Fact>]
let ``A for loop's body still needs a context no use around it supplies`` () =
    let source = """context Note = (note: int -> ())

def each: list(int), ([Note] int -> ()) -> () = { xs, f ->
  for xs | x -> f(x)
}
"""
    let code, _ = singleError (source.ReplaceLineEndings "\n")
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, code)