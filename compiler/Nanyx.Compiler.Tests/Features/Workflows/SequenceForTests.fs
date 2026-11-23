module Nanyx.Compiler.Tests.Features.WorkflowSequenceFor

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A `for` loop in a sequence's block walks anything `Iterable` (#2420): a sequence as it is, and anything else
// through its `Iterable`.

[<Fact>]
let ``A for loop in a builder block over a list checks`` () =
    let source =
        normalized """def plusOne = { xs: list(int) ->
  list.build {
    for xs | x -> yield x + 1
  }
}
"""
    Assert.Equal("list(int) -> list(int)", typeIn (compileClean source) "plusOne")

[<Fact>]
let ``A for loop in a seq block over a type nothing settles walks a sequence`` () =
    let source =
        normalized """def again = { s ->
  seq {
    for s | x -> yield x
  }
}
"""
    Assert.Matches(@"^seq\(t\d+\) -> seq\(t\d+\)$", typeIn (compileClean source) "again")

[<Fact>]
let ``A for loop in a seq block over something that is not Iterable is a type mismatch at it`` () =
    let source =
        normalized """def s = seq {
  for 5 | x -> yield x
}
"""
    let code, _, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("5", text)
