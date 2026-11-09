module Nanyx.Compiler.Tests.Features.Workflows

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``A workflow keyword in a branch of an if or match statement has the rest of the block as its continuation`` () =
    // Each branch goes on to the rest of the block (#348): a `try` in one branch widens the block's failures, and the
    // value a branch ends with is what a `def` of the statement binds.
    let source =
        "def halve: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }\n" +
        "def negative: int -> #some(int) | #negative = { n -> if n < 0 then #negative else #some(n) }\n" +
        "def check = { n -> proc {\n" +
        "  if n < 0 then try negative(n)\n" +
        "  def half = try halve(n)\n" +
        "  return half\n" +
        "} }\n" +
        // Annotated: inferred, a failure only one branch adds is dropped (#354).
        "def label: int -> #some(string) | #odd = { n -> proc {\n" +
        "  def text = match n\n" +
        "    | 0 -> \"zero\"\n" +
        "    | _ ->\n" +
        "      def h = try halve(n)\n" +
        "      \"half is {h}\"\n" +
        "  return text\n" +
        "} }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("int -> #some(int) | #negative | #odd", Unifier.tyToString result.Typed.Value.Types.["check"])

[<Fact>]
let ``A workflow keyword the rest of its block cannot follow is reported where it is written`` () =
    // It used to become a call to the builder's member with no continuation, and fail as a type mismatch (#347).
    let reported (body: string) =
        let source =
            "def halve: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }\n" +
            "def f: list(int) -> #some(int) | #odd = { xs -> proc {\n" + body + "\n  return 0\n} }"
        match (Compiler.compile source).Diagnostics with
        | [ diagnostic ] ->
            Assert.Equal(DiagnosticCodes.InvalidWorkflow.Code, diagnostic.Code)
            let span = diagnostic.Span.Value
            Assert.Equal("try", source.Substring(span.Start, 3))
            diagnostic.Message
        | other -> failwith $"Expected one diagnostic, got %A{other}"
    Assert.Equal(
        "`try` is in the body of a `for` loop, so the rest of the `proc` block cannot follow it: handle the value with a `match` in the loop's body",
        reported "  for xs | x -> halve(try halve(x))")
    Assert.Equal(
        "`try` is inside a lambda, so the rest of the `proc` block cannot follow it: move it out of the lambda, or handle the value with a `match` there",
        reported "  def halves = List.map(xs, { x -> try halve(x) })")
    Assert.Equal(
        "`try` is in a part of an expression that may not run, so the rest of the `proc` block cannot follow it: make the `if` or `match` it is in a statement of its own",
        reported "  def m = 1 + (if true then try halve(2) else 0)")
    // A builder block of its own inside a lambda has its own continuation.
    let nested =
        "def halve: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }\n" +
        "def f = { xs -> proc {\n" +
        "  def results = List.map(xs, { x -> proc {\n" +
        "    def h = try halve(x)\n" +
        "    return h\n" +
        "  } })\n" +
        "  return results\n" +
        "} }"
    Assert.Empty((Compiler.compile nested).Diagnostics)

[<Fact>]
let ``Desugar workflow bind and return`` () =
    let source =
        "def result = async {\n" +
        "  def x = await! foo()\n" +
        "  return! x.y\n" +
        "}"
    match parseModule source with
    | Result.Error err -> Assert.True(false, $"Parse error: {err}")
    | Result.Ok ast ->
        let desugared = Compiler.desugar ast
        // `async { ... }` is its block, each keyword calling the builder's member through a local of its own.
        match desugared with
        | [Kind (Def (ValueDef(_, "result", _, Kind (Block [Kind (ExprStatement expr)]))))] ->
            match expr.Kind with
            | Block [
                Kind (DefStatement(_, awaitLocal, None, Kind (MemberAccess(Kind (IdentifierExpr "async"), "await", _))))
                Kind (ExprStatement (Kind (FunctionCall(awaitCall, _, [ Kind (FunctionCall("foo", _, [])); Kind (Lambda([("x", None)], continuation)) ]))))
              ] when awaitCall = awaitLocal ->
                match continuation.Kind with
                | Block [
                    Kind (DefStatement(_, returnLocal, None, Kind (MemberAccess(Kind (IdentifierExpr "async"), "return", _))))
                    Kind (ExprStatement (Kind (FunctionCall(returnCall, _, [ Kind (MemberAccess(Kind (IdentifierExpr "x"), "y", _)) ]))))
                  ] when returnCall = returnLocal -> ()
                | other -> Assert.True(false, $"Unexpected workflow continuation: %A{other}")
            | other -> Assert.True(false, $"Unexpected workflow desugar: %A{other}")
        | other -> Assert.True(false, $"Unexpected desugared module: %A{other}")

[<Fact>]
let ``Desugar workflow implicit return`` () =
    let source =
        "def result = async {\n" +
        "  1\n" +
        "}"
    match parseModule source with
    | Result.Error err -> Assert.True(false, $"Parse error: {err}")
    | Result.Ok ast ->
        let desugared = Compiler.desugar ast
        match desugared with
        | [Kind (Def (ValueDef(_, "result", _, Kind (FunctionCall("async", _, [Kind (Lambda([], Kind (Block [Kind (ExprStatement (Kind (LiteralExpr (IntLit 1))))])))])))))] ->
            Assert.True(true)
        | other -> Assert.True(false, $"Unexpected desugared module: %A{other}")

