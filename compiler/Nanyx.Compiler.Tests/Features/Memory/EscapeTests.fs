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
