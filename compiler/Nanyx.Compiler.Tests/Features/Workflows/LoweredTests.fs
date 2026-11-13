module Nanyx.Compiler.Tests.Features.WorkflowLowering

open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

let private halve = """def halve: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }"""

/// The expression of `result`'s syntax written at `text` (its first occurrence) that `isIt` picks.
let private writtenAt (source: string) (result: CompileResult) (text: string) (isIt: Expression -> bool) =
    result.Syntax.Value
    |> List.collect SyntaxTree.ofItem
    |> List.collect SyntaxTree.expressionsWithin
    |> List.find (fun expr -> expr.Span.Start = offsetOf source text 0 0 && isIt expr)

/// The types of the expressions made up for `node`, in the order they were made.
let private loweredTypes (result: CompileResult) (node: NodeId) =
    result.Lowered.[node] |> List.choose (fun id -> result.Typed.Value.ExprTypes |> Map.tryFind id |> Option.map Unifier.tyToString)

[<Fact>]
let ``A workflow bind's made-up call and continuation are found from the bind as written`` () =
    let source =
        $"""{halve}
def quarter = {{ n -> proc {{
  def h = try halve(n)
  return h / 2
}} }}"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"%A{result.Diagnostics}")
    let bind = writtenAt source result "try" (fun expr -> match expr.Kind with WorkflowBindExpr _ -> true | _ -> false)
    let types = loweredTypes result bind.Id
    // The continuation takes what the bind gives the name `h`, and the call is the rest of the workflow.
    Assert.Equal("int -> #some(int)", types.Head)
    Assert.Contains("#some(int) | #odd", types)
    // Every made-up expression is in the desugared module, so each has a type.
    Assert.Equal(result.Lowered.[bind.Id].Length, types.Length)

[<Fact>]
let ``A workflow's return is found from the return as written`` () =
    let source =
        $"""{halve}
def quarter = {{ n -> proc {{
  def h = try halve(n)
  return h / 2
}} }}"""
    let result = Compiler.compile source
    let returned = writtenAt source result "return" (fun expr -> match expr.Kind with WorkflowReturnExpr _ -> true | _ -> false)
    Assert.True(result.Diagnostics.IsEmpty, $"%A{result.Diagnostics}")
    Assert.Contains("#some(int)", loweredTypes result returned.Id)

[<Fact>]
let ``The call @entry makes up is found from the attribute`` () =
    let source =
        """@entry
def main = { 1 }"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"%A{result.Diagnostics}")
    let attribute = result.Syntax.Value |> List.find (function Kind (Attribute _) -> true | _ -> false)
    Assert.Equal<string list>([ "int" ], loweredTypes result attribute.Id)

[<Fact>]
let ``A type error in a workflow bind is reported at the bind`` () =
    let source =
        $"""{halve}
def quarter = {{ n -> proc {{
  def h = try halve("two")
  return h / 2
}} }}"""
    let code, text = singleError source
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("\"two\"", text)

[<Fact>]
let ``A type error in a workflow's return is reported in the return`` () =
    let source =
        $"""{halve}
def quarter: int -> #some(int) | #odd = {{ n -> proc {{
  def h = try halve(n)
  return "half"
}} }}"""
    let code, text = singleError source
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("\"half\"", text)
