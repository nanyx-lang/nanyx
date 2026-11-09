module Nanyx.Compiler.Tests.Features.Types.TypeOf

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``typeof gives a def the type of another value`` () =
    let source =
        "def defaults = (host = \"localhost\", port = 8080)\n" +
        "def override: typeof defaults = (host = \"example.com\", port = 443)\n" +
        "def port = override.port"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["port"])

[<Fact>]
let ``typeof rejects a value of another type`` () =
    let source =
        "def defaults = (host = \"localhost\", port = 8080)\n" +
        "def override: typeof defaults = (host = \"example.com\", port = \"443\")"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, (List.head result.Diagnostics).Code)

[<Fact>]
let ``typeof can name a value declared later`` () =
    let source =
        "def bad: typeof limit = \"many\"\n" +
        "def limit = 10"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let good = Compiler.compile "def other: typeof limit = 20\ndef limit = 10"
    Assert.True(good.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{good.Diagnostics}")
    Assert.Equal(TyPrimitive "int", good.Typed.Value.Types.["other"])

[<Fact>]
let ``typeof a polymorphic value is a fresh instance of it`` () =
    let source =
        "def id = { x -> x }\n" +
        "def same: typeof id = { y -> y }\n" +
        "def a = same(1)\n" +
        "def b = same(\"s\")"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["a"])
    Assert.Equal(TyPrimitive "string", result.Typed.Value.Types.["b"])

[<Fact>]
let ``typeof takes a path of fields`` () =
    let source =
        "def config = (server = (port = 8080))\n" +
        "def port: typeof config.server.port = 443"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["port"])

[<Fact>]
let ``typeof works in a function type, a lambda parameter and a local def`` () =
    let source =
        "def defaults = (host = \"localhost\", port = 8080)\n" +
        "def portOf: typeof defaults -> int = { c -> c.port }\n" +
        "def add = { c: typeof defaults, n -> c.port + n }\n" +
        "def local = {\n" +
        "  def limit = 3\n" +
        "  def other: typeof limit = 4\n" +
        "  other\n" +
        "}"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal("(host: string, port: int) -> int", Unifier.tyToString result.Typed.Value.Types.["portOf"])
    Assert.Equal("(host: string, port: int), int -> int", Unifier.tyToString result.Typed.Value.Types.["add"])
    Assert.Equal(TyFunc(TyPrimitive "unit", TyPrimitive "int"), result.Typed.Value.Types.["local"])

[<Fact>]
let ``typeof reports a name that is not in scope`` () =
    let source = "def x: typeof missing = 1"
    let result = Compiler.compile source
    let diagnostic = List.head result.Diagnostics
    Assert.Equal(DiagnosticCodes.UnknownIdentifier.Code, diagnostic.Code)
    // A def's own name is not in scope in its annotation.
    let own = Compiler.compile "def x: typeof x = 1"
    Assert.Equal(DiagnosticCodes.UnknownIdentifier.Code, (List.head own.Diagnostics).Code)

[<Fact>]
let ``typeof cannot be used in a type definition`` () =
    let source =
        "def defaults = (port = 8080)\n" +
        "type Config = typeof defaults"
    let result = Compiler.compile source
    Assert.Equal(DiagnosticCodes.MisplacedTypeof.Code, (List.head result.Diagnostics).Code)