[<Fact>]
let ``Typecheck workflow context members`` () =
    let source =
        "context AsyncWorkflow = (await: (int, (int -> int)) -> int return: int -> int)\n" +
        "def async: AsyncWorkflow = (await = { x, f -> f(x) }, return = { x -> x })\n" +
        "def result = async {\n" +
        "  def x = await! 1\n" +
        "  return! x\n" +
        "}"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Typecheck workflow requires return`` () =
    let source =
        "context AsyncWorkflow = (await: (int, (int -> int)) -> int)\n" +
        "def async: AsyncWorkflow = (await = { x, f -> f(x) })\n" +
        "def result = async {\n" +
        "  return! 1\n" +
        "}"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("return", message)

[<Fact>]
let ``A proc block is a result of its return value or of the first error`` () =
    // Each `try` instantiates `proc` afresh, so one block can bind payloads of different types.
    let source =
        "import nanyx/result\n" +
        "def f: int -> #some(int) | #error(#tooBig) = { n -> if n > 5 then #error(#tooBig) else #some(n) }\n" +
        "def g: int -> #some(string) | #error(#tooBig) = { n -> #some(\"{n}\") }\n" +
        "def run = { x -> proc {\n" +
        "  def a = try f(x)\n" +
        "  try f(a)\n" +
        "  def s = try g(a)\n" +
        "  return s\n" +
        "} }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("int -> #some(string) | #error(#tooBig)", Unifier.tyToString result.Typed.Value.Types.["run"])

[<Fact>]
let ``A proc block that uses no workflow keyword is its block`` () =
    // It used to be checked for the members `await` and `pure`, which no builder has (#346).
    let source =
        "def total = proc {\n" +
        "  def a = 20\n" +
        "  a + 22\n" +
        "}\n" +
        "def nothing = { proc { () } }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("int", Unifier.tyToString result.Typed.Value.Types.["total"])

[<Fact>]
let ``A proc block collects the failures of each try`` () =
    let source =
        "import nanyx/result\n" +
        "def name: int -> #some(string) | #nil = { n -> if n > 5 then #nil else #some(\"n\") }\n" +
        "def find: string -> #some(int) | #notFound = { s -> if s == \"n\" then #some(1) else #notFound }\n" +
        "def run = { x -> proc {\n" +
        "  def s = try name(x)\n" +
        "  def i = try find(s)\n" +
        "  return i\n" +
        "} }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("int -> #some(int) | #notFound | #nil", Unifier.tyToString result.Typed.Value.Types.["run"])
    // Both failures are in the block's type, so a match on it must handle both.
    let partial =
        source + "\n" +
        "def shown = match run(1)\n" +
        "  | #some(_) -> \"some\"\n" +
        "  | #nil -> \"nil\""
    let partialResult = Compiler.compile partial
    Assert.False(partialResult.Diagnostics.IsEmpty)
    Assert.Contains("notFound", (List.head partialResult.Diagnostics).Message)

[<Fact>]
let ``A try nested in an expression of a proc block is bound before its statement`` () =
    let source =
        "import nanyx/result\n" +
        "def f: int -> #some(int) | #error(#tooBig) = { n -> if n > 5 then #error(#tooBig) else #some(n) }\n" +
        "def run = { x -> proc {\n" +
        "  return (a = try f(x), b = try f(try f(x)))\n" +
        "} }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("int -> #some(a: int, b: int) | #error(#tooBig)", Unifier.tyToString result.Typed.Value.Types.["run"])
    // The binds are lifted left to right, inner ones first: `f(x)` for `a`, then the inner `f(x)`, then `f` of it.
    match parseModule source with
    | Result.Error err -> Assert.True(false, $"Parse error: {err}")
    | Result.Ok ast ->
        let rec boundCalls (expr: Expression) =
            match expr.Kind with
            | Block statements ->
                statements
                |> List.collect (function
                    | Kind (DefStatement(_, _, _, value))
                    | Kind (ExprStatement value) -> boundCalls value
                    | _ -> [])
            | FunctionCall(_, _, [ Kind (FunctionCall("f", _, [ argument ])); Kind (Lambda(_, rest)) ]) ->
                argument :: boundCalls rest
            | Lambda(_, body) -> boundCalls body
            | _ -> []
        match Compiler.desugar ast |> List.tryPick (function Kind (Def (ValueDef(_, "run", _, body))) -> Some body | _ -> None) with
        | Some body ->
            let arguments = boundCalls body |> List.map (fun argument -> argument.Kind)
            match arguments with
            | [ IdentifierExpr "x"; IdentifierExpr "x"; IdentifierExpr inner ] when inner <> "x" -> ()
            | other -> Assert.True(false, $"Unexpected binds: %A{other}")
        | None -> Assert.True(false, "run not found")
