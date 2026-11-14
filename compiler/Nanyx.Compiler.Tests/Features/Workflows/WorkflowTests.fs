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
    // value a branch ends with is what a `def` of the statement binds. In `label`, a failure only one branch adds is in
    // an annotation too.
    let source =
        normalized """def halve: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }
def negative: int -> #some(int) | #negative = { n -> if n < 0 then #negative else #some(n) }
def check = { n -> proc {
  if n < 0 then try negative(n)
  def half = try halve(n)
  return half
} }
def label: int -> #some(string) | #odd = { n -> proc {
  def text = match n
    | 0 -> "zero"
    | _ ->
      def h = try halve(n)
      "half is {h}"
  return text
} }"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("int -> #some(int) | #negative | #odd", Unifier.tyToString result.Typed.Value.Types.["check"])

[<Fact>]
let ``The values the branches give the rest of the block are joined, and a match there must cover them all`` () =
    let source (arms: string) =
        $"""def sized = {{ n -> proc {{
  def size = match n
    | 0 -> return "none"
    | 1 -> #one
    | _ -> #many(n)
  match size
{arms}
}} }}
"""
    let result = Compiler.compile ((source "    | #one -> return \"one\"\n    | #many(k) -> return \"{k}\"").ReplaceLineEndings "\n")
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("int -> #some(string)", Unifier.tyToString result.Typed.Value.Types.["sized"])
    let code, text = singleError ((source "    | #one -> return \"one\"").ReplaceLineEndings "\n")
    Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, code)
    Assert.StartsWith("match size", text)

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
        "`try` is in the body of a `for` loop that is part of an expression or walks several iterables, so the rest of the `proc` block cannot follow it: make the loop a statement of its own over one list, or handle the value with a `match` in the loop's body",
        reported "  for xs, xs | x, y -> halve(try halve(x))")
    Assert.Equal(
        "`try` is inside a lambda, so the rest of the `proc` block cannot follow it: move it out of the lambda, or handle the value with a `match` there",
        reported "  def halves = list.map(xs, { x -> try halve(x) })")
    Assert.Equal(
        "`try` is in a part of an expression that may not run, so the rest of the `proc` block cannot follow it: make the `if` or `match` it is in a statement of its own",
        reported "  def m = false or (if true then try halve(2) == 1 else false)")
    // A builder block of its own inside a lambda has its own continuation.
    let nested =
        normalized """def halve: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }
