module Nanyx.Compiler.Tests.Features.Tooling.SyntaxTree

open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

let private valueOf (source: string) =
    match Compiler.parse source with
    | Ok [ Kind (Def (ValueDef(_, _, _, value))) ] -> value
    | other -> failwith $"Expected one def, got %A{other}"

[<Fact>]
let ``Every expression within one is listed, each before those within it and before its next sibling`` () =
    let source = normalized "def x = { a -> f(a, [1, a + 2]) }\n"
    let listed = SyntaxTree.expressionsWithin (valueOf source) |> List.map (fun expr -> textAt source expr.Span)
    // A call's arguments are parsed as one tuple.
    Assert.Equal<string list>(
        [ "{ a -> f(a, [1, a + 2]) }"; "f(a, [1, a + 2])"; "a, [1, a + 2]"; "a"; "[1, a + 2]"; "1"; "a + 2"; "a"; "2" ],
        listed)

[<Fact>]
let ``An expression nested a hundred thousand deep is listed without running out of stack`` () =
    // Each level is the left operand of the next, as a chain of operators parses (#1460).
    let one () = Expression.synthetic (LiteralExpr (IntLit 1I))
    let chain = Seq.init 100_000 id |> Seq.fold (fun left _ -> Expression.synthetic (BinaryOp("+", None, left, one ()))) (one ())
    Assert.Equal(200_001, (SyntaxTree.expressionsWithin chain).Length)
