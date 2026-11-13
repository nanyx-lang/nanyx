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
    assertReports DiagnosticCodes.NonExhaustiveMatch result
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
let ``A guard pattern covers nothing for certain, whatever its predicate`` () =
    // #428: the check cannot tell that `{ % 2 == 0 }` and `{ % 2 != 0 }` between them cover every int.
    let guardsOnly =
        Compiler.compile (
            "def parity: int -> string = { n ->\n" +
            "  match n\n" +
            "    | { % 2 == 0 } -> \"even\"\n" +
            "    | { x -> x % 2 != 0 } -> \"odd\"\n" +
            "}\n")
    Assert.Contains("Non-exhaustive match", (Assert.Single guardsOnly.Diagnostics).Message)
    let nested =
        Compiler.compile (
            "def f: #some(string) | #none -> int = { o ->\n" +
            "  match o\n" +
            "    | #some({ .length > 5 }) -> 1\n" +
            "    | #none -> 0\n" +
            "}\n")
    Assert.Contains("Non-exhaustive match", (Assert.Single nested.Diagnostics).Message)
    let withCatchAll =
        Compiler.compile (
            "def f: #some(string) | #none -> int = { o ->\n" +
            "  match o\n" +
            "    | #some({ .length > 5 }) -> 2\n" +
            "    | #some(_) -> 1\n" +
            "    | #none -> 0\n" +
            "}\n")
    Assert.True(withCatchAll.Diagnostics.IsEmpty, sprintf "%A" withCatchAll.Diagnostics)

[<Fact>]
let ``A guard pattern's predicate is checked as a function from the value matched to bool`` () =
    let notBool = Compiler.compile "def f: int -> int = { n ->\n  match n\n    | { + 1 } -> 1\n    | _ -> 0\n}\n"
    Assert.False(notBool.Diagnostics.IsEmpty, "a predicate must return a bool")
    let wrongType = Compiler.compile "def f: int -> int = { n ->\n  match n\n    | { .length > 5 } -> 1\n    | _ -> 0\n}\n"
    Assert.False(wrongType.Diagnostics.IsEmpty, "an int has no length")
    let twoParametersSource = "def f: int -> int = { n ->\n  match n\n    | { a, b -> a > b } -> 1\n    | _ -> 0\n}\n"
    let diagnostic = Assert.Single (Compiler.compile twoParametersSource).Diagnostics
    Assert.Equal(DiagnosticCodes.GuardNotAPredicate.Code, diagnostic.Code)
    Assert.Equal(twoParametersSource.IndexOf "{ a, b", diagnostic.Span.Value.Start)
    // The predicate sees what is in scope at the match, not what its own arm binds: those are bound only once the
    // arm has matched.
    let armName = Compiler.compile "def f: (int, int) -> int = { p ->\n  match p\n    | (a, { > a }) -> 1\n    | _ -> 0\n}\n"
    Assert.False(armName.Diagnostics.IsEmpty, "`a` is not in scope in the predicate")
    let ok = Compiler.compile "def f: int, string -> int = { n, s ->\n  match n\n    | { > s.length } -> 1\n    | _ -> 0\n}\n"
    Assert.True(ok.Diagnostics.IsEmpty, sprintf "%A" ok.Diagnostics)

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
    assertReports DiagnosticCodes.NonExhaustiveMatch result
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
    assertReports DiagnosticCodes.NonExhaustiveMatch result
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Non-exhaustive match", message)

[<Fact>]
let ``Match accepts record catch-all`` () =
    let source =
        "def point = (x = 1, y = 2)\n" +
        "def result = match point\n" +
        "| (x = 1, ...) -> 1\n" +
        "| _ -> 0\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)

[<Fact>]
let ``Match rejects incompatible record patterns`` () =
    let source =
        "def point = (x = 1)\n" +
        "def result = match point\n" +
        "| (x = 1) -> 1\n" +
        "| \"oops\" -> 0\n" +
        "| _ -> 0\n"
    let result = Compiler.compile source
    assertReports DiagnosticCodes.TypeMismatch result
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
    assertReports DiagnosticCodes.NonExhaustiveMatch result
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Non-exhaustive match", message)

