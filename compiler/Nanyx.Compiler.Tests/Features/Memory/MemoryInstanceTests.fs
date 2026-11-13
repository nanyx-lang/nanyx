module Nanyx.Compiler.Tests.Features.Memory.Instances

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A `memory` block may take its instance, `memory { m -> ... }`, and `use m` in an inner block makes the cells declared
// after it belong to `m`'s block (#1058).

[<Fact>]
let ``A block may take its instance, and one that does not still checks`` () =
    let typed = compileClean """
def named = memory { m ->
  mut x = 1
  x := x + 1
  x
}

def unnamed = memory {
  mut x = 1
  x
}
"""
    Assert.Equal("int", Unifier.tyToString typed.Types.["named"])
    Assert.Equal("int", Unifier.tyToString typed.Types.["unnamed"])

[<Fact>]
let ``The instance is a Memory, as a reader sees it`` () =
    let source = """
def f = memory { m ->
  mut x = 1
  x
}
"""
    Assert.Equal(Some "m: Memory", hoverType (source.ReplaceLineEndings "\n") "m ->")

[<Fact>]
let ``The instance cannot leave its block`` () =
    let source = "def leak = memory { m -> m }\n"
    let result = Compiler.compile source
    Assert.Equal<string list>([ DiagnosticCodes.ScopedContextEscapes.Code ], result.Diagnostics |> List.map _.Code)
    let span = result.Diagnostics.Head.Span.Value
    Assert.Equal("m", source.Substring(span.Start, span.End - span.Start))

[<Fact>]
let ``A cell made after use of an outer block's instance belongs to that block, so a lambda over it may leave the inner one`` () =
    let typed = compileClean """
def outer = memory { m1 ->
  def get = memory {
    use m1
    mut z = 0
    z := 5
    { z }
  }
  get()
}
"""
    Assert.Equal("int", Unifier.tyToString typed.Types.["outer"])

[<Fact>]
let ``Without the use, the same lambda cannot leave the inner block`` () =
    let result = Compiler.compile """
def outer = memory { m1 ->
  def get = memory {
    mut z = 0
    z := 5
    { z }
  }
  get()
}
"""
    Assert.Equal<string list>([ DiagnosticCodes.ScopedContextEscapes.Code ], result.Diagnostics |> List.map _.Code)
