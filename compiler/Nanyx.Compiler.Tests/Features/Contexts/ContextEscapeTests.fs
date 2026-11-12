module Nanyx.Compiler.Tests.Features.ContextEscapes

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A lambda that uses a context requires it where it is called, and its type says so (#768), so it may leave the def or
// `use` that supplies the context: each call passes it the instance in scope there (#836).

let private foo = """context Foo = (bar: string -> string)
"""

[<Fact>]
let ``A def may return a lambda that uses a member of a context it requires, which requires it where it is called`` () =
    let returns =
        foo
        + """
def fooEscapes: [Foo] string -> ([Foo] () -> string) = { s ->
  { bar(s) }
}

def escaped = use (bar = { s -> s }) in fooEscapes("Hello from Foo!")
"""
    compileClean returns |> ignore
    let called = returns + "\nescaped()\n"
    let diagnostic = (Compiler.compile called).Diagnostics |> List.exactlyOne
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
    Assert.Equal("'escaped' requires the context 'Foo', which is not available here", diagnostic.Message)

[<Fact>]
let ``A result type that does not say the context the lambda requires is a type error`` () =
    let source =
        foo
        + """
def fooEscapes: [Foo] string -> () -> string = { s ->
  { bar(s) }
}
"""
    Assert.Equal((DiagnosticCodes.ContextsNotAllowed.Code, "{ bar(s) }"), singleError source)

[<Fact>]
let ``A lambda that calls a def requiring the context, a local function that uses it, or a structure that holds one may leave`` () =
    compileClean (
        foo
        + """
def needsFoo: [Foo] string -> string = { s -> bar(s) }

def wraps: [Foo] string -> ([Foo] () -> string) = { s -> { needsFoo(s) } }

def viaLocal: [Foo] string -> ([Foo] () -> string) = { s ->
  def f = { bar(s) }
  def g = { f() }
  g
}

def held: [Foo] string -> (run: [Foo] () -> string) = { s -> (run = { bar(s) }) }
"""
    )
    |> ignore

[<Fact>]
let ``A use may result in a lambda that uses a member it loads`` () =
    compileClean (
        foo
        + """
def viaUse = use Foo(bar = { s -> s }) in { bar("x") }

def blockUse = {
  use Foo(bar = { s -> s })
  { bar("x") }
}

def viaRecord = use (shout = { s -> s ++ "!" }) in { n -> shout(n) }
"""
    )
    |> ignore

[<Fact>]
let ``A lambda that uses a context may be called, or passed on, inside the scope that supplies it`` () =
    compileClean (
        foo
        + """
def inside: [Foo] string -> string = { s ->
  def f = { bar(s) }
  def twice = { x -> bar(bar(x)) }
  f() ++ twice("y") ++ ([1, 2] \list.map { _ -> bar("z") } \list.length \int.toString)
}

def loaded = {
  use Foo(bar = { s -> s ++ "?" })
  def f = { bar("w") }
  f() ++ ([1] \list.map { _ -> bar("v") } \list.length \int.toString)
}

def viaUse = use Foo(bar = { s -> s }) in inside("ok")
"""
    )
    |> ignore

[<Fact>]
let ``A lambda may leave a scope whose context it does not use, or that a nearer one supplies`` () =
    compileClean (
        foo
        + """
def pure: [Foo] string -> () -> string = { s ->
  def t = bar(s)
  { t }
}

def nearer: [Foo] string -> () -> string = { s ->
  { use (bar = { x -> x }) in bar(s) }
}

def shadowed = use (bar = { s -> s }) in { bar -> bar("x") }
"""
    )
    |> ignore