[<Fact>]
let ``Typecheck matches on an inferred tag union exhaustively`` () =
    // Exhaustiveness is checked against the scrutinee's known type; an inferred union counts as known.
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
    let tag = (diagnosticOf ("def value = if true then #some(1) else #none\n" + "def #some(n) = value\n")).Message
    Assert.Contains("Non-exhaustive match", tag)
    Assert.Contains("none", tag)
    let list = (diagnosticOf ("def numbers = [1, 2, 3]\n" + "def [first, ...rest] = numbers\n")).Message
    Assert.Contains("Non-exhaustive match", list)
    Assert.Contains("list cases", list)

[<Fact>]
let ``A lambda's pattern parameter must be total, as a destructuring def's pattern must`` () =
    let total =
        Compiler.compile (
            "type $Email = string\n" +
            "def value: $Email -> string = { $Email(s) -> s }\n" +
            "def add = { (a, b), c -> a + b + c + 1 }\n" +
            "def ignore = { (), n -> n }\n")
    Assert.True(total.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{total.Diagnostics}")
    Assert.Equal("(int, int), int -> int", Unifier.tyToString total.Typed.Value.Types.["add"])
    // One that is not is reported where the pattern is written; `{ | ... }` is the lambda for several cases.
    let source = "def first = { [x, ...rest] -> x }\n"
    let diagnostic = diagnosticOf source
    Assert.Contains("Non-exhaustive match", diagnostic.Message)
    Assert.Contains("list cases", diagnostic.Message)
    Assert.Equal(Some (offsetOf source "[x" 0 0), diagnostic.Span |> Option.map (fun span -> span.Start))
    let tag = Compiler.compile "def unwrap: #some(int) | #none -> int = { #some(n) -> n }\n"
    Assert.Contains(tag.Diagnostics, fun diag -> diag.Message.Contains "Non-exhaustive match" && diag.Message.Contains "none")

[<Fact>]
let ``A match on an alias of a tag union is checked for exhaustiveness`` () =
    let source =
        "type Lookup = #found(int) | #missing\n" +
        "def describe: Lookup -> int = { l -> match l\n" +
        "  | #found(n) -> n }\n"
    let result = Compiler.compile source
    Assert.Contains(result.Diagnostics, fun diag -> diag.Message.Contains "missing tags: missing")

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
    Assert.Equal("_", textAt tooFew diagnostic.Span.Value)
    let tooMany = "def f: int, int -> int = { x, y ->\n  match x, y\n    | 1, 2, 3 -> 1\n    | else -> 2\n}\n"
    let diagnostic = Assert.Single((Compiler.compile tooMany).Diagnostics)
    Assert.Equal(DiagnosticCodes.WrongPatternCount.Code, diagnostic.Code)
    Assert.StartsWith("This arm has 3 patterns, but the match is on 2 values", diagnostic.Message)
    Assert.Equal("1, 2, 3", textAt tooMany diagnostic.Span.Value)

[<Fact>]
let ``Brackets around a tuple are optional in an arm, either way round`` () =
    // A match on a tuple takes `| x, y`, and a match on two values takes `| (x, y)`, and each is checked as the other.
    let onTuple = "def f: (int, int) -> int = { pair ->\n  match pair\n    | 0, y -> y\n    | x, _ -> x\n}\n"
    let result = Compiler.compile onTuple
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let onValues = "def f: int, int -> int = { a, b ->\n  match a, b\n    | (0, y) -> y\n    | (x, _) -> x\n}\n"
    let result = Compiler.compile onValues
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    // Exhaustiveness sees through the brackets too.
    let missing = "def f: int, int -> int = { a, b ->\n  match a, b\n    | (0, y) -> y\n}\n"
    let diagnostic = Assert.Single((Compiler.compile missing).Diagnostics)
    Assert.Contains(diagnostic.Code, [ DiagnosticCodes.NonExhaustiveMatch.Code; DiagnosticCodes.MissingCatchAllArm.Code ])
    // A tuple of the wrong size is still the wrong number of patterns.
    let wrongSize = "def f: int, int -> int = { a, b ->\n  match a, b\n    | (x, y, z) -> x\n    | else -> 0\n}\n"
    let diagnostic = Assert.Single((Compiler.compile wrongSize).Diagnostics)
    Assert.Equal(DiagnosticCodes.WrongPatternCount.Code, diagnostic.Code)

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

[<Fact>]
let ``A match on ints without a catch-all is non-exhaustive, and says which int is missing`` () =
    let single = diagnosticOf "def f: int -> string = { n ->\n  match n\n    | 0 -> \"zero\"\n    | 1 -> \"one\"\n}\n"
    Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, single.Code)
    Assert.Equal("Non-exhaustive match, missing int values, such as 2", single.Message)
    // Ranges count for the ints they hold.
    let ranges = diagnosticOf "def f: int -> string = { n ->\n  match n\n    | 0..9 -> \"digit\"\n    | 10..99 -> \"two digits\"\n}\n"
    Assert.Equal("Non-exhaustive match, missing int values, such as 100", ranges.Message)
    // `(1, 1)` reaches no arm, though each value on its own has a catch-all somewhere.
    let source = "def f: int, int -> string = { x, y ->\n  match x, y\n    | 0, 0 -> \"origin\"\n    | 0, _ -> \"on the y axis\"\n}\n"
    let pair = diagnosticOf source
    Assert.Equal("Non-exhaustive match (scrutinee 1), missing int values, such as 1", pair.Message)
    Assert.Equal("match x, y", textAt source pair.Span.Value)
    let strings = diagnosticOf "def f: string -> int = { s ->\n  match s\n    | \"a\" -> 1\n}\n"
    Assert.Equal("Non-exhaustive match, missing string values, such as \"\"", strings.Message)
    let booleans = diagnosticOf "def f: bool -> int = { b ->\n  match b\n    | true -> 1\n}\n"
    Assert.Equal("Non-exhaustive match, missing value: false", booleans.Message)
    // A guard covers nothing, whatever it says.
    let guarded = diagnosticOf "def f: int -> int = { n ->\n  match n\n    | { > 0 } -> 1\n    | { <= 0 } -> 0\n}\n"
    Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, guarded.Code)

