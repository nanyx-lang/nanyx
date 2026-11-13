module Nanyx.Compiler.Tests.Features.Memory.CellParameters

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// Passing a cell to a function (#1072): `{ mut cell -> ... }` takes one, `f(mut x)` passes one, and `mut T` is its type.

/// The codes of the diagnostics `source` has, and the text of the first.
let private errorsOf (source: string) =
    let result = Compiler.compile source
    let first = result.Diagnostics |> List.tryHead |> Option.map (fun d -> source.Substring(d.Span.Value.Start, d.Span.Value.End - d.Span.Value.Start))
    result.Diagnostics |> List.map _.Code, first

[<Fact>]
let ``A function that takes a cell has the type mut T, and its name reads what the cell holds`` () =
    let typed = compileClean """
def inc = { mut cell ->
  cell := cell + 1
}

def annotated: [$Memory] mut int -> () = { mut cell -> cell := cell + 1 }

def f = memory {
  mut count = 0
  for 1..10 -> inc(mut count)
  count
}
"""
    Assert.Equal("mut int -> ()", Unifier.tyToString typed.Types.["inc"])
    Assert.Equal("mut int -> ()", Unifier.tyToString typed.Types.["annotated"])
    Assert.Equal("int", Unifier.tyToString typed.Types.["f"])

[<Fact>]
let ``A cell parameter can be passed on, and one cell can be passed twice`` () =
    let typed = compileClean """
def inc = { mut cell -> cell := cell + 1 }

def twice = { mut cell ->
  inc(mut cell)
  inc(mut cell)
}

def swap = { mut a, mut b ->
  def held = a
  a := b
  b := held
}

def f = memory {
  mut x = 1
  twice(mut x)
  swap(mut x, mut x)
  x
}
"""
    Assert.Equal("mut int -> ()", Unifier.tyToString typed.Types.["twice"])
    Assert.Equal("int", Unifier.tyToString typed.Types.["f"])

[<Fact>]
let ``Passing a value where a cell is expected, or a cell where a value is, is a type error that names both`` () =
    let codes, text = errorsOf "def inc = { mut cell -> cell := cell + 1 }\ndef f = memory {\n  mut count = 0\n  inc(count)\n  count\n}\n"
    Assert.Equal<string list>([ DiagnosticCodes.TypeMismatch.Code ], codes)
    Assert.Equal(Some "count", text)
    let result = Compiler.compile "def plain = { x: int -> x + 1 }\ndef f = memory {\n  mut c = 0\n  plain(mut c)\n}\n"
    Assert.Equal<string list>([ DiagnosticCodes.TypeMismatch.Code ], result.Diagnostics |> List.map _.Code)
    Assert.Contains("mut", result.Diagnostics.Head.Message)

[<Fact>]
let ``Only a cell can be passed with mut`` () =
    let codes, text = errorsOf "def inc = { mut cell -> cell := cell + 1 }\ndef f = memory {\n  def count = 0\n  inc(mut count)\n  count\n}\n"
    Assert.Equal<string list>([ DiagnosticCodes.NotACellArgument.Code ], codes)
    Assert.Equal(Some "count", text)

[<Fact>]
let ``A lambda over a cell parameter cannot leave the block the cell is in`` () =
    let codes, text = errorsOf "def mk = { mut cell -> { cell := cell + 1 } }\ndef f = memory {\n  mut c = 0\n  mk(mut c)\n}\n"
    Assert.Equal<string list>([ DiagnosticCodes.ScopedContextEscapes.Code ], codes)
    Assert.Equal(Some "c", text)

[<Fact>]
let ``A cell's type inside another type is an error for now`` () =
    let codes, text = errorsOf "def bad: list(mut int) -> () = { xs -> () }\n"
    Assert.Equal<string list>([ DiagnosticCodes.MisplacedCellType.Code ], codes)
    Assert.Equal(Some "mut int", text)