def f = { xs -> proc {
  def results = list.map(xs, { x -> proc {
    def h = try halve(x)
    return h
  } })
  return results
} }"""
    Assert.Empty((Compiler.compile nested).Diagnostics)

[<Fact>]
let ``try and return outside a builder block are reported as workflow keywords`` () =
    // They used to be calls to functions named `try` and `return`, reported as unknown functions (#350).
    let halve = "def halve: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }\n"
    let reported (keyword: string) (definition: string) =
        let source = halve + definition
        let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
        Assert.Equal(DiagnosticCodes.WorkflowKeywordOutsideWorkflow.Code, diagnostic.Code)
        let span = diagnostic.Span.Value
        Assert.Equal(keyword, source.Substring(span.Start, span.End - span.Start))
        Assert.Equal(source.IndexOf(keyword, halve.Length), span.Start)
        diagnostic.Message
    let message (keyword: string) =
        $"`{keyword}` is a workflow keyword, so it can only be used inside a builder block, such as `proc {{ ... }}`"
    Assert.Equal(message "try", reported "try" "def a = try halve(4)")
    Assert.Equal(message "return", reported "return" "def a = return 4")
    let inFunction =
        """def f = { n ->
  def h = try halve(n)
  h + 1
}"""
    Assert.Equal(message "try", reported "try" (inFunction.ReplaceLineEndings "\n"))
    Assert.Equal(message "return", reported "return" "def f = { n -> return n }")
    // Inside a builder block they are the builder's keywords, as before.
    let inProc =
        """def f = { n -> proc {
  def h = try halve(n)
  return h + 1
} }"""
    let result = Compiler.compile (halve + inProc.ReplaceLineEndings "\n")
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("int -> #some(int) | #odd", Unifier.tyToString result.Typed.Value.Types.["f"])

[<Fact>]
let ``Desugar workflow bind and return`` () =
    let source =
        normalized """def result = async {
  def x = await! foo()
  return! x.y
}"""
    match parseModule source with
    | Result.Error err -> Assert.True(false, $"Parse error: {err}")
    | Result.Ok ast ->
        let desugared = WorkflowDesugaring.desugar ast
        // `async { ... }` is the call `async({ ... })`, and in the block each keyword calls the member of that name of
        // the context `async` runs it under.
        match desugared with
        | [ Kind (Def (ValueDef(_, "result", _, Kind (FunctionCall("async", _, [ Kind (Lambda([], Kind (Block [ Kind (ExprStatement expr) ]), _)) ]))))) ] ->
            match expr.Kind with
            | FunctionCall("await", _, [ Kind (FunctionCall("foo", _, [])); Kind (Lambda([ ("x", None) ], continuation, _)) ]) ->
                match continuation.Kind with
                | FunctionCall("return", _, [ Kind (MemberAccess(Kind (IdentifierExpr "x"), "y", _)) ]) -> ()
                | other -> Assert.True(false, $"Unexpected workflow continuation: %A{other}")
            | other -> Assert.True(false, $"Unexpected workflow desugar: %A{other}")
        | other -> Assert.True(false, $"Unexpected desugared module: %A{other}")

[<Fact>]
let ``Desugar a builder block after the builder's arguments`` () =
    // `retrying(3) { ... }` is `retrying(3, { ... })`: the arguments are outside the block.
    let source =
        normalized """def result = retrying(3) {
  def x = try foo()
  return x
}"""
    match parseModule source with
    | Result.Error err -> Assert.True(false, $"Parse error: {err}")
    | Result.Ok ast ->
        match WorkflowDesugaring.desugar ast with
        | [ Kind (Def (ValueDef(_, "result", _, Kind (FunctionCall("retrying", _, [ Kind (LiteralExpr (IntLit64 3L)); Kind (Lambda([], body, _)) ]))))) ] ->
            match body.Kind with
            | Block [ Kind (ExprStatement (Kind (FunctionCall("try", _, [ _; Kind (Lambda([ ("x", None) ], _, _)) ])))) ] -> ()
            | other -> Assert.True(false, $"Unexpected block: %A{other}")
        | other -> Assert.True(false, $"Unexpected desugared module: %A{other}")

[<Fact>]
let ``Desugar workflow implicit return`` () =
    let source =
        normalized """def result = async {
  1
}"""
    match parseModule source with
    | Result.Error err -> Assert.True(false, $"Parse error: {err}")
    | Result.Ok ast ->
        let desugared = WorkflowDesugaring.desugar ast
        match desugared with
        | [Kind (Def (ValueDef(_, "result", _, Kind (FunctionCall("async", _, [Kind (Lambda([], Kind (Block [Kind (ExprStatement (Kind (LiteralExpr (IntLit64 1L))))]), _))])))))] ->
            ()
        | other -> Assert.True(false, $"Unexpected desugared module: %A{other}")

[<Fact>]
let ``A builder is a function that runs its block under a context whose members are its keywords`` () =
    let source =
        normalized """context AsyncWorkflow = (
  await: [c] (int, ([c] int -> int)) -> int
  return: int -> int
)
def async: [c] ([AsyncWorkflow & c] () -> int) -> int = { block ->
  use AsyncWorkflow(await = { x, f -> f(x) }, return = { x -> x }) in block()
}
def result = async {
  def x = await! 1
  return! x
}"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("int", Unifier.tyToString result.Typed.Value.Types.["result"])