[<Fact>]
let ``A match on several values must cover every combination of them, not each value on its own`` () =
    let source =
        "def f: (#some(int) | #none), bool -> int = { o, b ->\n" +
        "  match o, b\n" +
        "    | #some(_), true -> 1\n" +
        "    | #none, false -> 2\n" +
        "}\n"
    let diagnostic = diagnosticOf source
    Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, diagnostic.Code)
    // `(#none, true)` is missing too; the check names one case.
    Assert.Equal("Non-exhaustive match, missing case: #none, true", diagnostic.Message)
    Assert.Equal("match o, b", textAt source diagnostic.Span.Value)
    let tags =
        diagnosticOf (
            "def f: (#left | #right), (#up | #down) -> int = { a, b ->\n" +
            "  match a, b\n" +
            "    | #left, #up -> 1\n" +
            "    | #right, _ -> 2\n" +
            "}\n")
    Assert.Equal("Non-exhaustive match, missing case: #left, #down", tags.Message)
    // Inside a tag's payload too.
    let nested = diagnosticOf "def f: #some(bool) | #none -> int = { o ->\n  match o\n    | #some(true) -> 1\n    | #none -> 0\n}\n"
    Assert.Equal("Non-exhaustive match, missing case: #some(false)", nested.Message)
    let tuple = diagnosticOf "def f: (int, bool) -> int = { p ->\n  match p\n    | (_, true) -> 1\n    | (0, false) -> 0\n}\n"
    Assert.Equal("Non-exhaustive match, missing case: (1, false)", tuple.Message)

/// The start and end of the one diagnostic compiling `source` gives, and the text between them.
let private onlySpan (source: string) =
    let diagnostic = diagnosticOf source
    Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, diagnostic.Code)
    let span = diagnostic.Span.Value
    span.Start, span.End, textAt source span

[<Fact>]
let ``A non-exhaustive match is reported from the match keyword to the end of its scrutinee, not at its arms`` () =
    let prefix = "def a: #some(string) | #notFound = #notFound\n"
    let source = prefix + "def b = match a\n  | #some(s) -> s\n"
    let start = prefix.Length + "def b = ".Length
    Assert.Equal((start, start + "match a".Length, "match a"), onlySpan source)

[<Fact>]
let ``A non-exhaustive match on a pipeline is reported over the whole pipeline`` () =
    let prefix =
        "def mapSome\n" +
        "  : (#some(int) | #notFound), (int -> string) -> (#some(string) | #notFound)\n" +
        "  = { | #some(a), f -> #some(f(a))\n" +
        "      | #notFound, _ -> #notFound }\n" +
        "def getData: () -> #some(int) | #notFound = { #notFound }\n" +
        "def b = "
    let head = "match getData() \\mapSome { n: int -> \"{n}\" }"
    let source = prefix + head + "\n  | #some(s) -> s\n"
    Assert.Equal((prefix.Length, prefix.Length + head.Length, head), onlySpan source)

