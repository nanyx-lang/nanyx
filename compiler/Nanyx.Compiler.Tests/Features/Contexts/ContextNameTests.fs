module Nanyx.Compiler.Tests.Features.ContextNames

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// `context l` names the instance of a context a lambda is given, which only `use l` can load (#1445).

let private contexts = """context Log = (log: string -> ())
context Tick = (tick: () -> ())

def inner: [Log] () -> () = { log("x") }
"""

/// The code of the one diagnostic `contexts` followed by `source` compiles to, and the text it is reported at.
let private errorIn (source: string) = singleError ((contexts + source).ReplaceLineEndings "\n")

[<Fact>]
let ``A context name loaded with use checks, and its lambda's type doesn't change`` () =
    compileClean (
            (contexts
             + """def run: [Log] () -> () = { context l ->
  use Log(log = { m -> () })
  inner()
  use l in inner()
}
def twice: [Log, Tick] int -> int = { context l: Log, context t: Tick, n ->
  use l
  use t
  tick()
  n * 2
}
""")
                .ReplaceLineEndings "\n")
    |> ignore

[<Fact>]
let ``A context name is not a value`` () =
    for body, text in [ "def x = l\n  ()", "l"; "inner2(l)", "l"; "l", "l" ] do
        let code, at = errorIn $"def inner2 = {{ x -> () }}\ndef run: [Log] () -> () = {{ context l ->\n  {body}\n}}\n"
        Assert.Equal(DiagnosticCodes.ContextNameAsValue.Code, code)
        Assert.Equal(text, at)
    let result = Compiler.compile ((contexts + "def run: [Log] () -> () = { context l ->\n  def x = l\n  ()\n}\n").ReplaceLineEndings "\n")
    Assert.Equal("It names a context instance, which can only be loaded with `use l`", (Assert.Single result.Diagnostics).Message)

[<Fact>]
let ``An inner lambda may capture a context name to use it`` () =
    compileClean ((contexts + "def run: [Log] () -> () = { context l ->\n  def again = { use l in inner() }\n  again()\n}\n").ReplaceLineEndings "\n")
    |> ignore

[<Fact>]
let ``A context name must name a context the lambda is given, and only one`` () =
    let code, at = errorIn "def run: [Log, Tick] () -> () = { context l -> () }\n"
    Assert.Equal(DiagnosticCodes.UnknownContextName.Code, code)
    Assert.Equal("l", at)
    let code, at = errorIn "def run: [Log] () -> () = { context l: Tick -> () }\n"
    Assert.Equal(DiagnosticCodes.UnknownContextName.Code, code)
    Assert.Equal("l", at)
    let code, _ = errorIn "def run: () -> () = { context l -> () }\n"
    Assert.Equal(DiagnosticCodes.UnknownContextName.Code, code)
    let result = Compiler.compile ((contexts + "def run: [Log, Tick] () -> () = { context l -> () }\n").ReplaceLineEndings "\n")
    Assert.Equal(
        "'l' could name any of 'Log' and 'Tick', which this lambda is given: say which, as in `context l: Log`",
        (Assert.Single result.Diagnostics).Message)

[<Fact>]
let ``A lambda passed to a handler names the contexts the handler gives it`` () =
    compileClean (
        (contexts
         + """def withLog: [c] ([Log, c] () -> ()) -> () = { f -> use Log(log = { m -> () }) in f() }
def main = withLog { context l -> use l in inner() }
""")
            .ReplaceLineEndings "\n")
    |> ignore

[<Fact>]
let ``Hovering a context name shows its context`` () =
    let source = (contexts + "def run: [Log] () -> () = { context l -> use l in inner() }\n").ReplaceLineEndings "\n"
    Assert.Equal(Some "l: Log", hoverType source "l in")

[<Fact>]
let ``A context name names an instance of a parameterised context, by its context or with its types`` () =
    // Not only a plain context with members (#1870).
    compileClean (
        """context Show(a) = (show: a -> string)
def describe: [Show(int)] int -> string = { n -> Show(int).show(n) }
def run: [Show(int)] int -> string = { context s, n ->
  use Show(int)(show = { m -> "shadowed" })
  use s
  describe(n)
}
def both: [Show(int), Show(string)] int -> string = { context s: Show(int), n ->
  use s in describe(n)
}
"""
            .ReplaceLineEndings "\n")
    |> ignore
    let result =
        Compiler.compile (
            "context Show(a) = (show: a -> string)\ndef both: [Show(int), Show(string)] () -> () = { context s: Show -> () }\n"
                .ReplaceLineEndings "\n")
    let diagnostic = Assert.Single result.Diagnostics
    Assert.Equal(DiagnosticCodes.UnknownContextName.Code, diagnostic.Code)
    Assert.Equal("'s' could name any of the instances of 'Show' this lambda is given: say which, as in `context s: Show(int)`", diagnostic.Message)

[<Fact>]
let ``A context name names a context with no members, and use loads it`` () =
    compileClean (
        """context Tx = protected ()
def needsTx: [Tx] () -> () = { () }
def run: [Tx] () -> () = { context t ->
  use t
  needsTx()
}
"""
            .ReplaceLineEndings "\n")
    |> ignore
