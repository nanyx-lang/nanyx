module Nanyx.Compiler.Tests.Features.Loops.WhileLoops

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// `while condition -> body` runs the body for as long as the condition is true; the loop is unit.

/// A function whose memory block has the cell `remaining` and then a `while` loop with this condition.
let private countdown (condition: string) =
    let source = $"""
def f = {{
  memory {{
    mut remaining = 3
    while {condition} ->
      println("{{remaining}}...")
      remaining := remaining - 1
  }}
}}
"""
    source.ReplaceLineEndings "\n"

[<Fact>]
let ``a while loop over a cell checks, and is unit`` () =
    let typed = compileClean (countdown "remaining > 0")
    Assert.Equal("() -> ()", Unifier.tyToString typed.Types.["f"])

[<Fact>]
let ``a while loop's condition must be a bool`` () =
    let code, text = singleError (countdown "\"yes\"")
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("\"yes\"", text)

[<Fact>]
let ``a workflow keyword in a while loop is reported where it is written`` () =
    let source = """
def halve: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }
def f: int -> #some(int) | #odd = { n -> proc {
  while true -> halve(try halve(n))
  return 0
} }
"""
    let diagnostic = single (Compiler.compile (source.ReplaceLineEndings "\n"))
    Assert.Equal(DiagnosticCodes.InvalidWorkflow.Code, diagnostic.Code)
    Assert.Equal(
        "`try` is in a `while` loop, so the rest of the `proc` block cannot follow it: handle the value with a `match` in the loop",
        diagnostic.Message)

/// A function whose memory block has the cell `state`, then a `while` loop with this condition whose body reads `v`,
/// then `after`.
let private bindingLoop (condition: string) (after: string) =
    let source = $"""
def f = {{
  memory {{
    mut state: #a(int) | #b = #a(1)
    mut go = true
    while {condition} ->
      println(v)
      state := #b
      go := false
    {after}
  }}
}}
"""
    source.ReplaceLineEndings "\n"

[<Fact>]
let ``the names a while condition's is test binds are in scope in its body`` () =
    let typed = compileClean (bindingLoop "state is #a(v) and v > 0" "()")
    Assert.Equal("() -> ()", Unifier.tyToString typed.Types.["f"])

[<Fact>]
let ``the names a while condition binds are not in scope after the loop`` () =
    let code, text = singleError (bindingLoop "state is #a(v)" "println(v)")
    Assert.Equal(DiagnosticCodes.UnknownIdentifier.Code, code)
    Assert.Equal("v", text)

[<Fact>]
let ``a while condition's is test under or binds nothing in the body`` () =
    let code, text = singleError (bindingLoop "state is #a(v) or go" "()")
    Assert.Equal(DiagnosticCodes.UnknownIdentifier.Code, code)
    Assert.Equal("v", text)