[<Fact>]
let ``A non-exhaustive lambda that matches its arguments is reported at its opening brace`` () =
    let prefix = "def f: (#some(int) | #none) -> int = "
    let source = prefix + "{ | #some(n) -> n }\n"
    Assert.Equal((prefix.Length, prefix.Length + 1, "{"), onlySpan source)
    // A lambda that takes its parameters apart with patterns is reported at the patterns.
    let prefix = "def g: int, (#some(int) | #none), (#some(int) | #none) -> int = { a, "
    let source = prefix + "#some(n), #some(m) -> a + n + m }\n"
    Assert.Equal((prefix.Length, prefix.Length + "#some(n), #some(m)".Length, "#some(n), #some(m)"), onlySpan source)
    // A `for` loop's arms are reported at the first `|`.
    let prefix = "def h: list(#some(int) | #none) -> () = { xs -> for xs "
    let source = prefix + "| #some(_) -> () }\n"
    Assert.Equal((prefix.Length, prefix.Length + 1, "|"), onlySpan source)

[<Fact>]
let ``Matches that cover every value pass, however the arms split them`` () =
    let sources =
        [ // Every combination of two booleans, and of a tag and a boolean.
          "def f: bool, bool -> int = { a, b ->\n  match a, b\n    | true, true -> 1\n    | true, false -> 2\n    | false, _ -> 3\n}\n"
          "def f: (#some(int) | #none), bool -> int = { o, b ->\n  match o, b\n    | #some(_), true -> 1\n    | #some(_), false -> 2\n    | #none, _ -> 3\n}\n"
          // Ints with a catch-all, at the end of the arms or in another arm's other position.
          "def f: int, int -> int = { x, y ->\n  match x, y\n    | 0, 0 -> 0\n    | 0, _ -> 1\n    | _, _ -> 2\n}\n"
          "def f: int -> int = { n ->\n  match n\n    | 0 -> 0\n    | 1..5 -> 1\n    | _ -> 2\n}\n"
          // Payloads and list elements.
          "def f: #some(bool) | #none -> int = { o ->\n  match o\n    | #some(true) -> 1\n    | #some(false) -> 2\n    | #none -> 0\n}\n"
          "def f: list(bool) -> int = { xs ->\n  match xs\n    | [] -> 0\n    | [true, ..._rest] -> 1\n    | [false, ..._rest] -> 2\n}\n"
          // A record that is covered field by field.
          "def f: (x: bool, y: int) -> int = { p ->\n  match p\n    | (x = true, ...) -> 1\n    | (x = false, ...) -> 0\n}\n" ]
    for source in sources do
        let result = Compiler.compile source
        Assert.True(result.Diagnostics.IsEmpty, $"{source}\n%A{result.Diagnostics}")

[<Fact>]
let ``A match on a call's result that leaves out one of its tags reports only that tag`` () =
    // #681: the arms' closed union narrowed the call's result, and the call's argument was reported as well.
    for head in [ """match find(n) \bindSome(parseDigit)"""; """match bindSome(find(n), parseDigit)""" ] do
        let source =
            $$"""
def parseDigit: string -> #some(int) | #invalidInt = {
  | "1" -> #some(1)
  | _ -> #invalidInt
}
def find: int -> #some(string) | #notFound = {
  | 1 -> #some("1")
  | _ -> #notFound
}
def describe: int -> string = { n ->
  {{head}}
    | #some(n) -> "some {n}"
    | #notFound -> "not found"
}
"""
        let source = source.ReplaceLineEndings "\n"
        let diagnostic = diagnosticOf source
        Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, diagnostic.Code)
        Assert.Equal("Non-exhaustive match, missing tags: invalidInt", diagnostic.Message)
        Assert.Equal(head, textAt source diagnostic.Span.Value)
    let direct =
        diagnosticOf """
def get: () -> #some(int) | #none | #error = { #none }
def f = match get()
  | #some(n) -> n
"""
    Assert.Equal("Non-exhaustive match, missing tags: error, none", direct.Message)
