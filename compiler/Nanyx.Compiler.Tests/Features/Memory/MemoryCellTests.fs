module Nanyx.Compiler.Tests.Features.Memory.Cells

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// `memory { ... }`, and the cells `mut` makes in it: `:=` changes one, and its name reads it.

let private typesOf (source: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    result.Typed.Value.Types

[<Fact>]
let ``A memory block's cells are read by name and changed with :=`` () =
    let types = typesOf """
def sum: list(int) -> int = { xs ->
  memory {
    mut total = 0
    for xs | x -> total := total + x
    total
  }
}

def count = { xs ->
  memory {
    mut evens = 0
    mut odds = 0
    for xs | x ->
      if x % 2 == 0 then evens := evens + 1 else odds := odds + 1
    (evens, odds)
  }
}

def longest: list(string) -> string = { names ->
  memory {
    mut best = ""
    for names | name ->
      match name.length > best.length
        | true -> best := name
        | false -> ()
    best
  }
}
"""
    Assert.Equal("list(int) -> int", Unifier.tyToString types.["sum"])
    Assert.Equal("list(int) -> (int, int)", Unifier.tyToString types.["count"])
    Assert.Equal("list(string) -> string", Unifier.tyToString types.["longest"])

[<Fact>]
let ``A cell's type is its value's, and an annotation says what it holds`` () =
    let types = typesOf """
def tags = memory {
  mut found: list(string) = []
  found := found ++ ["a"]
  found
}
"""
    Assert.Equal("list(string)", Unifier.tyToString types.["tags"])

[<Fact>]
let ``A lambda in the block may read and change its cells`` () =
    let types = typesOf """
def counted = memory {
  mut n = 0
  def bump = { by -> n := n + by }
  bump(2)
  bump(3)
  n
}
"""
    Assert.Equal("int", Unifier.tyToString types.["counted"])

let private hoverOn (source: string) (text: string) (occurrence: int) =
    let source = source.ReplaceLineEndings "\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"%A{result.Diagnostics}")
    Hover.typeAt Unicode result (offsetOf source text occurrence 0) |> Option.map _.Type

[<Fact>]
let ``A lambda that reads or changes a cell requires $Memory`` () =
    let source = """
def tally = { words: list(string) ->
  memory {
    mut count = 0
    def note = { word: string -> count := count + 1 }
    def current = { count }
    for words | word -> note(word)
    current()
  }
}
"""
    Assert.Equal(Some "note: [$Memory] string -> ()", hoverOn source "note" 0)
    Assert.Equal(Some "current: [$Memory] () -> int", hoverOn source "current" 0)
    Assert.Equal(Some "def tally: list(string) -> int", hoverOn source "tally" 0)

[<Fact>]
let ``A lambda that calls a local function over a cell requires $Memory too`` () =
    let source = """
def f = memory {
  mut n = 0
  def bump = { n := n + 1 }
  def twice = {
    bump()
    bump()
  }
  twice()
  n
}
"""
    Assert.Equal(Some "twice: [$Memory] () -> ()", hoverOn source "twice" 0)

[<Fact>]
let ``A lambda that only uses what a cell holds requires nothing`` () =
    let source = """
def f = memory {
  mut n = 1
  def held = n
  def add = { m: int -> m + held }
  add(n)
}
"""
    Assert.Equal(Some "add: int -> int", hoverOn source "add" 0)

[<Fact>]
let ``A mut declaration outside a memory block is an error that names the context`` () =
    let source = """
def f = {
  mut x = 0
  x
}
"""
    let code, text = singleError source
    Assert.Equal(DiagnosticCodes.MutOutsideMemory.Code, code)
    Assert.Equal("mut x = 0", text)
    let result = Compiler.compile source
    Assert.Contains("$Memory", result.Diagnostics.Head.Message)

[<Fact>]
let ``$Memory is nominal, so code outside the prelude cannot make one`` () =
    let result = Compiler.compile "def m = $Memory(())\n"
    Assert.Equal<string list>([ DiagnosticCodes.NominalConstructedOutsideItsModule.Code ], result.Diagnostics |> List.map _.Code)

[<Fact>]
let ``Assigning to a name that is not a cell is an error`` () =
    let code, text = singleError """
def f = memory {
  def x = 0
  x := 1
  x
}
"""
    Assert.Equal(DiagnosticCodes.NotACell.Code, code)
    Assert.Equal("x", text)

[<Fact>]
let ``Assigning a value of another type to a cell is an error`` () =
    let code, text = singleError """
def f = memory {
  mut x = 0
  x := "one"
  x
}
"""
    Assert.Equal(DiagnosticCodes.CellAssignmentMismatch.Code, code)
    Assert.Equal("x := \"one\"", text)

[<Fact>]
let ``A lambda that uses a cell cannot be the result of its block`` () =
    let code, text = singleError """
def f = memory {
  mut x = 0
  { x := x + 1 }
}
"""
    Assert.Equal(DiagnosticCodes.ScopedContextEscapes.Code, code)
    Assert.Equal("x", text)

[<Fact>]
let ``Nor can a local function that uses one, or a structure that holds it`` () =
    let named = """
def f = memory {
  mut x = 0
  def next = { x }
  next
}
"""
    Assert.Equal(DiagnosticCodes.ScopedContextEscapes.Code, fst (singleError named))
    let held = """
def f = memory {
  mut x = 0
  def next = { x }
  (count = 1, next = next)
}
"""
    Assert.Equal(DiagnosticCodes.ScopedContextEscapes.Code, fst (singleError held))

[<Fact>]
let ``A lambda that only uses what a cell holds may leave the block`` () =
    let types = typesOf """
def adder = memory {
  mut x = 1
  x := x + 1
  def held = x
  { n -> n + held }
}
"""
    Assert.Equal("int -> int", Unifier.tyToString types.["adder"])

[<Fact>]
let ``Hover on a cell shows the type of what it holds`` () =
    let source = """
def f = memory {
  mut total = 0
  total := total + 1
  total
}
"""
    let source = source.ReplaceLineEndings "\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"%A{result.Diagnostics}")
    let hover = Hover.typeAt Unicode result (offsetOf source "total" 2 0)
    Assert.Equal(Some "total: int", hover |> Option.map _.Type)
