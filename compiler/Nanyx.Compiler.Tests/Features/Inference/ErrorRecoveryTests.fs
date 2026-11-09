module Nanyx.Compiler.Tests.Features.Inference.ErrorRecovery

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``Compile reports diagnostics on parse error`` () =
    let source = "def message = "
    let result = Compiler.compile source
    Assert.Equal(CompilationPhase.Parsed, result.Phase)
    Assert.False(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Every type mismatch in a module is reported`` () =
    let result = Compiler.compile "def a: int = \"x\"\ndef b: string = 1"
    let mismatches = result.Diagnostics |> List.filter (fun diag -> diag.Code = DiagnosticCodes.TypeMismatch.Code)
    Assert.Equal(2, mismatches.Length)
    Assert.Equal<int list>([ 0; 1 ], mismatches |> List.map (fun diag -> diag.Span.Value.StartLine))

[<Fact>]
let ``A def that fails to check does not hide errors in the defs after it`` () =
    let result = Compiler.compile "def a = missing\ndef b: string = 1"
    Assert.Equal(2, result.Diagnostics.Length)
    Assert.Contains("Unknown identifier", result.Diagnostics.[0].Message)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, result.Diagnostics.[1].Code)

[<Fact>]
let ``A module with errors is typed with what did check`` () =
    let result = Compiler.compile "def a = missing\ndef b = 1\ndef c: string = 2"
    Assert.False(result.TypeChecked)
    Assert.Equal(2, result.Diagnostics.Length)
    let typed = result.Typed.Value
    Assert.Equal(Unifier.errorType, typed.Types.["a"])
    Assert.Equal(TyPrimitive "int", typed.Types.["b"])
    Assert.Equal(TyPrimitive "string", typed.Types.["c"])
    Assert.True(result.Syntax.IsSome)

[<Fact>]
let ``A failed expression is compiled to a trap with its error`` () =
    let result = Compiler.compile "def f = { x -> missing(x) }\ndef g = 1"
    Assert.True(result.ErrorsDeferred)
    let panics =
        result.Typed.Value.Module
        |> List.choose (function
            | Kind (Def (ValueDef(_, "f", _, Kind (Lambda(_, Kind (FunctionCall("__panic", _, [ Kind (LiteralExpr (StringLit message)) ]))))))) -> Some message
            | _ -> None)
    Assert.Equal<string list>([ "1:16: error NYX0102: Unknown function 'missing' (deferred from compile time)\n" ], panics)

[<Fact>]
let ``A module with no errors has nothing deferred`` () =
    let result = Compiler.compile "def g = 1"
    Assert.True(result.TypeChecked)
    Assert.True(result.ErrorsDeferred)

[<Fact>]
let ``An error in a type definition is not deferred`` () =
    let result = Compiler.compile "type T = (a: Missing)\ndef g = 1"
    Assert.False(result.TypeChecked)
    Assert.False(result.ErrorsDeferred)

[<Fact>]
let ``A module that imports one with errors is not typed`` () =
    let result = compileImporting "module mod\nexport def foo = missing" "module main\nimport \"mod.nyx\"\ndef value = foo"
    Assert.True(result.Typed.IsNone)
    Assert.False(result.TypeChecked)
    Assert.Equal("Unknown identifier 'missing'", (Assert.Single result.Diagnostics).Message)

[<Fact>]
let ``The error type agrees with every type`` () =
    let a = TyVar { Id = 1; Name = None }
    let constraints: ConstraintSet =
        [ (Unifier.errorType, TyPrimitive "int", Equal, SourceSpan.none)
          (TyFunc(Unifier.errorType, TyPrimitive "int"), TyFunc(TyPrimitive "string", TyPrimitive "int"), Assignable, SourceSpan.none)
          // A variable that takes it agrees with everything from then on.
          (a, Unifier.errorType, Equal, SourceSpan.none)
          (a, TyPrimitive "int", Equal, SourceSpan.none)
          (a, TyPrimitive "string", Equal, SourceSpan.none) ]
    let _, failures = Unifier.solve constraints
    Assert.Empty(failures)

[<Fact>]
let ``Every error inside a def is reported`` () =
    let result = Compiler.compile "def f = { x ->\n  def a = missing1(x)\n  def b = missing2(x)\n  def s: string = 1\n  a + b\n}"
    Assert.Equal<string list>(
        [ "Unknown function 'missing1'"; "Unknown function 'missing2'"; "Type mismatch: int vs string." ],
        result.Diagnostics |> List.map (fun diag -> diag.Message))

[<Fact>]
let ``The uses of an expression that fails to check are not errors too`` () =
    let result = Compiler.compile "def f = { x ->\n  def y = nope\n  def a = y + 1\n  def b: string = y\n  def c = y \\contains(\"s\")\n  def d = y.length\n  a\n}"
    let diag = Assert.Single(result.Diagnostics)
    Assert.Equal("Unknown identifier 'nope'", diag.Message)

[<Fact>]
let ``The arguments of a pipe from an expression that fails to check are still checked`` () =
    let result = Compiler.compile "def f = { x ->\n  def y = nope\n  y \\contains(other)\n}"
    Assert.Equal<string list>(
        [ "Unknown identifier 'nope'"; "Unknown identifier 'other'" ],
        result.Diagnostics |> List.map (fun diag -> diag.Message))

[<Fact>]
let ``The uses of a def that fails to check are not errors too`` () =
    let result = Compiler.compile "def broken = { x -> missing(x) }\ndef a = broken(1)\ndef b = broken(\"s\")"
    let diag = Assert.Single(result.Diagnostics)
    Assert.Contains("Unknown function 'missing'", diag.Message)
