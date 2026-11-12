module Nanyx.Compiler.Tests.Features.Linters.UnusedResults

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private code = "default/unusedResults"

/// The diagnostics of `source`, compiled with every linter on, which must have no compiler errors, or no linter would
/// run. `default/unusedResults` is itself an error, so it does not count.
let private compiled (source: string) =
    let result = Compiler.compileWith Linters.everyLinter source
    let compilerErrors = result.Diagnostics |> List.filter (fun diagnostic -> Diagnostics.isError diagnostic && diagnostic.Code <> code)
    Assert.True(result.TypeChecked && compilerErrors.IsEmpty, sprintf "%A" result.Diagnostics)
    result.Diagnostics

/// What `default/unusedResults` reports for `source`: each diagnostic with the text it is reported at. Its line
/// endings are made `\n`, as for spans.
let private reported (source: string) =
    let source = source.ReplaceLineEndings "\n"
    compiled source
    |> List.filter (fun diagnostic -> diagnostic.Code = code)
    |> List.map (fun diagnostic ->
        Assert.Equal(Severity.Error, diagnostic.Severity)
        let span = diagnostic.Span.Value
        diagnostic, source.Substring(span.Start, span.End - span.Start))

[<Fact>]
let ``A call whose result is thrown away is reported at the call`` () =
    let source =
        """import nanyx/console

def compute: [Console] int -> int = { n ->
  readLine()
  42
}
"""
    match reported source with
    | [ diagnostic, text ] ->
        Assert.Equal("readLine()", text)
        Assert.StartsWith("The result of `readLine`, of type '#some(string) | ", diagnostic.Message)
        Assert.EndsWith("', is never used: bind it to a name, or pass it to `ignore`", diagnostic.Message)
    | other -> failwith $"Expected one report, got %A{other}"

[<Fact>]
let ``A pure expression whose value is thrown away is reported`` () =
    let source =
        """def f: int -> int = { n ->
  n + 2
  n
}
"""
    match reported source with
    | [ diagnostic, text ] ->
        Assert.Equal("n + 2", text)
        Assert.Equal("This value, of type 'int', is never used: bind it to a name, or pass it to `ignore`", diagnostic.Message)
    | other -> failwith $"Expected one report, got %A{other}"

[<Fact>]
let ``The fix passes the value to ignore`` () =
    let source =
        """def twice: int -> int = { n -> n * 2 }

def f: int -> int = { n ->
  twice(n)
  n
}
"""
    let source = source.ReplaceLineEndings "\n"
    match reported source with
    | [ diagnostic, _ ] ->
        let fixedSource = applyFix source diagnostic.Fixes.Head
        Assert.Contains("  ignore(twice(n))\n", fixedSource)
        Assert.Empty(reported fixedSource)
    | other -> failwith $"Expected one report, got %A{other}"

[<Fact>]
let ``A call whose type is unit is not reported`` () =
    let source =
        """import nanyx/console

def greet: [Console] string -> int = { name ->
  println("hello")
  println(name)
  1
}
"""
    Assert.Empty(reported source)

[<Fact>]
let ``A block's last expression is not reported`` () =
    let source =
        """def f: int -> int = { n ->
  def m = n + 1
  m * 2
}
"""
    Assert.Empty(reported source)

[<Fact>]
let ``A for loop and what its body gives are not reported`` () =
    let source =
        """import nanyx/console

def f: [Console] list(int) -> int = { xs ->
  for xs | x ->
    println("{x}")
    x * 2
  0
}
"""
    Assert.Empty(reported source)

[<Fact>]
let ``A workflow's statements are not reported`` () =
    let source =
        """def parse: string -> #some(int) | #error(string) = { s -> if s == "" then #error("empty") else #some(1) }

def both: string, string -> #some(int) | #error(string) = { a, b -> proc {
  try parse(a)
  def y = try parse(b)
  return y
} }
"""
    Assert.Empty(reported source)

[<Fact>]
let ``A call of the built-in dbg on a line of its own is not reported`` () =
    let source =
        """import nanyx/console

def total: [Console] list(int) -> int = { xs ->
  dbg(xs)
  xs \dbg
  def counted = dbg(xs \sum)
  counted
}
"""
    Assert.Empty(reported source)

[<Fact>]
let ``A call of a def called dbg whose result is thrown away is reported`` () =
    let source =
        """def dbg: int -> int = { n -> n * 2 }

def f: int -> int = { n ->
  dbg(n)
  n \dbg
  n
}
"""
    match reported source with
    | [ (first, firstText); (second, secondText) ] ->
        Assert.Equal("dbg(n)", firstText)
        Assert.Equal("n \\dbg", secondText)
        Assert.Equal("The result of `dbg`, of type 'int', is never used: bind it to a name, or pass it to `ignore`", first.Message)
        Assert.Equal(first.Message, second.Message)
    | other -> failwith $"Expected two reports, got %A{other}"

[<Fact>]
let ``A call that never returns is left to unreachableCode`` () =
    let source =
        """def f: int -> int = { n ->
  panic("bad input")
  n + 1
}
"""
    let diagnostics = compiled (source.ReplaceLineEndings "\n")
    Assert.DoesNotContain(diagnostics, fun diagnostic -> diagnostic.Code = code)
    Assert.Contains(diagnostics, fun diagnostic -> diagnostic.Code = "default/unreachableCode")
