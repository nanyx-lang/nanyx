module Nanyx.Compiler.Tests.Features.AddContextFixes

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The one diagnostic `source` compiles to: its code, with the title of each fix and the source it gives.
let private fixesFor (source: string) =
    let source = source.ReplaceLineEndings "\n"
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] -> diagnostic.Code, diagnostic.Fixes |> List.map (fun fix -> fix.Title, applyFix source fix)
    | diagnostics -> failwith $"Expected one diagnostic, got %A{diagnostics}"

/// Asserts that the one diagnostic `source` compiles to has the code `code` and the one fix `title`, which gives
/// `expected`, and that `expected` then compiles with no diagnostics.
let private assertFix (source: string) (code: DiagnosticCode) (title: string) (expected: string) =
    let expected = expected.ReplaceLineEndings "\n"
    Assert.Equal((code.Code, [ title, expected ]), fixesFor source)
    compileClean expected |> ignore

[<Fact>]
let ``A comparison adds Eq to a type that writes no contexts, in front of it`` () =
    assertFix
        "def same: a, a -> bool = { x, y -> x == y }\n"
        DiagnosticCodes.MissingContextMembers
        "Add `Eq(a)` to the type of 'same'"
        "def same: [Eq(a)] a, a -> bool = { x, y -> x == y }\n"

[<Fact>]
let ``A context is joined with & to the contexts the type writes`` () =
    assertFix
        """def describe: [ToString(a)] a, a -> string = { x, y -> if x < y then "{x}" else "{y}" }
"""
        DiagnosticCodes.MissingContextMembers
        "Add `Ord(a)` to the type of 'describe'"
        """def describe: [ToString(a) & Ord(a)] a, a -> string = { x, y -> if x < y then "{x}" else "{y}" }
"""

[<Fact>]
let ``Arithmetic adds the context of its operator`` () =
    assertFix
        "def double: a -> a = { x -> x + x }\n"
        DiagnosticCodes.MissingContextMembers
        "Add `Add(a)` to the type of 'double'"
        "def double: [Add(a)] a -> a = { x -> x + x }\n"

[<Fact>]
let ``Joining with ++ adds Append`` () =
    assertFix
        "def concat: a, a -> a = { x, y -> x ++ y }\n"
        DiagnosticCodes.MissingContextMembers
        "Add `Append(a)` to the type of 'concat'"
        "def concat: [Append(a)] a, a -> a = { x, y -> x ++ y }\n"

[<Fact>]
let ``Interpolating adds ToString`` () =
    assertFix
        """def show: a -> string = { value -> "<{value}>" }
"""
        DiagnosticCodes.MissingContextMembers
        "Add `ToString(a)` to the type of 'show'"
        """def show: [ToString(a)] a -> string = { value -> "<{value}>" }
"""

[<Fact>]
let ``A call that requires a context of the def's variable adds it`` () =
    assertFix
        "def sorted: list(a) -> list(a) = { xs -> list.sort(xs) }\n"
        DiagnosticCodes.MissingContextMembers
        "Add `Ord(a)` to the type of 'sorted'"
        "def sorted: [Ord(a)] list(a) -> list(a) = { xs -> list.sort(xs) }\n"

[<Fact>]
let ``A local function's call adds the context to the def around it`` () =
    let source =
        """
def g: a -> bool = { v ->
  def lt = { x, y -> x < y }
  lt(v, v)
}
"""
    let expected =
        """
def g: [Ord(a)] a -> bool = { v ->
  def lt = { x, y -> x < y }
  lt(v, v)
}
"""
    assertFix source DiagnosticCodes.MissingContextMembers "Add `Ord(a)` to the type of 'g'" expected

[<Fact>]
let ``A call to a function that requires an ambient context adds it`` () =
    let source =
        """
def sayHello: [Console] () -> () = { println("hello") }
def g: int -> int = { n ->
  sayHello()
  n
}

@entry
def main = {
  println("{g(1)}")
}
"""
    let expected =
        """
def sayHello: [Console] () -> () = { println("hello") }
def g: [Console] int -> int = { n ->
  sayHello()
  n
}

@entry
def main = {
  println("{g(1)}")
}
"""
    assertFix source DiagnosticCodes.UndeclaredContext "Add `Console` to the type of 'g'" expected

[<Fact>]
let ``A def that does not write its type gets no fix, since it needs none`` () =
    let result = Compiler.compile "def same = { x, y -> x == y }\n"
    Assert.Empty result.Diagnostics