[<Fact>]
let ``A keyword the builder's context has no member for is reported at the keyword`` () =
    let source =
        normalized """context AsyncWorkflow = (await: [c] (int, ([c] int -> int)) -> int)
def async: [c] ([AsyncWorkflow & c] () -> int) -> int = { block ->
  use AsyncWorkflow(await = { x, f -> f(x) }) in block()
}
def result = async {
  return! 1
}"""
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.InvalidWorkflow.Code, diagnostic.Code)
    Assert.Equal(
        "`return` is a keyword of this `async` block, but the context `async` runs it under has no member `return`: give the context a `return`, or write the block after a builder that has one",
        diagnostic.Message)
    Assert.Equal("return! 1", textAt source (spanOf diagnostic))

[<Fact>]
let ``A builder takes parameters, and its block requires contexts of its own`` () =
    // `retrying(3) { ... }` passes `3` before the block, and the block's `println` requires `Console`, which the
    // builder's context variable carries to where the builder is called.
    let source =
        normalized """import nanyx/console
context Retry = (
  try: [c] ((#some(a) | #failed), ([c] a -> #some(b) | #failed)) -> #some(b) | #failed
  return: a -> #some(a) | #failed
)
def retrying: [c] int, ([Retry & c] () -> #some(b) | #failed) -> #some(b) | #failed = { times, block ->
  use Retry(
    try = {
      | #some(value), continue -> continue(value)
      | #failed, _ -> #failed
    }
    return = { value -> #some(value) }
  ) in (if times > 0 then block() else #failed)
}
def attempt: [Console] () -> #some(int) | #failed = { retrying(3) {
  def x = try #some(2)
  println("got {x}")
  return x * 2
} }"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)

[<Fact>]
let ``A proc block is a result of its return value or of the first error`` () =
    // Each `try` instantiates `proc` afresh, so one block can bind payloads of different types.
    let source =
        normalized """import nanyx/result
def f: int -> #some(int) | #error(#tooBig) = { n -> if n > 5 then #error(#tooBig) else #some(n) }
def g: int -> #some(string) | #error(#tooBig) = { n -> #some("{n}") }
def run = { x -> proc {
  def a = try f(x)
  try f(a)
  def s = try g(a)
  return s
} }"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("int -> #some(string) | #error(#tooBig)", Unifier.tyToString result.Typed.Value.Types.["run"])

[<Fact>]
let ``A proc block that uses no workflow keyword is an ordinary call of proc`` () =
    // It used to be checked for the members `await` and `pure`, which no builder has (#346), and then to be its block
    // as written. Now it is `proc({ ... })`, whose block must be a result.
    let source =
        normalized """def total = proc {
  def a = 20
  #some(a + 22)
}"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("#some(int)", Unifier.tyToString result.Typed.Value.Types.["total"])
    let code, text = singleError "def three = proc { 1 + 2 }"
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("{ 1 + 2 }", text)

[<Fact>]
let ``A proc block collects the failures of each try`` () =
    let source =
        normalized """import nanyx/result
