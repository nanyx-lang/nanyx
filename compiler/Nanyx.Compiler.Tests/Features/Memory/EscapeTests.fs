module Nanyx.Compiler.Tests.Features.MemoryEscapes

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// The routes by which a mutable object, or something holding one, could leave its `memory` block, each rejected with
// the code it is reported with (#2233). A block frees its mutable objects when it ends (#2232), so each of these would
// be a use after free.

/// The code of each diagnostic of compiling `source`.
let private codes (source: string) =
    (Compiler.compile (normalized source)).Diagnostics |> List.map (fun diagnostic -> diagnostic.Code)

/// A function that gives its callback a block's reference and gives back what the callback does (#2294).
let private run =
    """
import nanyx/mref

def run = { f -> memory { f(MRef.new(0)) } }
"""

[<Fact>]
let ``A reference can't leave its block as the result of a callback the block gives it to`` () =
    let source =
        run
        + """
def counter = run({ r -> r })

def next: () -> int = {
  memory {
    MRef.update(counter, { n -> n + 1 })
    MRef.get(counter)
  }
}
"""
    let code, text = singleError (normalized source)
    Assert.Equal(DiagnosticCodes.ScopedContextEscapes.Code, code)
    Assert.Equal("r", text)

[<Theory>]
[<InlineData("[r]")>]
[<InlineData("(ref = r)")>]
[<InlineData("#some(r)")>]
[<InlineData("{ MRef.get(r) }")>]
let ``Nor can what the callback wraps it in`` (wrapped: string) =
    Assert.Equal<string list>([ DiagnosticCodes.ScopedContextEscapes.Code ], codes (run + $"def held = run({{ r -> {wrapped} }})\n"))

[<Fact>]
let ``Nor can a value of a type in a user's scoped context's scope`` () =
    let source =
        """
scoped context Tx = private ()

type Cursor = [Tx] protected (query: string, row: int)

def transaction: [c] ([Tx, c] () -> a) -> a = { block -> use Tx() in block() }
def open: [Tx] string -> Cursor = { query -> Cursor(query = query, row = 0) }

def run = { f -> transaction { f(open("select")) } }
def leaked = run({ c -> c })
"""
    Assert.Equal<string list>([ DiagnosticCodes.ScopedContextEscapes.Code ], codes source)

[<Fact>]
let ``A callback may give back what the reference holds`` () =
    let source =
        run
        + """
def read: [Memory] () -> int = { run({ r -> MRef.get(r) }) }
def total = memory { read() }
"""
    Assert.Equal<string list>([], codes source)

/// An outer block whose inner block stores the inner block's reference in `holder`, declared by `declaration`, with
/// `store` (#2295).
let private storedOutside (declaration: string) (store: string) =
    $"""
import nanyx/mref

def total = memory {{
  {declaration}
  memory {{
    def r = MRef.new(5)
    {store}
  }}
  0
}}
"""

[<Fact>]
let ``An inner block's reference can't be stored in an outer block's cell`` () =
    let source =
        """
import nanyx/mref

def total = memory {
  mut holder = []
  memory {
    def r = MRef.new(5)
    holder := [r]
  }
  match holder
    | [r] -> MRef.get(r)
    | _ -> 0
}
"""
    let code, text = singleError (normalized source)
    Assert.Equal(DiagnosticCodes.ScopedContextEscapes.Code, code)
    Assert.Equal("holder", text)

[<Fact>]
let ``Nor in an outer block's reference`` () =
    let source =
        """
import nanyx/mref

def total = memory {
  def outer = MRef.new([])
  memory {
    def r = MRef.new(5)
    MRef.set(outer, [r])
  }
  match MRef.get(outer)
    | [r] -> MRef.get(r)
    | _ -> 0
}
"""
    let code, text = singleError (normalized source)
    Assert.Equal(DiagnosticCodes.ScopedContextEscapes.Code, code)
    Assert.Equal("outer", text)

