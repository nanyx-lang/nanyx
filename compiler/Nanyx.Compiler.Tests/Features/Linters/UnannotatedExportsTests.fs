module Nanyx.Compiler.Tests.Features.Linters.UnannotatedExports

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private code = "default/unannotatedExports"

/// The diagnostics of `source`, compiled with every linter on. The module must type-check, or no linter would run.
let private compiled (source: string) =
    let result = Compiler.compileWith Linters.everyLinter source
    Assert.True(result.TypeChecked && not (result.Diagnostics |> List.exists Diagnostics.isError), sprintf "%A" result.Diagnostics)
    result.Diagnostics

/// What `default/unannotatedExports` reports for `source`: each diagnostic with the text it is reported at. Its line
/// endings are made `\n`, as for spans.
let private reported (source: string) =
    let source = source.ReplaceLineEndings "\n"
    compiled source
    |> List.filter (fun diagnostic -> diagnostic.Code = code)
    |> List.map (fun diagnostic ->
        Assert.Equal(Severity.Warning, diagnostic.Severity)
        let span = diagnostic.Span.Value
        diagnostic, source.Substring(span.Start, span.End - span.Start))

/// `source` with the fix of its one report applied, checked to type-check with nothing left to report.
let private fixedOnce (source: string) =
    let source = source.ReplaceLineEndings "\n"
    match reported source with
    | [ diagnostic, _ ] ->
        let fixedSource = applyFix source diagnostic.Fixes.Head
        Assert.Empty(compiled fixedSource |> List.filter (fun diagnostic -> diagnostic.Code = code))
        fixedSource
    | other -> failwith $"Expected one report, got %A{other}"

[<Fact>]
let ``An exported def without a type is reported at its name`` () =
    match reported "export def add = { a: int, b: int -> a + b }\n" with
    | [ diagnostic, text ] ->
        Assert.Equal("add", text)
        Assert.Equal("`add` is exported but does not write its type: int, int -> int", diagnostic.Message)
    | other -> failwith $"Expected one report, got %A{other}"

[<Fact>]
let ``The fix writes a plain function's type`` () =
    Assert.Equal(
        "export def add: int, int -> int = { a: int, b: int -> a + b }\n",
        fixedOnce "export def add = { a: int, b: int -> a + b }\n")

[<Fact>]
let ``The fix writes a value's type, with the lowercase names of built-in types`` () =
    let source =
        """export def greeting = "hello"
"""
    Assert.Equal(
        """export def greeting: string = "hello"
""".ReplaceLineEndings "\n",
        fixedOnce source)

[<Fact>]
let ``The fix writes a generic function's type variables`` () =
    Assert.Equal("export def first: a, b -> a = { x, _ -> x }\n", fixedOnce "export def first = { x, _ -> x }\n")

[<Fact>]
let ``The fix writes the contexts a function requires, joined with &`` () =
    Assert.Equal(
        "export def same: [Add(a) & Eq(a)] a, a -> bool = { x, y -> x + y == y }\n",
        fixedOnce "export def same = { x, y -> x + y == y }\n")

[<Fact>]
let ``The fix writes a context variable`` () =
    Assert.Equal(
        "export def apply: [c] ([c] a -> b), a -> b = { f, x -> f(x) }\n",
        fixedOnce "export def apply = { f, x -> f(x) }\n")

[<Fact>]
let ``A def a module block exports is reported`` () =
    let source =
        """module shapes =
  export def area = { w: int, h: int -> w * h }

def total = shapes.area(2, 3)
"""
    Assert.Equal<string list>([ "area" ], reported source |> List.map snd)

[<Fact>]
let ``Annotated, private and destructuring defs are not reported`` () =
    let source =
        """import nanyx/console

export def add: int, int -> int = { a, b -> a + b }
export def shown: [Console] = { println("hi") }
def helper = { x: int -> x }
export def (low, high) = (1, 2)
def used = helper(low + high)
"""
    Assert.Empty(reported source)
