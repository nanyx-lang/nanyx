module Nanyx.Compiler.Tests.Features.ContextEscapes

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A lambda that uses a context requires it where it is called, but function types do not say so yet (#768), so it
// must not leave the def or `use` that supplies the context (#774).

let private foo = """context Foo = (bar: string -> string)
"""

[<Fact>]
let ``A def cannot return a lambda that uses a member of a context it requires`` () =
    let source =
        foo
        + """
def fooEscapes: [Foo] string -> () -> string = { s ->
  { bar(s) }
}

def escaped = use (bar = { s -> s }) in fooEscapes("Hello from Foo!")

escaped()
"""
    let code, text = singleError source
    Assert.Equal(DiagnosticCodes.ContextEscapesScope.Code, code)
    Assert.Equal("{ bar(s) }", text)
    let diagnostic = (Compiler.compile source).Diagnostics.Head
    Assert.Equal(
        "This lambda requires the context 'Foo', which is not available where it ends up: it is returned from 'fooEscapes', which supplies it only inside itself",
        diagnostic.Message
    )
    let related, label = List.exactlyOne diagnostic.Related
    Assert.Equal("fooEscapes", source.ReplaceLineEndings("\n").Substring(related.Start, related.End - related.Start))
    Assert.Equal("the def that supplies it", label)

[<Fact>]
let ``Nor one that calls a def requiring it, a local function that uses it, or a structure that holds one`` () =
    let requiring = foo + "def needsFoo: [Foo] string -> string = { s -> bar(s) }\n"
    let called = requiring + "def wraps: [Foo] string -> () -> string = { s -> { needsFoo(s) } }"
    Assert.Equal((DiagnosticCodes.ContextEscapesScope.Code, "{ needsFoo(s) }"), singleError called)
    let local =
        foo
        + """
def viaLocal: [Foo] string -> () -> string = { s ->
  def f = { bar(s) }
  def g = { f() }
  g
}
"""
    Assert.Equal((DiagnosticCodes.ContextEscapesScope.Code, "g"), singleError local)
    let held = foo + "def held: [Foo] string -> (run: () -> string) = { s -> (run = { bar(s) }) }"
    Assert.Equal((DiagnosticCodes.ContextEscapesScope.Code, "{ bar(s) }"), singleError held)

[<Fact>]
let ``A use cannot result in a lambda that uses a member it loads`` () =
    let source = foo + "def viaUse = use Foo(bar = { s -> s }) in { bar(\"x\") }"
    Assert.Equal((DiagnosticCodes.ContextEscapesScope.Code, "{ bar(\"x\") }"), singleError source)
    let diagnostic = (Compiler.compile source).Diagnostics.Head
    Assert.StartsWith(
        "This lambda requires the context 'Foo', which is not available where it ends up: it is the result of the `use` that loads it. The braces make it",
        diagnostic.Message
    )
    let related, label = List.exactlyOne diagnostic.Related
    Assert.Equal("use Foo(bar = { s -> s })", source.ReplaceLineEndings("\n").Substring(related.Start, related.End - related.Start))
    Assert.Equal("the `use`", label)

[<Fact>]
let ``Nor can a block with a use, and a use of a record names the members it loads`` () =
    let block =
        foo
        + """
def blockUse = {
  use Foo(bar = { s -> s })
  { bar("x") }
}
"""
    Assert.Equal((DiagnosticCodes.ContextEscapesScope.Code, "{ bar(\"x\") }"), singleError block)
    let record = "def viaRecord = use (shout = { s -> s ++ \"!\" }) in { n -> shout(n) }"
    Assert.Equal((DiagnosticCodes.ContextEscapesScope.Code, "{ n -> shout(n) }"), singleError record)
    Assert.Contains("This lambda requires 'shout'", (Compiler.compile record).Diagnostics.Head.Message)

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
