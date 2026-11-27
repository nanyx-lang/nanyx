module Nanyx.Compiler.Tests.Features.Tooling.SelectedExpression

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// The expression written exactly at a selection, which Introduce local and the info view both find.

/// The text of the expression found at the selection between `[[` and `]]` in `marked`, if any.
let private selectedIn (marked: string) =
    let source, selection = unmarkedSelection marked
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    SelectedExpression.at source result.Syntax.Value selection
    |> Option.map (fun (expr, _, _) -> Parser.Program.SourceSpan.textIn source expr.Span)

/// What the info view describes at the selection between `[[` and `]]` in `marked`: its name or text, and its type.
let private describedAt (marked: string) =
    let source, selection = unmarkedSelection marked
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    InfoView.expressionAt TypeNotation.Ascii source result selection |> Option.map (fun described -> described.Name, described.Type)

[<Fact>]
let ``The expression written exactly at a selection is found`` () =
    Assert.Equal(Some "x + 1", selectedIn "def f = { x -> [[x + 1]] }")

[<Fact>]
let ``The spaces around a selection are left out`` () =
    Assert.Equal(Some "x + 1", selectedIn "def f = { x ->[[ x + 1 ]]}")

[<Fact>]
let ``An expression in parentheses is found without them`` () =
    Assert.Equal(Some "x + 1", selectedIn "def f = { x -> ([[x + 1]]) * 2 }")
    Assert.Equal(None, selectedIn "def f = { x -> [[(x + 1)]] * 2 }")

[<Fact>]
let ``A selection that is part of an expression finds none`` () =
    Assert.Equal(None, selectedIn "def f = { x -> [[x +]] 1 }")

[<Fact>]
let ``A selection across two statements finds none`` () =
    Assert.Equal(
        None,
        selectedIn
            """def f = {
  def a = [[1
  def b = 2]]
  a + b
}""")

[<Fact>]
let ``The info view describes the selected expression with its type`` () =
    Assert.Equal(Some ("x + 1", "int"), describedAt "def f = { x -> [[x + 1]] }")
    Assert.Equal(None, describedAt "def f = { x -> [[x +]] 1 }")

[<Fact>]
let ``With no selection the info view describes what hover does at the cursor`` () =
    Assert.Equal(Some ("x", "int"), describedAt "def f = { x -> [[]]x + 1 }")
    Assert.Equal(Some ("f", "int -> int"), describedAt "def [[]]f = { x -> x + 1 }")
