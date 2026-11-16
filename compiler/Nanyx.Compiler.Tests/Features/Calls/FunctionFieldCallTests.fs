/// A call of a function a record's field holds, `statement.setInt(position, value)` (#1513).
module Nanyx.Compiler.Tests.Features.Calls.FunctionFieldCalls

open System.IO
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``A call through a record's function field has the field's result type`` () =
    let source =
        normalized """type Counter = (add: (int, int) -> int, name: () -> string)

def total: Counter -> int = { c -> c.add(1, 2) }
def label: Counter -> string = { c -> c.name() }
"""
    let typed = compileClean source
    Assert.Equal("Counter -> int", Unifier.tyToString typed.Types.["total"])
    Assert.Equal("Counter -> string", Unifier.tyToString typed.Types.["label"])

[<Fact>]
let ``A call through a function field checks its arguments against the field's type`` () =
    let source =
        normalized """type Counter = (add: (int, int) -> int)

def total: Counter -> int = { c -> c.add(1, "two") }
"""
    assertReports DiagnosticCodes.TypeMismatch (Compiler.compile source)

[<Fact>]
let ``A field that holds no function is still not a function's name`` () =
    let source =
        normalized """type Counter = (count: int)

def total: Counter -> int = { c -> c.count(1) }
"""
    assertReports DiagnosticCodes.UnknownFunction (Compiler.compile source)

[<Fact>]
let ``examples/sql-utils.nyx, which calls a prepared statement's fields, checks cleanly`` () =
    let path = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "..", "examples", "sql-utils.nyx"))
    let result = Compiler.compileFrom Linters.none (SourceFile path) (File.ReadAllText path)
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
