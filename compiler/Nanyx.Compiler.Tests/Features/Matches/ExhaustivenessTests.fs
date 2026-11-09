module Nanyx.Compiler.Tests.Features.Matches

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``Match rejects non-exhaustive tag unions`` () =
    let source =
        "def value: #some(int) | #none = #none\n" +
        "def result = match value\n" +
        "| #some(v) -> v\n"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Non-exhaustive match", message)

[<Fact>]
let ``Match checks exhaustiveness when the scrutinee is a call result or a local bound to one`` () =
    // #61: the scrutinee's type was still an unsolved variable when the check ran, so it passed silently.
    let direct =
        Compiler.compile (
            "def getMessage: () -> #some(string) | #noMessage = { #some(\"hi\") }\n" +
            "def partial = {\n" +
            "  match getMessage()\n" +
            "    | #some(_) -> \"message\"\n" +
            "}\n")
    Assert.False(direct.Diagnostics.IsEmpty, "a match on an annotated call result must be checked")
    Assert.Contains("missing tags: noMessage", (direct.Diagnostics |> List.head).Message)
    let viaLocal =
        Compiler.compile (
            "def getMessage: () -> #some(string) | #noMessage = { #some(\"hi\") }\n" +
            "def partial = {\n" +
            "  def m = getMessage()\n" +
            "  match m\n" +
            "    | #some(_) -> \"message\"\n" +
            "}\n")
    Assert.False(viaLocal.Diagnostics.IsEmpty, "a match on a local bound to a call result must be checked")
    let complete =
        Compiler.compile (
            "def getMessage: () -> #some(string) | #noMessage = { #some(\"hi\") }\n" +
            "def total = {\n" +
            "  match getMessage()\n" +
            "    | #some(_) -> \"message\"\n" +
            "    | #noMessage -> \"none\"\n" +
            "}\n")
    Assert.True(complete.Diagnostics.IsEmpty, sprintf "%A" complete.Diagnostics)

[<Fact>]
let ``Match accepts exhaustive tag unions`` () =
    let source =
        "def value: #some(int) | #none = #none\n" +
        "def result = match value\n" +
        "| #some(v) -> v\n" +
        "| #none -> 0\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Match rejects non-exhaustive list patterns`` () =
    let source =
        "def values = [1, 2]\n" +
        "def result = match values\n" +
        "| [x, y] -> x\n"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Non-exhaustive match", message)

[<Fact>]
let ``Match accepts list catch-all`` () =
    let source =
        "def values = [1, 2]\n" +
        "def result = match values\n" +
        "| [x, y] -> x\n" +
        "| _ -> 0\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Match accepts exhaustive list patterns`` () =
    let source =
        "def values = [1, 2]\n" +
        "def result = match values\n" +
        "| [] -> 0\n" +
        "| [head, ...tail] -> head\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Match accepts an unnamed rest as covering every longer list`` () =
    // `[x, ...]` matches any list of one or more, like `[x, ...rest]`; it is not `[x]`.
    let source =
        "def values = [1, 2]\n" +
        "def result = match values\n" +
        "| [x, ...] -> x\n" +
        "| [] -> 0\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``Match rejects non-exhaustive record patterns`` () =
    let source =
        "def point = (x = 1, y = 2)\n" +
        "def result = match point\n" +
        "| (x = 1) -> 1\n"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Non-exhaustive match", message)

[<Fact>]
let ``Match accepts record catch-all`` () =
    let source =
        "def point = (x = 1, y = 2)\n" +
        "def result = match point\n" +
        "| (x = 1) -> 1\n" +
        "| _ -> 0\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Match rejects incompatible record patterns`` () =
    let source =
        "def point = (x = 1)\n" +
        "def result = match point\n" +
        "| (x = 1) -> 1\n" +
        "| \"oops\" -> 0\n" +
        "| _ -> 0\n"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Type mismatch", message)

[<Fact>]
let ``Match rejects non-exhaustive tag unions with multiple scrutinees`` () =
    let source =
        "def a: #left | #right = #left\n" +
        "def b: #up | #down = #up\n" +
        "def result = match a, b\n" +
        "| #left, #up -> 1\n" +
        "| #right, #up -> 2\n"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Non-exhaustive match", message)

[<Fact>]
let ``Typecheck matches on an inferred tag union exhaustively`` () =
    // Exhaustiveness is checked against the scrutinee's known type; an inferred union counts as known.
    // (Scrutinees that are call results or local bindings are not checked today, inferred or annotated.)
    let source =
        "def message = if false then #some(\"hi\") else #noMessage\n" +
        "def describe = {\n" +
        "  match message\n" +
        "    | #some(_) -> \"message\"\n" +
        "    | #noMessage -> \"nothing\"\n" +
        "}\n" +
        "def partial = {\n" +
        "  match message\n" +
        "    | #some(_) -> \"message\"\n" +
        "}"
    let result = Compiler.compile source
    Assert.Equal(1, result.Diagnostics.Length)
    Assert.Contains("Non-exhaustive match", (List.head result.Diagnostics).Message)
    Assert.Contains("noMessage", (List.head result.Diagnostics).Message)

