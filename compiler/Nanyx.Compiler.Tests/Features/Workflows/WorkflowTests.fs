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
        // A failure only one branch adds is in an annotation too.
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
        reported "  def halves = list.map(xs, { x -> try halve(x) })")
    Assert.Equal(
        "`try` is in a part of an expression that may not run, so the rest of the `proc` block cannot follow it: make the `if` or `match` it is in a statement of its own",
        reported "  def m = 1 + (if true then try halve(2) else 0)")
    // A builder block of its own inside a lambda has its own continuation.
    let nested =
        "def halve: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }\n" +
        "def f = { xs -> proc {\n" +
        "  def results = list.map(xs, { x -> proc {\n" +
        "    def h = try halve(x)\n" +
        "    return h\n" +
        "  } })\n" +
        "  return results\n" +
        "} }"
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
        "def result = async {\n" +
        "  def x = await! foo()\n" +
        "  return! x.y\n" +
        "}"
    match parseModule source with
    | Result.Error err -> Assert.True(false, $"Parse error: {err}")
    | Result.Ok ast ->
        let desugared = WorkflowDesugaring.desugar ast
        // `async { ... }` is its block, each keyword calling the builder's member through a local of its own.
        match desugared with
        | [Kind (Def (ValueDef(_, "result", _, Kind (Block [Kind (ExprStatement expr)]))))] ->
            match expr.Kind with
            | Block [
                Kind (DefStatement(_, awaitLocal, None, Kind (MemberAccess(Kind (IdentifierExpr "async"), "await", _))))
                Kind (ExprStatement (Kind (FunctionCall(awaitCall, _, [ Kind (FunctionCall("foo", _, [])); Kind (Lambda([("x", None)], continuation, _)) ]))))
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
        let desugared = WorkflowDesugaring.desugar ast
        match desugared with
        | [Kind (Def (ValueDef(_, "result", _, Kind (FunctionCall("async", _, [Kind (Lambda([], Kind (Block [Kind (ExprStatement (Kind (LiteralExpr (IntLit64 1L))))]), _))])))))] ->
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
    let byHand (branches: string) =
        $"""def f = {{ n ->
  def t1 = proc.try
  def t2 = proc.try
  def r1 = proc.return
  def r2 = proc.return
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
        [ inferred (byHand "  if n < 0 then t1(#negative, { v -> r1(v) }) else t2(halve(n), { v -> r2(v) })")
          inferred (byHand "  match n\n    | 0 -> t1(#negative, { v -> r1(v) })\n    | _ -> t2(halve(n), { v -> r2(v) })")
          inferred withKeywords ])

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
            | FunctionCall(_, _, [ Kind (FunctionCall("f", _, [ argument ])); Kind (Lambda(_, rest, _)) ]) ->
                argument :: boundCalls rest
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
        "def f: int -> #some(int) | #error(#tooBig) = { n -> if n > 5 then #error(#tooBig) else #some(n) }\n" +
        "def run = { x -> proc {\n" +
        "  def a = try f(x)\n" +
        "  a + 1\n" +
        "} }\n" +
        "def check = { x -> proc {\n" +
        "  if x < 0 then try #error(#tooBig)\n" +
        "} }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("int -> #some() | #error(#tooBig)", Unifier.tyToString result.Typed.Value.Types.["run"])
    Assert.Equal("int -> #some() | #error(#tooBig)", Unifier.tyToString result.Typed.Value.Types.["check"])

[<Fact>]
let ``A builder block that can end without return needs the builder's zero`` () =
    let builder =
        "def opt = (\n" +
        "  try = {\n" +
        "    | #some(value), continue -> continue(value)\n" +
        "    | failure, _ -> failure\n" +
        "  }\n" +
        "  return = { value -> #some(value) }\n" +
        ")\n" +
        "def half: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }\n"
    let fallsOff = builder + "def show = { n -> opt {\n  def h = try half(n)\n  h + 1\n} }\n"
    let result = Compiler.compile fallsOff
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.InvalidWorkflow.Code, diagnostic.Code)
    Assert.Equal("h + 1", fallsOff.Substring(diagnostic.Span.Value.Start, diagnostic.Span.Value.End - diagnostic.Span.Value.Start))
    Assert.Contains("`opt` has no `zero`", diagnostic.Message)
    // A block that always ends with `return` never needs it.
    Assert.Empty((Compiler.compile (builder + "def show = { n -> opt {\n  def h = try half(n)\n  return h + 1\n} }\n")).Diagnostics)

[<Fact>]
let ``A builder's zero is the value of a block that ends without return`` () =
    let source =
        "def opt\n" +
        "  : (\n" +
        "    try: ((#some(a) | r), (a -> #some(b) | r)) -> #some(b) | r\n" +
        "    return: a -> #some(a) | r\n" +
        "    zero: #nothing | r\n" +
        "  )\n" +
        "  = (\n" +
        "  try = {\n" +
        "    | #some(value), continue -> continue(value)\n" +
        "    | failure, _ -> failure\n" +
        "  }\n" +
        "  return = { value -> #some(value) }\n" +
        "  zero = #nothing\n" +
        ")\n" +
        "def half: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }\n" +
        "def show = { n -> opt {\n  def h = try half(n)\n  h + 1\n} }\n"
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
        "def nothing: () -> #nothing | r = { #nothing }\n" +
        "def h = { c -> if c then #some(1) else nothing() }\n" +
        "def m = { n -> match n\n  | 0 -> #some(\"zero\")\n  | _ -> nothing()\n}\n" +
        "def k = { c ->\n  def __rest_1 = { _ -> nothing() }\n  if c then #some(1) else __rest_1(())\n}"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("bool -> #some(int) | #nothing", Unifier.tyToString result.Typed.Value.Types.["h"])
    Assert.Equal("int -> #some(string) | #nothing", Unifier.tyToString result.Typed.Value.Types.["m"])
    Assert.Equal("bool -> #some(int) | #nothing", Unifier.tyToString result.Typed.Value.Types.["k"])

[<Fact>]
let ``A builder's zero is part of the block's type when a branch returns a value`` () =
    let source =
        "def opt\n" +
        "  : (\n" +
        "    try: ((#some(a) | r), (a -> #some(b) | r)) -> #some(b) | r\n" +
        "    return: a -> #some(a) | r\n" +
        "    zero: #nothing | r\n" +
        "  )\n" +
        "  = (\n" +
        "  try = {\n" +
        "    | #some(value), continue -> continue(value)\n" +
        "    | failure, _ -> failure\n" +
        "  }\n" +
        "  return = { value -> #some(value) }\n" +
        "  zero = #nothing\n" +
        ")\n" +
        "def half: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }\n" +
        "def show = { n -> opt {\n  def h = try half(n)\n  if h > 2 then return h\n} }\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let shown = Unifier.tyToString result.Typed.Value.Types.["show"]
    for tag in [ "#some(int)"; "#nothing"; "#odd" ] do
        Assert.Contains(tag, shown)
