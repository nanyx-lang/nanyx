module Nanyx.Compiler.Tests.Features.ScopedRepresentations

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A nominal type whose written representation holds a value of a scoped context's scope declares that context:
// `type MStack(a) = [Memory] private MRef(list(a))` (NYX0256). Its value's scoped parts are of its own scope.

let private codes (source: string) =
    (Compiler.compile (normalized source)).Diagnostics |> List.map _.Code

[<Fact>]
let ``A type holding an MRef without declaring Memory is an error, and the fix declares it`` () =
    let source =
        normalized
            """
import nanyx/mref

type Stack(a) = private MRef(list(a))
"""
    let diagnostic = Assert.Single (Compiler.compile source).Diagnostics
    Assert.Equal(DiagnosticCodes.UndeclaredTypeScope.Code, diagnostic.Code)
    Assert.Equal(
        "'Stack' holds a value of type 'MRef', which belongs to a 'Memory' scope: declare it `type Stack(a) = [Memory] private ...`",
        diagnostic.Message)
    let edit = (Assert.Single diagnostic.Fixes).Edits |> Assert.Single
    Assert.Equal(source.IndexOf "private", edit.Span.Start)
    Assert.Equal(edit.Span.Start, edit.Span.End)
    Assert.Equal("[Memory] ", edit.NewText)

[<Fact>]
let ``A type holding an MArray or a user's scoped type without declaring its context is an error`` () =
    let marray =
        """
import nanyx/marray

type Grid = protected (cells: MArray(int), width: int)
"""
    Assert.Equal<string list>([ DiagnosticCodes.UndeclaredTypeScope.Code ], codes marray)
    let user =
        """
scoped context Tx = private ()
type Cursor = [Tx] protected (row: int)
type Page = private (cursor: Cursor, size: int)
"""
    Assert.Equal<string list>([ DiagnosticCodes.UndeclaredTypeScope.Code ], codes user)

[<Fact>]
let ``A record or tag union holding a scoped value deep inside needs the declaration too`` () =
    let source =
        """
import nanyx/mref

type Deep = protected (items: list(#one(MRef(int)) | #none))
"""
    Assert.Equal<string list>([ DiagnosticCodes.UndeclaredTypeScope.Code ], codes source)

[<Fact>]
let ``A type that declares the context it holds checks`` () =
    let source =
        """
import nanyx/mref
import nanyx/marray

scoped context Tx = private ()
type Cursor = [Tx] protected (row: int)

type Stack(a) = [Memory] private MRef(list(a))
type Grid = [Memory] protected (cells: MArray(int), width: int)
type Page = [Tx] private (cursor: Cursor, size: int)
type Deep = [Memory] protected (items: list(#one(MRef(int)) | #none))
"""
    Assert.Empty(codes source)

[<Fact>]
let ``A scoped type as a type argument at a use needs no declaration`` () =
    let source =
        """
import nanyx/mref

type Holder(a) = protected (item: a)

def total: [Memory] list(MRef(int)) -> int = { refs -> refs \list.fold(0) { sum, r -> sum + MRef.get(r) } }

def held = memory {
  def h = Holder(item = MRef.new(1))
  MRef.get(h.item)
}
"""
    Assert.Empty(codes source)

[<Fact>]
let ``A function type that requires a scoped context holds no value of its scope`` () =
    let source =
        """
type Action = protected (run: [Memory] () -> int)
"""
    Assert.Empty(codes source)

[<Fact>]
let ``What a declared type holds is of the value's own scope, so it can't leave its memory block`` () =
    let source =
        """
import nanyx/mref

type Counter = [Memory] private MRef(int)

def Counter.new: [Memory] () -> Counter = { -> Counter(MRef.new(0)) }

def Counter.bump: [Memory] Counter -> int = { Counter(cell) ->
  MRef.set(cell, MRef.get(cell) + 1)
  MRef.get(cell)
}

def counted = memory {
  def c = Counter.new()
  Counter.bump(c)
  Counter.bump(c)
}

def leaks = memory { Counter.new() }
"""
    Assert.Equal<string list>([ DiagnosticCodes.ScopedContextEscapes.Code ], codes source)