[<Fact>]
let ``A def takes a nominal value apart with its type's pattern`` () =
    let source =
        "type $Money = private int\n" +
        "def toPence: $Money -> int = { m ->\n" +
        "  def $Money(pence) = m\n" +
        "  pence\n" +
        "}\n" +
        "def price = $Money(250)\n" +
        "def $Money(top) = price\n" +
        "def shown = match price\n" +
        "  | $Money(0) -> \"free\"\n" +
        "  | $Money(p) -> \"{p}\"\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["top"])
    Assert.Equal(TyPrimitive "string", typed.Types.["shown"])

[<Fact>]
let ``A def with a pattern that is not total is a non-exhaustive match`` () =
    // The parser accepts any pattern on the left of a def; the type checker decides whether it is total.
    let onlyMessage (source: string) =
        match (Compiler.compile source).Diagnostics with
        | [ diagnostic ] -> diagnostic.Message
        | other -> failwith $"Expected one diagnostic, got %A{other}"
    let tag = onlyMessage ("def value = if true then #some(1) else #none\n" + "def #some(n) = value\n")
    Assert.Contains("Non-exhaustive match", tag)
    Assert.Contains("none", tag)
    let list = onlyMessage ("def numbers = [1, 2, 3]\n" + "def [first, ...rest] = numbers\n")
    Assert.Contains("Non-exhaustive match", list)
    Assert.Contains("list cases", list)

[<Fact>]
let ``A match on an alias of a tag union is checked for exhaustiveness`` () =
    let source =
        "type Lookup = #found(int) | #missing\n" +
        "def describe: Lookup -> int = { l -> match l\n" +
        "  | #found(n) -> n }\n"
    let result = Compiler.compile source
    Assert.Contains(result.Diagnostics, fun diag -> diag.Message.Contains "missing tags: missing")

/// The text `span` covers in `source`.
let private textOf (source: string) (span: SourceSpan) = source.Substring(span.Start, span.End - span.Start)

[<Fact>]
let ``A lone else in a match on several values matches whatever they all are`` () =
    let source =
        "def describe: int, int -> string = { x, y ->\n" +
        "  match x, y\n" +
        "    | 0, 0 -> \"origin\"\n" +
        "    | 0, _ -> \"on the y axis\"\n" +
        "    | else -> \"elsewhere\"\n" +
        "}\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``An arm with a pattern count other than the values' is an error at the arm`` () =
    // It used to throw from inside type checking instead.
    let tooFew = "def f: int, int -> int = { x, y ->\n  match x, y\n    | 0, 0 -> 1\n    | _ -> 2\n}\n"
    let diagnostic = Assert.Single((Compiler.compile tooFew).Diagnostics)
    Assert.Equal(DiagnosticCodes.WrongPatternCount.Code, diagnostic.Code)
    Assert.Equal(
        "This arm has 1 pattern, but the match is on 2 values: an arm needs a pattern for each value, or a lone `else` for anything else",
        diagnostic.Message)
    Assert.Equal("_", textOf tooFew diagnostic.Span.Value)
    let tooMany = "def f: int, int -> int = { x, y ->\n  match x, y\n    | 1, 2, 3 -> 1\n    | else -> 2\n}\n"
    let diagnostic = Assert.Single((Compiler.compile tooMany).Diagnostics)
    Assert.Equal(DiagnosticCodes.WrongPatternCount.Code, diagnostic.Code)
    Assert.StartsWith("This arm has 3 patterns, but the match is on 2 values", diagnostic.Message)
    Assert.Equal("1, 2, 3", textOf tooMany diagnostic.Span.Value)

[<Fact>]
let ``The parser's multi-value match examples compile without throwing`` () =
    // They are snippets of syntax that use names they never define, so unknown names are all there is to report.
    let examples = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "Nanyx.Parser.Tests", "Features", "Matches")
    let files =
        Array.append (Directory.GetFiles(examples, "test_match_multi_*.nyx")) [| Path.Combine(examples, "test_match_phase6_all.nyx") |]
    Assert.True(files.Length > 10)
    for path in files do
        let result = Compiler.compileFile path
        for diagnostic in result.Diagnostics do
            Assert.True(diagnostic.Code = DiagnosticCodes.UnknownIdentifier.Code, $"{Path.GetFileName path}: %A{diagnostic}")
