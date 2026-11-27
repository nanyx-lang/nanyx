module Nanyx.Compiler.Tests.Features.WorkflowSequences

open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

// A lambda with `yield` in it, a block after `seq` included, is a lazy sequence: the `#yield` keyword kind rewrites it
// into calls to the prelude's `seq` functions (#1699). A `yield` or a `for` loop the block ends with makes no rest
// (#2425).

/// The value of the def `name` in `source`, desugared.
let private desugaredDef (name: string) (source: string) : Expression =
    match parseModule (normalized source) with
    | Result.Error err -> failwith $"Parse error: {err}"
    | Result.Ok ast ->
        match WorkflowDesugaring.desugar ast |> List.tryPick (function Kind (Def (ValueDef(_, n, _, body))) when n = name -> Some body | _ -> None) with
        | Some body -> body
        | None -> failwith $"No def '{name}'"

[<Fact>]
let ``A yield is seq.cons of its value and the rest of the block, and the last yield is the sequence of its value alone`` () =
    match (desugaredDef "items" "def items = seq {\n  yield 1\n  yield 2\n}\n").Kind with
    | FunctionCall("seq", _, [ Kind (Lambda([], body, _)) ]) ->
        match body.Kind with
        | Block [ Kind (ExprStatement (Kind (FunctionCall(cons, _, [ Kind (LiteralExpr _); Kind (Lambda([], rest, false)) ])))) ] when cons = Seqs.consFunction ->
            match rest.Kind with
            | FunctionCall(last, _, [ Kind (LiteralExpr _) ]) when last = Seqs.lastFunction -> ()
            | other -> Assert.Fail $"Unexpected rest: %A{other}"
        | other -> Assert.Fail $"Unexpected body: %A{other}"
    | other -> Assert.Fail $"Unexpected value: %A{other}"

[<Fact>]
let ``A for loop the block ends with is the loop's sequence alone, and one followed by more is appended to it`` () =
    let bodyOf (name: string) (source: string) =
        match (desugaredDef name source).Kind with
        | FunctionCall("seq", _, [ Kind (Lambda([], Kind (Block [ Kind (ExprStatement body) ]), _)) ]) -> body.Kind
        | other -> failwith $"Unexpected value: %A{other}"
    // A body that yields more than once is seq.flatMap's.
    match bodyOf "last" "def last = seq {\n  for 1..3 | i ->\n    yield i\n    yield i\n}\n" with
    | FunctionCall(flatMap, _, [ _; _ ]) when flatMap = Seqs.flatMapFunction -> ()
    | other -> Assert.Fail $"Unexpected loop: %A{other}"
    // One that yields once each time round is a map (#2208).
    match bodyOf "more" "def more = seq {\n  for 1..3 | i -> yield i\n  yield 4\n}\n" with
    | FunctionCall(append, _, [ Kind (FunctionCall(map, _, _)); _ ]) when append = Seqs.appendFunction && map = Seqs.mapFunction -> ()
    | other -> Assert.Fail $"Unexpected loop: %A{other}"

[<Fact>]
let ``A for loop whose body yields once each time round, in each branch of an if or a match, is a map`` () =
    let bodyOf (name: string) (source: string) =
        match (desugaredDef name source).Kind with
        | FunctionCall("seq", _, [ Kind (Lambda([], Kind (Block [ Kind (ExprStatement body) ]), _)) ]) -> body.Kind
        | other -> failwith $"Unexpected value: %A{other}"
    let isMap (kind: ExpressionKind) = match kind with FunctionCall(map, _, [ _; _ ]) -> map = Seqs.mapFunction | _ -> false
    Assert.True(isMap (bodyOf "one" "def one = seq {\n  for 1..3 | i -> yield i\n}\n"))
    Assert.True(isMap (bodyOf "both" "def both = seq {\n  for 1..3 | i -> if i > 1 then yield i else yield 0\n}\n"))
    // One that can yield nothing, an `if` with no `else`, is not.
    Assert.False(isMap (bodyOf "some" "def some = seq {\n  for 1..3 | i -> if i > 1 then yield i\n}\n"))

[<Fact>]
let ``A lambda with yield in it is a sequence without a builder`` () =
    match (desugaredDef "f" "def f = { x -> yield x }\n").Kind with
    | Lambda([ "x", None ], Kind (Block [ Kind (ExprStatement (Kind (FunctionCall(last, _, _)))) ]), _) when last = Seqs.lastFunction -> ()
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
