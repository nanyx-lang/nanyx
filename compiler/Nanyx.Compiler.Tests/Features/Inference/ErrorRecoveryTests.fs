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
    assertReports DiagnosticCodes.SyntaxError result

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
            | Kind (Def (ValueDef(_, "f", _, Kind (Lambda(_, Kind (FunctionCall("__panic", _, [ Kind (LiteralExpr (StringLit message)) ])), _))))) -> Some message
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
let ``Of two errors with the same message, one deferred does not make the other deferred`` () =
    // An error was taken as deferred when one with the same text was placed (#449).
    let result = Compiler.compile "type T = (a: Missing)\ndef f: Missing -> int = { x -> 1 }\ndef g = 1"
    let unknown = result.Diagnostics |> List.filter (fun diagnostic -> diagnostic.Code = DiagnosticCodes.UnknownType.Code)
    Assert.Equal<int list>([ 0; 1 ], unknown |> List.map (fun diagnostic -> diagnostic.Span.Value.StartLine))
    Assert.Equal(unknown.[0].Message, unknown.[1].Message)
    Assert.False(result.ErrorsDeferred)

[<Fact>]
let ``Two errors with the same message in different defs are both deferred`` () =
    let result = Compiler.compile "def f = { x -> missing(x) }\ndef g = { x -> missing(x) }"
    Assert.Equal(2, result.Diagnostics.Length)
    Assert.Equal(result.Diagnostics.[0].Message, result.Diagnostics.[1].Message)
    Assert.True(result.ErrorsDeferred)

[<Fact>]
let ``A module that imports one with errors is not typed`` () =
    let result = compileImporting "module mod\nexport def foo = missing" "module main\nimport mod\ndef value = foo"
    Assert.True(result.Typed.IsNone)
    Assert.False(result.TypeChecked)
    Assert.Equal("Unknown identifier 'missing'", (Assert.Single result.Diagnostics).Message)

[<Fact>]
let ``??? is reported as not implemented, deferred, and typed ???`` () =
    let result = Compiler.compile "def area = { r -> ??? }\ndef total: int = area(2) + 1"
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.NotImplemented.Code, diagnostic.Code)
    Assert.Equal("Not implemented: `???` stands for code not written yet", diagnostic.Message)
    Assert.True(result.ErrorsDeferred)
    // Its uses check: `area(2) + 1` is an int.
    Assert.EndsWith("-> ???", Unifier.tyToString result.Typed.Value.Types.["area"])
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["total"])

[<Fact>]
let ``The error type is written as the hole ???`` () =
    Assert.Equal("???", Unifier.tyToString Unifier.errorType)
    let result = Compiler.compile "def r = (a = 1, b = missing)"
    Assert.Equal("(a: int, b: ???)", Unifier.tyToString result.Typed.Value.Types.["r"])

[<Fact>]
let ``The error type agrees with every type`` () =
    let a = TyVar { Id = 1; Name = None }
    let constraints: ConstraintSet =
        [ (Unifier.errorType, TyPrimitive "int", Equal, SourceSpan.none)
          (TyFunc(Unifier.errorType, TyPrimitive "int", []), TyFunc(TyPrimitive "string", TyPrimitive "int", []), Assignable, SourceSpan.none)
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

[<Fact>]
let ``A type error in one def does not make a later match on tags an error too`` () =
    // Each union a later def gives a tag to tried its members against the rest of the module's constraints, and so
    // blamed itself for the earlier def's error.
    let source =
        normalized """def f = { s -> s + "x" }
def g = { t -> match t
  | #some(x) -> x
  | #none -> 0
}
def h: int -> #some(int) | #none = { n -> if n > 0 then #some(n) else #none }
"""
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
        Assert.Equal("'string' cannot be used with +: it is not a number. Use ++ to join them", diagnostic.Message)
        Assert.Equal(0, diagnostic.Span.Value.StartLine)
    | other -> failwith $"Expected one diagnostic, got %A{other}"

[<Fact>]
let ``A conflict in one def is reported alongside a later def's error`` () =
    // The conflict in `bad` once left `v` unresolved, so `v.major` failed, and that error stopped the module's
    // constraints being solved: the conflict itself was never reported (#170).
    let source = """
type P = (major: int)
def bad = { x -> if x then (1, #some("")) else (1, 5) }
def make: string -> #some(P) | #error(#bad) = { value -> #error(#bad) }
def main = {
  match make("x")
    | #some(v) -> v.major + missing(1)
    | #error(#bad) -> 0
}
"""
    let result = Compiler.compile source
    Assert.Equal<(string * string * int) list>(
        [ DiagnosticCodes.UnknownFunction.Code, "Unknown function 'missing'", 6
          DiagnosticCodes.TypeMismatch.Code, "Type mismatch: int vs #some(string).", 2 ],
        result.Diagnostics |> List.map (fun diag -> diag.Code, diag.Message, diag.Span.Value.StartLine))

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``A conflict between two closed tag unions is reported in its own def, naming the tags`` (conflictFirst: bool) =
    // It was reported at an unrelated match in another def, or as "Tag union length mismatch: 2 vs 2" (#170).
    let conflict = """
def applyOp: int -> #some(int) | #bad(string) = { a -> if a == 1 then #some(a) else #bad("x") }
def other: int -> #some(int) | #underflow = { a -> if a == 1 then #some(a) else #underflow }
def step = { x -> if x == 1 then applyOp else other }
"""
    let head = """
def head2: list(int) -> int = { xs ->
  match list.head(xs)
    | #some(m) -> m
    | #emptyList -> 0
}
"""
    let source = (if conflictFirst then conflict + head else head + conflict).ReplaceLineEndings "\n"
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    Assert.Equal(
        "Tag union mismatch: only the first has #bad; only the second has #underflow. Type mismatch: #some(int) | #bad(string) vs #some(int) | #underflow.",
        diagnostic.Message)
    Assert.Equal(offsetOf source "if x == 1 then applyOp" 0 0, diagnostic.Span.Value.Start)

[<Fact>]
let ``An error in an earlier def does not change what a later lambda takes`` () =
    // A `memory` block's lambda took unit rather than the block's `Memory`, and was reported as
    // "Type mismatch: @scope(@call) vs ()" (#1105).
    let source = """
def bad: int = "x"

def count: () -> int = {
  memory {
    mut n = 0
    n := n + 1
    n
  }
}
"""
    let source = source.ReplaceLineEndings "\n"
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal("Type mismatch: string vs int.", diagnostic.Message)
    Assert.Equal("\"x\"", textAt source diagnostic.Span.Value)

[<Fact>]
let ``An error in an earlier def does not stop a later lambda argument taking its parameter's type`` () =
    // `n` took no type, so `println(n)` found no `ToString` and was reported as NYX0205 too (#833).
    let source = """
import nanyx/console

def wrong: () -> int = { "not an int" }

def h: [Console] list(int) -> list(int) = { numbers ->
  numbers \list.map { n ->
    println(n)
    n + 1
  }
}
"""
    let source = source.ReplaceLineEndings "\n"
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal("Type mismatch: string vs int.", diagnostic.Message)
    Assert.Equal("\"not an int\"", textAt source diagnostic.Span.Value)