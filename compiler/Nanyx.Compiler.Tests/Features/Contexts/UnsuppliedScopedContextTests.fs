module Nanyx.Compiler.Tests.Features.UnsuppliedScopedContexts

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A call that requires a scoped context is an error where nothing supplies it: outside any lambda, in a def whose type
// does not write the context, in a top-level value, or in a script's statements. A def that writes no type is inferred
// to require it, and its callers are checked in turn (#1561).

let private tx = """
scoped context Tx = private ()
type Cursor = [Tx] protected (row: int)
def open: [Tx] int -> Cursor = { r -> Cursor(row = r) }
def transaction: [c] ([Tx, c] () -> a) -> a = { block -> block() }
"""

/// The code of each diagnostic `source` compiles to, with the text it is reported at.
let private errorsOf (source: string) =
    let source = source.ReplaceLineEndings "\n"
    (Compiler.compile source).Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, textAt source diagnostic.Span.Value)

[<Fact>]
let ``A def that writes its type without the scoped context cannot call what requires it`` () =
    let source = tx + """
def f: () -> Cursor = { open(1) }
"""
    let errors = errorsOf source
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UndeclaredContext.Code, "open" ], errors)

[<Fact>]
let ``A def that writes the scoped context, or supplies it to what it is given, can`` () =
    let source = tx + """
def f: [Tx] () -> Cursor = { open(1) }
def run: [c] ([Tx, c] () -> a) -> a = { block -> block() }
def row = transaction { open(2).row }
"""
    compileClean (source.ReplaceLineEndings "\n") |> ignore

[<Fact>]
let ``A def that writes no type is inferred to require the scoped context, and its callers are checked`` () =
    let source = tx + """
def g = { open(2) }
def fine = transaction { g().row }
"""
    let typed = compileClean (source.ReplaceLineEndings "\n")
    Assert.Equal("[Tx] () -> Cursor", Unifier.tyToString typed.Types.["g"])
    let caller = source + """
def k: () -> int = { g().row }
"""
    let errors = errorsOf caller
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UndeclaredContext.Code, "g" ], errors)

[<Fact>]
let ``A top-level value cannot call what requires a scoped context`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.MissingContextMembers.Code, "open" ],
        errorsOf (tx + "def c = open(1)\n"))
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.UndeclaredContext.Code, "open" ],
        errorsOf ("module m\n" + tx + "def c = open(1)\n"))

[<Fact>]
let ``An MRef cannot be made outside a memory block`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.MissingContextMembers.Code, "MRef.new" ],
        errorsOf "import nanyx/mref\n\ndef r = MRef.new(1)\n")
    let errors = errorsOf """
import nanyx/mref

def f: () -> int = {
  def r = MRef.new(1)
  memory { MRef.get(r) }
}
"""
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UndeclaredContext.Code, "MRef.new" ], errors)

[<Fact>]
let ``A loop whose body requires a scoped context runs where it is written`` () =
    let errors = errorsOf """
import nanyx/mref

def f: () -> () = {
  for [1, 2] | x -> MRef.new(x)
  ()
}
"""
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UndeclaredContext.Code, "for [1, 2] | x -> MRef.new(x)" ], errors)

let private calculator (setUp: string) (body: string) = $"""
import nanyx/mstack

def force = {{
  | #some(v), _ -> v
  | _, msg -> crash("Expected a value: {{msg}}")
}}

def calculate: string -> float = {{ input ->
{setUp}
  memory {{
{body}
    push(1.0)
    push(2.0)
    pop() + pop()
  }}
}}
"""

let private stack = """
    def numberStack = MStack.empty()
    def push = { value -> MStack.push(numberStack, value) }
    def pop = { MStack.pop(numberStack) \force("Stack underflow") }
"""

[<Fact>]
let ``An MStack made outside its memory block is an error where it is made`` () =
    let errors = errorsOf (calculator stack "")
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UndeclaredContext.Code, "MStack.empty" ], errors)

[<Fact>]
let ``A local function over its block's MStack requires the block's Memory once`` () =
    let source = (calculator "" stack).ReplaceLineEndings "\n"
    Assert.Equal(Some "pop: [Memory] () -> float", hoverType source "pop =")
