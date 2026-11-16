module Nanyx.Compiler.Tests.Features.ScopedTypes

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// `type Cursor = [Tx] protected (...)`: a value of the type belongs to the scope of the `Tx` it was made under, so it
// can't leave the call that supplies that `Tx` (NYX0233).

/// A module with a scoped context, a type in its scope, and the functions that make, use and supply them.
let private transactions =
    """
module mod

scoped export context Tx = private ()

export type Cursor = [Tx] protected (query: string, row: int)

export def transaction: [c] ([Tx, c] () -> a) -> a = { block -> block() }
export def open: [Tx] string -> Cursor = { query -> Cursor(query = query, row = 0) }
export def next: [Tx] Cursor -> int = { c -> c.row + 1 }
"""

/// The code and message of each diagnostic of `result`.
let private reported (result: CompileResult) =
    result.Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, diagnostic.Message)

[<Fact>]
let ``A value of a type in a scoped context's scope is used inside the call that supplies the context`` () =
    let source =
        transactions
        + """
def row = transaction {
  def c = open("select")
  next(c)
}
"""
    let typed = compileClean (normalized source)
    Assert.Equal("string -> Cursor", Unifier.tyToDisplayString Ascii typed.Types.["open"])
    Assert.Equal("int", Unifier.tyToDisplayString Ascii typed.Types.["row"])

[<Fact>]
let ``A value of a type in a scoped context's scope can't leave the call that supplies the context`` () =
    let source =
        transactions
        + """
def leaks = transaction {
  def c = open("select")
  c
}
"""
    let source = normalized source
    let result = Compiler.compile source
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.ScopedContextEscapes.Code, "The result of this 'transaction' call requires its 'Tx' through 'c', so it cannot leave the call" ],
        reported result)
    Assert.Equal(source.LastIndexOf "  c" + 2, result.Diagnostics.Head.Span.Value.Start)

[<Fact>]
let ``A record holding a value of a type in a scoped context's scope can't leave the call either`` () =
    let source =
        transactions
        + """
def held = transaction { (cursor = open("select"), count = 1) }
"""
    let result = Compiler.compile (normalized source)
    Assert.Equal<string list>([ DiagnosticCodes.ScopedContextEscapes.Code ], result.Diagnostics |> List.map _.Code)

[<Fact>]
let ``Another module's type in its scoped context's scope stays in the call that supplies it`` () =
    let main =
        """
module main

import mod

def row = transaction { next(open("select")) }
def leaks = transaction { open("select") }
"""
    let result = compileImporting (normalized transactions) (normalized main)
    Assert.Equal<string list>([ DiagnosticCodes.ScopedContextEscapes.Code ], result.Diagnostics |> List.map _.Code)

[<Fact>]
let ``A type can only belong to a scoped context's scope`` () =
    let source =
        """
type Cursor = [Console] protected (row: int)
"""
    let code, text = singleError (normalized source)
    Assert.Equal(DiagnosticCodes.InvalidTypeScope.Code, code)
    Assert.Equal("Console", text)
    let diagnostic = Assert.Single (Compiler.compile (normalized source)).Diagnostics
    Assert.Equal("'Console' isn't a scoped context: a type can only belong to a scoped context's scope", diagnostic.Message)

[<Fact>]
let ``A type belongs to one scoped context's scope at most, and an alias to none`` () =
    let several =
        """
scoped context Tx = private ()
scoped context Lock = private ()
type Cursor = [Tx, Lock] protected (row: int)
"""
    Assert.Equal<string list>([ DiagnosticCodes.InvalidTypeScope.Code ], (Compiler.compile (normalized several)).Diagnostics |> List.map _.Code)
    let alias =
        """
scoped context Tx = private ()
type Cursor = [Tx] (row: int)
"""
    Assert.Equal<string list>([ DiagnosticCodes.ContextsOnValueType.Code ], (Compiler.compile (normalized alias)).Diagnostics |> List.map _.Code)

[<Fact>]
let ``Contexts written on a type that isn't a function are an error`` () =
    let source =
        """
def count: [Console] int = 3
"""
    let code, text = singleError (normalized source)
    Assert.Equal(DiagnosticCodes.ContextsOnValueType.Code, code)
    Assert.Equal("Console", text)
