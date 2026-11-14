module Nanyx.Compiler.Tests.Features.Linters.UnusedBindings

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

// The official linter's `unusedBindings` rule.

/// What `default/unusedBindings` reports for `source`: each message with the text it is reported at.
let private unusedIn (source: string) =
    (compileWithOfficial source).Diagnostics
    |> List.map (fun diagnostic ->
        Assert.Equal("default/unusedBindings", diagnostic.Code)
        Assert.Equal(Severity.Warning, diagnostic.Severity)
        diagnostic.Message, textAt source diagnostic.Span.Value)

[<Fact>]
let ``An unused local def, argument or pattern variable is reported once, at its name`` () =
    // The examples from the official linter's page in the docs.
    let source =
        normalized """def total =
  def taxRate = 0.2
  100
def greet: (string, string) -> string = { name, title ->
  "Hello, {name}!"
}
def describe: #some(int) | #error(string) -> string = { response ->
  match response
    | #some(value) -> "Success"
    | #error(msg) -> "Error"
}
"""
    Assert.Equal<(string * string) list>(
        [ "`taxRate` is never used", "taxRate"
          "Argument `title` is never used", "title"
          "`value` is introduced by a pattern but never used", "value"
          "`msg` is introduced by a pattern but never used", "msg" ],
        unusedIn source)

[<Fact>]
let ``Used names, top-level defs and names starting with an underscore are not reported`` () =
    let source =
        normalized """def unusedTopLevel = 1
def f = { x, _ignored ->
  def doubled = x * 2
  def _spare = 3
  def g = { y -> "{y} {doubled}" }
  match [doubled]
    | [_first, ..._rest] -> g(1)
    | _ -> g(2)
}
"""
    Assert.Empty(unusedIn source)

[<Fact>]
let ``Names the compiler makes up are not reported`` () =
    // A destructuring def binds the whole value under a made-up name, `for` its element, a shorthand lambda its
    // argument, and a workflow bind a lambda's.
    let source =
        normalized """def half: int -> Result(int, string) = { n -> #some(n / 2) }
def f = { pair ->
  def (a, b) = pair
  def doubled = [a, b] \ list.map({ * 2 })
  for doubled
    | n -> n
}
def quarter = { n -> proc {
  def h = try half(n)
  return try half(h)
} }
"""
    Assert.Empty(unusedIn source)

[<Fact>]
let ``A name used only in a guard pattern's predicate is used`` () =
    let source =
        normalized """def size: int, int -> string = { n, limit ->
  match n
    | { x -> x > limit } -> "big"
    | _ -> "small"
}
"""
    Assert.Empty(unusedIn source)

[<Fact>]
let ``The fix for an unused local def removes its line, with a comment after it`` () =
    let source = "def total = { x ->\n  def y = 1\n  def taxRate = 0.2 -- the rate\n  x + y\n}\n"
    let text, title = fixed' source
    Assert.Equal("Remove `taxRate`", title)
    Assert.Equal("def total = { x ->\n  def y = 1\n  x + y\n}\n", text)
    Assert.Empty((compileWithOfficial text).Diagnostics)

[<Fact>]
let ``The fix for an unused argument or pattern variable renames it to an underscore`` () =
    let argument, title = fixed' "def f = { x, y -> x }"
    Assert.Equal("Rename `y` to `_`", title)
    Assert.Equal("def f = { x, _ -> x }", argument)
    let pattern, _ = fixed' "def f: #some(int) | #none -> int = { o ->\n  match o\n    | #some(n) -> 1\n    | #none -> 0\n}"
    Assert.Contains("| #some(_) -> 1", pattern)
    Assert.Empty((compileWithOfficial pattern).Diagnostics)
    // After a list pattern's `...` too: without a name, `[x, ...]` would be a list of one.
    let rest, _ = fixed' "def f: list(int) -> int = { xs ->\n  match xs\n    | [x, ...rest] -> x\n    | [] -> 0\n}"
    Assert.Contains("| [x, ..._] -> x", rest)
    Assert.Empty((compileWithOfficial rest).Diagnostics)

[<Fact>]
let ``The fix for one unused name of a destructuring def renames it rather than removing the def`` () =
    let text, title = fixed' "def f = { pair ->\n  def (a, b) = pair\n  b\n}"
    Assert.Equal("Rename `a` to `_`", title)
    Assert.Equal("def f = { pair ->\n  def (_, b) = pair\n  b\n}", text)
    Assert.Empty((compileWithOfficial text).Diagnostics)
