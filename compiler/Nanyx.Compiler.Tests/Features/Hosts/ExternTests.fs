/// `@extern("module", "name")` declares a foreign function: a typed def with no value that the embedder provides,
/// which only a host can call (#1424, see docs/src/content/deep-dives/foreign-functions.md).
module Nanyx.Compiler.Tests.Features.Hosts.Externs

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// `source`, an import of `nanyx/host.runtime` and then `declarations`.
let private withRuntime (declarations: string) = normalized ("import nanyx/host.runtime\n\n" + declarations)

[<Fact>]
let ``A host declares and calls a foreign function`` () =
    let source =
        withRuntime
            """@extern("wasi_snapshot_preview1", "args_sizes_get")
def argsSizesGet: [HostRuntime] (I32, I32) -> I32

def sizes: [HostRuntime] () -> I32 = { -> argsSizesGet(0, 4) }
"""
    let result = compileAsHost Linters.none source
    Assert.True(result.TypeChecked, $"%A{result.Diagnostics}")
    Assert.Empty(result.Diagnostics)

[<Fact>]
let ``Any module may declare a foreign function`` () =
    withRuntime
        """@extern("wasi_snapshot_preview1", "proc_exit")
def procExit: [HostRuntime] I32 -> ()
"""
    |> compileClean
    |> ignore

[<Fact>]
let ``Calling a foreign function outside a host is an error, since the call requires HostRuntime`` () =
    let source =
        withRuntime
            """@extern("wasi_snapshot_preview1", "args_sizes_get")
def argsSizesGet: [HostRuntime] (I32, I32) -> I32

def sizes = { -> argsSizesGet(0, 4) }
"""
    Assert.Equal((DiagnosticCodes.HostRuntimeOutsideHost.Code, "sizes"), singleError source)

[<Fact>]
let ``A def with no value and no @extern is an error`` () =
    let code, message, text = onlyDiagnostic "def size: () -> I32\n"
    Assert.Equal(DiagnosticCodes.DefinitionWithoutValue.Code, code)
    Assert.StartsWith("'size' has no value", message)
    Assert.Equal("def size: () -> I32", text)

[<Fact>]
let ``A foreign function with a value is an error, reported at the value`` () =
    let source =
        withRuntime
            """@extern("test", "double")
def double: [HostRuntime] I32 -> I32 = { x -> x }
"""
    let code, message, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.InvalidExtern.Code, code)
    Assert.Equal("The foreign function 'double' has a value: the embedder provides it, so write only its type", message)
    Assert.Equal("{ x -> x }", text)

[<Fact>]
let ``A foreign function without a written type is an error`` () =
    let code, message, _ = onlyDiagnostic (withRuntime "@extern(\"test\", \"tick\")\ndef tick\n")
    Assert.Equal(DiagnosticCodes.InvalidExtern.Code, code)
    Assert.StartsWith("The foreign function 'tick' needs its type written", message)

[<Fact>]
let ``A foreign function that takes a type other than a value type is an error, reported at the type`` () =
    let source =
        withRuntime
            """@extern("test", "print")
def print: [HostRuntime] string -> ()
"""
    let code, message, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.InvalidExtern.Code, code)
    Assert.Equal("A foreign function takes and returns only WebAssembly's value types, I32, I64, F32 and F64, not 'string'", message)
    Assert.Equal("string", text)

[<Fact>]
let ``A foreign function whose type does not require HostRuntime is an error that says so`` () =
    let code, message, text = onlyDiagnostic "@extern(\"test\", \"tick\")\ndef tick: () -> I32\n"
    Assert.Equal(DiagnosticCodes.ExternWithoutHostRuntime.Code, code)
    Assert.StartsWith("A foreign function's type must require 'HostRuntime', so that only a host can call it", message)
    Assert.Equal("() -> I32", text)

[<Fact>]
let ``@extern with other than the import's module and name is an error`` () =
    let code, message, text = onlyDiagnostic (withRuntime "@extern(\"tick\")\ndef tick: [HostRuntime] () -> I32\n")
    Assert.Equal(DiagnosticCodes.InvalidExtern.Code, code)
    Assert.StartsWith("Attribute '@extern' takes the import's module and name", message)
    Assert.Equal("@extern(\"tick\")", text)

[<Fact>]
let ``Two foreign functions that import one function with different types are an error, reported at the second`` () =
    let source =
        withRuntime
            """@extern("wasi_snapshot_preview1", "args_sizes_get")
def argsSizesGet: [HostRuntime] (I32, I32) -> I32

@extern("wasi_snapshot_preview1", "args_sizes_get")
def sizesGet: [HostRuntime] I32 -> I32
"""
    let code, message, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.ConflictingExterns.Code, code)
    Assert.Equal(
        """'sizesGet' imports "args_sizes_get" from "wasi_snapshot_preview1" as (I32) -> I32, but 'argsSizesGet' imports it as (I32, I32) -> I32""",
        message)
    Assert.Equal("def sizesGet: [HostRuntime] I32 -> I32", text)

[<Fact>]
let ``Two foreign functions may import one function with the same type`` () =
    withRuntime
        """@extern("wasi_snapshot_preview1", "proc_exit")
def procExit: [HostRuntime] I32 -> ()

@extern("wasi_snapshot_preview1", "proc_exit")
def exit: [HostRuntime] I32 -> ()
"""
    |> compileClean
    |> ignore