[<Theory>]
[<InlineData("mut holder: #some(MRef(int)) | #none = #none", "holder := #some(r)")>]
[<InlineData("mut holder = []", "holder := [{ MRef.get(r) }]")>]
[<InlineData("def outer = MRef.new([])", "MRef.set(outer, [(ref = r)])")>]
[<InlineData("def outer = MRef.new([])", "MRef.set(outer, [{ MRef.get(r) }])")>]
[<InlineData("def outer = MRef.new([])", "def put = { x -> MRef.set(outer, x) }\n    put([r])")>]
let ``Nor what holds it, nor a lambda over it`` (declaration: string) (store: string) =
    Assert.Equal<string list>([ DiagnosticCodes.ScopedContextEscapes.Code ], codes (storedOutside declaration store))

[<Theory>]
[<InlineData("mut holder = 0", "holder := MRef.get(r)")>]
[<InlineData("def outer = MRef.new(0)", "MRef.set(outer, MRef.get(r))")>]
let ``An inner block may store what its reference holds in an outer block's cell or reference`` (declaration: string) (store: string) =
    Assert.Equal<string list>([], codes (storedOutside declaration store))

[<Fact>]
let ``A lambda that loads the block's named Memory can't leave the block`` () =
    let source =
        """
import nanyx/mref

def maker = memory { context m ->
  { use m in MRef.get(MRef.new(3)) }
}

def call: () -> int = { memory { maker() } }
"""
    let code, text = singleError (normalized source)
    Assert.Equal(DiagnosticCodes.ScopedContextEscapes.Code, code)
    Assert.Equal("m", text)

[<Theory>]
[<InlineData("[{ use m in MRef.get(MRef.new(3)) }]")>]
[<InlineData("(f = { use m in MRef.get(MRef.new(3)) })")>]
[<InlineData("{ use m in 1 }")>]
let ``Nor a list or record holding one, nor one that makes nothing`` (result: string) =
    let source = $"import nanyx/mref\n\ndef maker = memory {{ context m ->\n  {result}\n}}\n"
    Assert.Equal<string list>([ DiagnosticCodes.ScopedContextEscapes.Code ], codes source)

[<Fact>]
let ``A lambda that loads the block's named Memory is called inside the block`` () =
    let source =
        """
import nanyx/mref

def total = memory { context m ->
  def f = { use m in MRef.get(MRef.new(3)) }
  f() + f()
}
"""
    Assert.Equal<string list>([], codes source)

[<Fact>]
let ``A script that uses what escaped its block is told only of the escape`` () =
    let source =
        """
import nanyx/mref
def s = memory {
  def r = MRef.new(10)
  { MRef.get(r) }
}

println(s())
"""
    Assert.Equal<string list>([ DiagnosticCodes.ScopedContextEscapes.Code ], codes source)

/// A block that gives back `lambda`, made where `r`, a reference, and `c`, a cell, are in scope.
let private capturing (lambda: string) =
    $"import nanyx/mref\n\ndef k = {{ a, b -> a }}\n\ndef x = memory {{\n  def r = MRef.new(1)\n  mut c = 0\n  c := 1\n  {lambda}\n}}\n"

[<Theory>]
[<InlineData("{ list.length([r]) }")>]
[<InlineData("{ k(1, r) }")>]
[<InlineData("{\n    def y = r\n    1\n  }")>]
[<InlineData("{\n    def y = { c }\n    1\n  }")>]
[<InlineData("{\n    def f = {\n      def y = r\n      1\n    }\n    2\n  }")>]
let ``A lambda that captures a reference or a cell can't leave the block, though it doesn't read it`` (lambda: string) =
    Assert.Equal<string list>([ DiagnosticCodes.ScopedContextEscapes.Code ], codes (capturing lambda))

[<Fact>]
let ``A lambda that captures a reference is called inside the block`` () =
    let source =
        """
import nanyx/mref

def x = memory {
  def r = MRef.new(1)
  def f = { list.length([r]) }
  f() + f()
}
"""
    Assert.Equal<string list>([], codes source)
