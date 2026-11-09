module Nanyx.Compiler.Tests.Features.StdLib.Prelude

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``The prelude is in scope with no imports`` () =
    let source =
        "def f: int -> Result(int, string) = { n -> if n > 5 then #error(\"big\") else #some(n) }\n" +
        "def run = { x -> proc {\n" +
        "  def a = try f(x)\n" +
        "  return a + 1\n" +
        "} }\n" +
        "def name: #some(string) | #empty = #some(\"abc\")\n" +
        "def called = Option.map(name, { s -> 1 })\n" +
        "def piped = name \\ Option.map({ s -> 1 })\n" +
        "def mapped = f(1) \\ Result.mapError({ e -> 0 })"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let typeOf name = Unifier.tyToString result.Typed.Value.Types.[name]
    Assert.Equal("#some(int) | #empty", typeOf "called")
    Assert.Equal("#some(int) | #empty", typeOf "piped")
    Assert.Equal("Result(int, int)", typeOf "mapped")

[<Fact>]
let ``A pipe finds the prelude's Result helpers by the Result alias`` () =
    let source =
        "def r: Result(int, string) = #error(\"x\")\n" +
        "def mapped = r \\ mapError({ e -> 0 })\n" +
        "def fallback = r \\ defaultValue(3)"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("Result(int, int)", Unifier.tyToString result.Typed.Value.Types.["mapped"])
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["fallback"])

[<Fact>]
let ``Int.parse is in the prelude`` () =
    let result = Compiler.compile "def n = Int.parse(\"42\")\ndef piped = \"7\" \\ Int.parse"
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("#some(int) | #invalidInt", Unifier.tyToString result.Typed.Value.Types.["n"])
    Assert.Equal("#some(int) | #invalidInt", Unifier.tyToString result.Typed.Value.Types.["piped"])

[<Fact>]
let ``Bool.parse is in the prelude`` () =
    let result = Compiler.compile "def b = Bool.parse(\"true\")\ndef piped = \"false\" \\ Bool.parse"
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("#some(bool) | #invalidBool", Unifier.tyToString result.Typed.Value.Types.["b"])
    Assert.Equal("#some(bool) | #invalidBool", Unifier.tyToString result.Typed.Value.Types.["piped"])

[<Fact>]
let ``Each built-in type has an attached toString in the prelude`` () =
    let source =
        "def i = Int.toString(1)\n" +
        "def f = 1.5 \\ toString\n" +
        "def b = true \\ toString\n" +
        "def s = String.toString(\"a\")\n" +
        "def texts = [1, 2] \\ map(Int.toString)"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let typeOf name = Unifier.tyToString result.Typed.Value.Types.[name]
    for name in [ "i"; "f"; "b"; "s" ] do
        Assert.Equal("string", typeOf name)
    Assert.Equal("list(string)", typeOf "texts")

[<Fact>]
let ``A context of toString is synthesized for a built-in type from its attached toString`` () =
    let source =
        "context ToString(a) = (toString: a -> string)\n" +
        "def label: [ToString(a)] a -> string = { x -> \"<\" ++ toString(x) ++ \">\" }\n" +
        "def text = label(42)"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal(TyPrimitive "string", result.Typed.Value.Types.["text"])

[<Fact>]
let ``The prelude's List functions are found by a pipe on a list`` () =
    let source =
        "def xs = [3, 1, 2]\n" +
        "def doubled = xs \\ map({ x -> x * 2 })\n" +
        "def names = xs \\ map({ x -> \"n\" })\n" +
        "def big = xs \\ filter({ x -> x > 1 })\n" +
        "def first = xs \\ head\n" +
        "def total = List.fold(xs, 0, { acc, x -> acc + x })\n" +
        "def pairs = List.zip(xs, names)"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let typeOf name = Unifier.tyToString result.Typed.Value.Types.[name]
    Assert.Equal("list(int)", typeOf "doubled")
    Assert.Equal("list(string)", typeOf "names")
    Assert.Equal("list(int)", typeOf "big")
    Assert.Equal("#some(int) | #emptyList", typeOf "first")
    Assert.Equal("int", typeOf "total")
    Assert.Equal("list((int, string))", typeOf "pairs")

[<Fact>]
let ``The prelude's distinct, sortBy and unfold have the docs' types`` () =
    let source =
        "def xs = [3, 1, 2]\n" +
        "def unique = xs \\ distinct\n" +
        "def sorted = xs \\ sortBy({ x -> 0 - x })\n" +
        "def counted = List.unfold(3, { n -> if n == 0 then #done else #some(\"x\", n - 1) })"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let typeOf name = Unifier.tyToString result.Typed.Value.Types.[name]
    Assert.Equal("list(int)", typeOf "unique")
    Assert.Equal("list(int)", typeOf "sorted")
    Assert.Equal("list(string)", typeOf "counted")

[<Fact>]
let ``List.sortBy needs an int key`` () =
    let result = Compiler.compile "def sorted = List.sortBy([\"b\", \"a\"], { s -> s })"
    let diagnostic = Assert.Single result.Diagnostics
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    Assert.Contains("string vs int", diagnostic.Message)

[<Fact>]
let ``A module's own definition wins over the prelude's`` () =
    let result = Compiler.compile "def proc = 5\ndef x = proc + 1"
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["x"])

[<Fact>]
let ``An import's definition wins over the prelude's without being ambiguous`` () =
    let modules = [ "mod.nyx", "module mod\nexport def proc = 5" ]
    let result = compileImportingAll modules "module main\nimport \"mod.nyx\"\ndef x = proc + 1"
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["x"])

[<Fact>]
let ``The prelude is not listed among the standard-library modules to import`` () =
    let result = Compiler.compile "import nanyx/nope"
    let message = (List.head result.Diagnostics).Message
    Assert.Contains("nanyx/text", message)
    Assert.DoesNotContain("nanyx/prelude", message)
