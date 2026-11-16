/// An arithmetic operator on operands whose type is still a variable where it is read, in a def that writes its type,
/// is on whatever number the rest of the def settles them to, and on `int` if nothing does (#1485).
module Nanyx.Compiler.Tests.Features.OperandsSettledLater

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private stack =
    """def pop = { mut stack ->
  match stack
    | [] -> crash("empty")
    | [value, ...rest] ->
      stack := rest
      value
}

def push = { mut stack, value -> stack := [value] ++ stack }
"""

[<Fact>]
let ``Operands settled to float after the operator are added as floats`` () =
    let source =
        stack
        + """
def calc: bool -> float = { add ->
  memory {
    mut s = []
    if add then push(mut s, pop(mut s) + pop(mut s)) else push(mut s, 1.5)
    pop(mut s)
  }
}
"""
    let typed = compileClean source
    Assert.Equal("bool -> float", Unifier.tyToString typed.Types.["calc"])

[<Fact>]
let ``Operands settled to float before the operator are added as floats`` () =
    let source =
        stack
        + """
def calc: () -> float = {
  memory {
    mut s = []
    push(mut s, 1.5)
    push(mut s, 2.0)
    push(mut s, pop(mut s) + pop(mut s))
    pop(mut s)
  }
}
"""
    let typed = compileClean source
    Assert.Equal("() -> float", Unifier.tyToString typed.Types.["calc"])

[<Fact>]
let ``Operands nothing settles are ints`` () =
    let source =
        stack
        + """
def calc: () -> list(int) = {
  memory {
    mut s = []
    if false then push(mut s, pop(mut s) + pop(mut s)) else ()
    s
  }
}
"""
    compileClean source |> ignore

[<Fact>]
let ``Operands settled later to a type that is not a number are an error at the operator`` () =
    let source =
        stack
        + """
def calc: () -> () = {
  memory {
    mut s = []
    if false then push(mut s, pop(mut s) + pop(mut s)) else push(mut s, "a")
  }
}
"""
    Assert.Equal(
        (DiagnosticCodes.TypeMismatch.Code, "'string' cannot be used with +: it is not a number. Use ++ to join them", "pop(mut s) + pop(mut s)"),
        onlyDiagnostic source)