def name: int -> #some(string) | #nil = { n -> if n > 5 then #nil else #some("n") }
def find: string -> #some(int) | #notFound = { s -> if s == "n" then #some(1) else #notFound }
def run = { x -> proc {
  def s = try name(x)
  def i = try find(s)
  return i
} }"""
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
    assertReports DiagnosticCodes.NonExhaustiveMatch partialResult
    Assert.Contains("notFound", (List.head partialResult.Diagnostics).Message)

[<Fact>]
let ``A proc block's failures include a try's though only one branch has it`` () =
    // The joined branches used to drop the failure of a `try` that only one of them has (#354).
    let halve = "def halve: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }\n"
    let inferred (body: string) =
        let result = Compiler.compile (halve + "def label = { n -> proc {\n" + body + "\n  return text\n} }")
        Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
        Unifier.tyToString result.Typed.Value.Types.["label"]
    let withMatch =
        """  def text = match n
    | 0 -> "zero"
    | _ ->
      def h = try halve(n)
      "half is {h}" """
    let withIf =
        """  def text = if n == 0 then "zero" else
    def h = try halve(n)
    "half is {h}" """
    Assert.Equal("int -> #some(string) | #odd", inferred withMatch)
    Assert.Equal("int -> #some(string) | #odd", inferred withIf)

[<Fact>]
let ``An annotation that leaves out a try's failure is a mismatch`` () =
    // It used to be accepted, though `label(3)` is `#odd` (#354).
    let halve = "def halve: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }\n"
    let diagnosticCodes (body: string) =
        let result = Compiler.compile (halve + "def label: int -> #some(string) = { n -> proc {\n" + body + "\n} }")
        result.Diagnostics |> List.map (fun diagnostic -> diagnostic.Code)
    let straight =
        """  def h = try halve(n)
  return "half is {h}" """
    let withIf =
        """  def text = if n == 0 then "zero" else
    def h = try halve(n)
    "half is {h}"
  return text"""
    Assert.Equal<string list>([ DiagnosticCodes.TypeMismatch.Code ], diagnosticCodes straight)
    Assert.Equal<string list>([ DiagnosticCodes.TypeMismatch.Code ], diagnosticCodes withIf)

[<Fact>]
let ``A bare tag given to try in one branch widens the block's failures`` () =
    // The tag used to close the rest of the union `try` expects, so the branches failed to agree (#353).
    let halve = "def halve: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }\n"
    let inferred (source: string) =
        let result = Compiler.compile (halve + source)
        Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
        Unifier.tyToString result.Typed.Value.Types.["f"]
    // `bindSome` has the type of `Proc`'s `try`.
    let byHand (branches: string) =
        $"""def f = {{ n ->
{branches}
}}"""
    let withKeywords =
        """def f = { n -> proc {
  if n < 0 then try #negative
  def h = try halve(n)
  return h
} }"""
    // A union's tags are in the order they were found.
    Assert.Equal<string list>(
        [ "int -> #some(int) | #odd | #negative"; "int -> #some(int) | #odd | #negative"; "int -> #some(int) | #negative | #odd" ],
        [ inferred (byHand "  if n < 0 then bindSome(#negative, { v -> #some(v) }) else bindSome(halve(n), { v -> #some(v) })")
          inferred (byHand "  match n\n    | 0 -> bindSome(#negative, { v -> #some(v) })\n    | _ -> bindSome(halve(n), { v -> #some(v) })")
          inferred withKeywords ])

[<Fact>]
let ``A try nested in an expression of a proc block is bound before its statement`` () =
    let source =
        normalized """import nanyx/result
def f: int -> #some(int) | #error(#tooBig) = { n -> if n > 5 then #error(#tooBig) else #some(n) }
def run = { x -> proc {
  return (a = try f(x), b = try f(try f(x)))
} }"""
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
            | FunctionCall(_, _, [ Kind (FunctionCall("f", _, [ argument ])); Kind (Lambda(_, rest, _)) ]) ->
                argument :: boundCalls rest
            | FunctionCall("proc", _, [ Kind (Lambda([], body, _)) ])
            | Lambda(_, body, _) -> boundCalls body
            | _ -> []
        match WorkflowDesugaring.desugar ast |> List.tryPick (function Kind (Def (ValueDef(_, "run", _, body))) -> Some body | _ -> None) with
        | Some body ->
            let arguments = boundCalls body |> List.map (fun argument -> argument.Kind)
            match arguments with
            | [ IdentifierExpr "x"; IdentifierExpr "x"; IdentifierExpr inner ] when inner <> "x" -> ()
            | other -> Assert.True(false, $"Unexpected binds: %A{other}")
        | None -> Assert.True(false, "run not found")

[<Fact>]
let ``A proc block that ends without return is #some()`` () =
    let source =
        normalized """def f: int -> #some(int) | #error(#tooBig) = { n -> if n > 5 then #error(#tooBig) else #some(n) }
def run = { x -> proc {
  def a = try f(x)
  a + 1
} }
def check = { x -> proc {
  if x < 0 then try #error(#tooBig)
} }"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("int -> #some() | #error(#tooBig)", Unifier.tyToString result.Typed.Value.Types.["run"])
    Assert.Equal("int -> #some() | #error(#tooBig)", Unifier.tyToString result.Typed.Value.Types.["check"])

