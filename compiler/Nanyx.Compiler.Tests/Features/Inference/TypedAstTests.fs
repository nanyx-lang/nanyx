module Nanyx.Compiler.Tests.Features.Inference.TypedAst

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``Typecheck populates typed AST items`` () =
    let source = "def message = \"Hello\""
    let result = Compiler.compile source
    let typed = result.Typed.Value
    let hasMessage =
        typed.Items
        |> List.exists (function
            | TypedDef (TypedValueDef(_, name, _, typedExpr)) ->
                name = "message" && typedExpr.Type = TyPrimitive "string"
            | _ -> false)
    Assert.True(hasMessage)

[<Fact>]
let ``Typecheck captures typed block statements in lambda`` () =
    let source =
        normalized """def f = { x ->
  def y = x
  y
}"""
    let result = Compiler.compile source
    let typed = result.Typed.Value
    let hasBlockStatements =
        typed.Items
        |> List.exists (function
            | TypedDef (TypedValueDef(_, name, _, typedExpr)) when name = "f" ->
                match typedExpr.Body with
                | Some bodyExpr ->
                    match bodyExpr.Statements with
                    | Some statements ->
                        statements
                        |> List.exists (function
                            | TypedDefStatement(_, defName, _, _) -> defName = "y"
                            | _ -> false)
                    | None -> false
                | None -> false
            | _ -> false)
    Assert.True(hasBlockStatements)

[<Fact>]
let ``Typecheck supports match shorthand lambda`` () =
    let source =
        normalized """def f: string -> int = {
  | "one" -> 1
  | "two" -> 2
  | _ -> 0
}
def result = f("one")"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["result"])

[<Fact>]
let ``Typecheck infers a shorthand lambda's argument from the operators applied to it`` () =
    let source =
        normalized """def suffix = "!"
def isEven = { % 2 == 0 }
def shout = { ++ suffix }
def add = { + }
def even = isEven(4)
def loud = shout("hi")
def sum = add(1, 2)
"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "bool", typed.Types.["even"])
    // `suffix` is the def around the lambda: the lambda's argument cannot be named, so it never shadows one.
    Assert.Equal(TyPrimitive "string", typed.Types.["loud"])
    Assert.Equal(TyPrimitive "int", typed.Types.["sum"])

[<Fact>]
let ``Typecheck records typed match patterns`` () =
    let source =
        "def result = match 1\n" +
        "  | 1 -> \"one\"\n" +
        "  | _ -> \"other\""
    let result = Compiler.compile source
    let typed = result.Typed.Value
    let matchHasPattern =
        typed.Items
        |> List.exists (function
            | TypedDef (TypedValueDef(_, name, _, typedExpr)) when name = "result" ->
                match typedExpr.MatchArms with
                | Some arms ->
                    arms
                    |> List.exists (fun (patterns, _) ->
                        patterns
                        |> List.exists (fun pattern ->
                            match pattern.Pattern.Kind with
                            | LiteralPattern (IntLit64 1L) -> pattern.Type = TyPrimitive "int"
                            | _ -> false))
                | None -> false
            | _ -> false)
    Assert.True(matchHasPattern)

[<Fact>]
let ``Typecheck types list and guard patterns`` () =
    let source =
        normalized """def result = match [1, 2]
  | [x, y] -> x
  | _ -> 0"""
    let result = Compiler.compile source
    let typed = result.Typed.Value
    let hasListPattern =
        typed.Items
        |> List.exists (function
            | TypedDef (TypedValueDef(_, name, _, typedExpr)) when name = "result" ->
                match typedExpr.MatchArms with
                | Some arms ->
                    arms
                    |> List.exists (fun (patterns, _) ->
                        patterns
                        |> List.exists (fun pattern ->
                            match pattern.Pattern.Kind with
                            | ListPattern _ -> true
                            | _ -> false))
                | None -> false
            | _ -> false)
    Assert.True(hasListPattern)
