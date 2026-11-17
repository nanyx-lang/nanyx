module Nanyx.Compiler.Tests.Features.WorkflowSequences

open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

// A lambda with `yield` in it, a block after `seq` included, is a lazy sequence: the `#yield` keyword kind rewrites it
// into calls to the prelude's `seq` functions (#1699).

/// The value of the def `name` in `source`, desugared.
let private desugaredDef (name: string) (source: string) : Expression =
    match parseModule (normalized source) with
    | Result.Error err -> failwith $"Parse error: {err}"
    | Result.Ok ast ->
        match WorkflowDesugaring.desugar ast |> List.tryPick (function Kind (Def (ValueDef(_, n, _, body))) when n = name -> Some body | _ -> None) with
        | Some body -> body
        | None -> failwith $"No def '{name}'"

[<Fact>]
let ``A yield is seq.cons of its value and the rest of the block, and the block ends in seq.empty`` () =
    match (desugaredDef "items" "def items = seq {\n  yield 1\n  yield 2\n}\n").Kind with
    | FunctionCall("seq", _, [ Kind (Lambda([], body, _)) ]) ->
        match body.Kind with
        | Block [ Kind (ExprStatement (Kind (FunctionCall(cons, _, [ Kind (LiteralExpr _); Kind (Lambda([], rest, false)) ])))) ] when cons = Seqs.consFunction ->
            match rest.Kind with
            | FunctionCall(cons, _, [ _; Kind (Lambda([], Kind (FunctionCall(empty, _, [])), false)) ]) when cons = Seqs.consFunction && empty = Seqs.emptyFunction -> ()
            | other -> Assert.Fail $"Unexpected rest: %A{other}"
        | other -> Assert.Fail $"Unexpected body: %A{other}"
    | other -> Assert.Fail $"Unexpected value: %A{other}"

[<Fact>]
let ``A lambda with yield in it is a sequence without a builder`` () =
    match (desugaredDef "f" "def f = { x -> yield x }\n").Kind with
    | Lambda([ "x", None ], Kind (Block [ Kind (ExprStatement (Kind (FunctionCall(cons, _, _)))) ]), _) when cons = Seqs.consFunction -> ()
    | other -> Assert.Fail $"Unexpected value: %A{other}"

[<Fact>]
let ``A seq block checks as a sequence of what it yields`` () =
    let source =
        normalized """def numbers = seq {
  yield 1
  for 2..3 | n -> yield n * 10
  yield 4
}
"""
    let typed = compileClean source
    Assert.Equal("seq(int)", Unifier.tyToString typed.Types.["numbers"])

[<Fact>]
let ``yield in a while loop is an error`` () =
    let source =
        normalized """def s = seq {
  while true ->
    yield 1
}
"""
    let code, message, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.InvalidWorkflow.Code, code)
    Assert.Equal("yield 1", text)
    Assert.Equal(
        "`yield` is in a `while` loop, so the rest of the `seq` block cannot follow it: walk a sequence with a `for` loop instead, or make the sequence with `seq.unfold`",
        message)

[<Fact>]
let ``yield bound with def is an error`` () =
    let source =
        normalized """def s = seq {
  def x = yield 1
  yield 2
}
"""
    let code, message, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.InvalidWorkflow.Code, code)
    Assert.Equal("yield 1", text)
    Assert.StartsWith("`yield` gives the sequence a value and has none of its own", message)

[<Fact>]
let ``return in a sequence is an error`` () =
    let source =
        normalized """def s = seq {
  yield 1
  return 2
}
"""
    let code, message, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.InvalidWorkflow.Code, code)
    Assert.Equal("return 2", text)
    Assert.StartsWith("`return` can't end the `seq` block, which is a sequence", message)

[<Fact>]
let ``yield outside a lambda is an error`` () =
    let code, message, text = onlyDiagnostic "yield 1\n"
    Assert.Equal(DiagnosticCodes.WorkflowKeywordOutsideWorkflow.Code, code)
    Assert.Equal("yield", text)
    Assert.Equal("`yield` is a workflow keyword, so it can only be used inside a lambda or a builder block, such as `seq { ... }`", message)

[<Fact>]
let ``yield written with an exclamation mark is an error that says to write it bare`` () =
    let source = normalized "def s = seq {\n  yield! 1\n}\n"
    let code, message, _ = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.SyntaxError.Code, code)
    Assert.Contains("`yield` is written without `!`: write `yield x`", message)

[<Fact>]
let ``yield called as a function is an error that says to write it bare`` () =
    let source = normalized "def s = seq {\n  yield(1)\n}\n"
    let code, message, _ = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.SyntaxError.Code, code)
    Assert.Contains("`yield` is not a function: write `yield x`, with no parentheses", message)
