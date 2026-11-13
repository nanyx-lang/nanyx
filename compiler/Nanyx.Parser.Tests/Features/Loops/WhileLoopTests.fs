module ParserTests.Features.Loops.WhileLoops

open Xunit
open Parser.Program
open ParserTestHelpers

[<Fact>]
let ``while is a call to __while with the condition and the body`` () =
    let source = """
def f = {
  while remaining > 0 ->
    println("{remaining}...")
    remaining := remaining - 1
}
"""
    match parseModule (source.ReplaceLineEndings "\n") with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (Lambda ([], body, _))))) ] ->
        let loop =
            match body.Kind with
            | Block [ Kind (ExprStatement loop) ] -> loop
            | _ -> body
        match loop.Kind with
        | WhileSyntax.Call (Kind (BinaryOp (">", _, Kind (IdentifierExpr "remaining"), _)), Kind (Block [ _; _ ])) -> ()
        | other -> failwith $"Unexpected loop: %A{other}"
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``A while loop's body can be on the same line`` () =
    match parseModule "def f = while ready() -> step()" with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (WhileSyntax.Call (Kind (FunctionCall ("ready", _, [])), Kind (FunctionCall ("step", _, []))))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``A name that starts with while is not a loop`` () =
    match parseModule "def f = whileLoop(1)" with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (FunctionCall ("whileLoop", _, [ _ ]))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"