/// A builder `opt` like `proc`, whose context `Opt` has a `zero` of `#nothing` when `withZero`, and then the definition of
/// `half`.
let private optBuilder (withZero: bool) =
    let zeroMember, zeroValue = if withZero then "\n  zero: #nothing | r", "\n    zero = #nothing" else "", ""
    $"""context Opt = (
  try: [c] ((#some(a) | r), ([c] a -> #some(b) | r)) -> #some(b) | r
  return: a -> #some(a) | r{zeroMember}
)
def opt: [c] ([Opt & c] () -> t) -> t = {{ block ->
  use Opt(
    try = {{
      | #some(value), continue -> continue(value)
      | failure, _ -> failure
    }}
    return = {{ value -> #some(value) }}{zeroValue}
  ) in block()
}}
def half: int -> #some(int) | #odd = {{ n -> if n %% 2 == 0 then #some(n / 2) else #odd }}
"""
        .ReplaceLineEndings "\n"

[<Fact>]
let ``A builder block that can end without return needs the builder's zero`` () =
    let builder = optBuilder false
    let fallsOff = builder + "def show = { n -> opt {\n  def h = try half(n)\n  h + 1\n} }\n"
    let result = Compiler.compile fallsOff
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.InvalidWorkflow.Code, diagnostic.Code)
    Assert.Equal("h + 1", fallsOff.Substring(diagnostic.Span.Value.Start, diagnostic.Span.Value.End - diagnostic.Span.Value.Start))
    Assert.Contains("the context `opt` runs it under has no `zero`", diagnostic.Message)
    // A block that always ends with `return` never needs it.
    Assert.Empty((Compiler.compile (builder + "def show = { n -> opt {\n  def h = try half(n)\n  return h + 1\n} }\n")).Diagnostics)

[<Fact>]
let ``A builder's zero is the value of a block that ends without return`` () =
    let source =
        optBuilder true + """def show = { n -> opt {
  def h = try half(n)
  h + 1
} }
"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let shown = Unifier.tyToString result.Typed.Value.Types.["show"]
    for tag in [ "#nothing"; "#odd" ] do
        Assert.Contains(tag, shown)
[<Fact>]
let ``A proc block that returns a value on one path and ends without return on another is a mismatch`` () =
    // `return n` makes the block a `#some(int)`, and ending without `return` makes it `#some()`.
    let result = Compiler.compile "def c = { n -> proc {\n  if n > 2 then return n\n} }"
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    Assert.Contains("() vs int", diagnostic.Message)
[<Fact>]
let ``An if or match keeps every tag of a branch whose type is an open union`` () =
    // A branch that is `#nothing | r` used to be made equal to a `#some(...)` one, which dropped `#nothing` (#433).
    let source =
        normalized """def nothing: () -> #nothing | r = { #nothing }
def h = { c -> if c then #some(1) else nothing() }
def m = { n -> match n
  | 0 -> #some("zero")
  | _ -> nothing()
}
def k = { c ->
  def __rest_1 = { _ -> nothing() }
  if c then #some(1) else __rest_1(())
}"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("bool -> #some(int) | #nothing", Unifier.tyToString result.Typed.Value.Types.["h"])
    Assert.Equal("int -> #some(string) | #nothing", Unifier.tyToString result.Typed.Value.Types.["m"])
    Assert.Equal("bool -> #some(int) | #nothing", Unifier.tyToString result.Typed.Value.Types.["k"])

[<Fact>]
let ``A builder's zero is part of the block's type when a branch returns a value`` () =
    let source =
        optBuilder true + """def show = { n -> opt {
  def h = try half(n)
  if h > 2 then return h
} }
"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let shown = Unifier.tyToString result.Typed.Value.Types.["show"]
    for tag in [ "#some(int)"; "#nothing"; "#odd" ] do
        Assert.Contains(tag, shown)
