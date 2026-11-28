module Nanyx.Compiler.Tests.Features.Linters.UselessExpressions

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private code = "default/uselessExpressions"

/// The diagnostics of `source`, compiled with every linter on, which must have no compiler errors, or no linter would
/// run. The linters' own errors do not count.
let private compiled (source: string) =
    let result = Compiler.compileWith (everyLinter ()) source
    let compilerErrors = result.Diagnostics |> List.filter (fun diagnostic -> Diagnostics.isError diagnostic && not (diagnostic.Code.StartsWith "default/"))
    Assert.True(result.TypeChecked && compilerErrors.IsEmpty, sprintf "%A" result.Diagnostics)
    result.Diagnostics

/// What `default/uselessExpressions` reports for `source`: each diagnostic with the text it is reported at. Its line
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
let ``A pure expression whose value is thrown away is reported, and not as an unused result`` () =
    let source =
        """def f: int -> int = { n ->
  n + 2
  n
}
"""
    match reported source with
    | [ diagnostic, text ] ->
        Assert.Equal("n + 2", text)
        Assert.Equal("This value, of type 'int', does nothing and is never used: remove it, or use its value", diagnostic.Message)
    | other -> failwith $"Expected one report, got %A{other}"
    let diagnostics = compiled (source.ReplaceLineEndings "\n")
    Assert.DoesNotContain(diagnostics, fun diagnostic -> diagnostic.Code = "default/unusedResults")

[<Fact>]
let ``A comparison on a line of its own is reported`` () =
    let source =
        """def f: int, int -> bool = { x, y ->
  x == y
  x > y
}
"""
    Assert.Equal<string list>([ "x == y" ], reported source |> List.map snd)

[<Fact>]
let ``A call of a function that requires no context is reported by its name`` () =
    let source =
        """def twice: int -> int = { n -> n * 2 }

def f: int -> int = { n ->
  twice(n)
  n \twice
  n
}
"""
    let messages = reported source |> List.map (fun (diagnostic, text) -> text, diagnostic.Message)
    Assert.Equal<(string * string) list>(
        [ "twice(n)", "The result of `twice`, of type 'int', does nothing and is never used: remove it, or use its value"
          "n \\twice", "The result of `twice`, of type 'int', does nothing and is never used: remove it, or use its value" ],
        messages)

[<Fact>]
let ``Syntax the parser makes a built-in call is reported by what was written`` () =
    let source =
        """def b: (x: int, y: int) -> int = { p ->
  p with (x = 3)
  p.x
}

def c: int -> int = { n ->
  def base = (a = n)
  (...base, b = 1)
  n
}
"""
    let messages = reported source |> List.map (fun (diagnostic, text) -> text, diagnostic.Message)
    Assert.Equal<(string * string) list>(
        [ "p with (x = 3)", "This `with` update, of type '(x: int, y: int)', does nothing and is never used: remove it, or use its value"
          "(...base, b = 1)", "This record, of type '(a: int, b: int)', does nothing and is never used: remove it, or use its value" ],
        messages)

[<Fact>]
let ``A map, which finds an instance of a context for its keys, is left to unusedResults`` () =
    let source =
        """def c: int -> int = { n ->
  [n => "n"]
  n
}
"""
    Assert.Empty(reported source)
    let diagnostics = compiled (source.ReplaceLineEndings "\n")
    Assert.Contains(diagnostics, fun diagnostic -> diagnostic.Code = "default/unusedResults")

[<Fact>]
let ``The fix removes the statement`` () =
    let source =
        """def f: int -> int = { n ->
  n + 2
  n
}
"""
    let source = source.ReplaceLineEndings "\n"
    match reported source with
    | [ diagnostic, _ ] ->
        let fixedSource = applyFix source diagnostic.Fixes.Head
        Assert.Equal("""def f: int -> int = { n ->
  n
}
""".ReplaceLineEndings "\n", fixedSource)
        Assert.Empty(reported fixedSource)
    | other -> failwith $"Expected one report, got %A{other}"

[<Fact>]
let ``A call that requires a context is left to unusedResults`` () =
    let source =
        """def shout: [Console] string -> int = { s ->
  println(s)
  1
}

def f: [Console] int -> int = { n ->
  readLine()
  shout("hi")
  n
}
"""
    Assert.Empty(reported source)
    let diagnostics = compiled (source.ReplaceLineEndings "\n")
    let unused = diagnostics |> List.filter (fun diagnostic -> diagnostic.Code = "default/unusedResults")
    Assert.Equal(2, unused.Length)

[<Fact>]
let ``A pure expression with a call that requires a context in it is left to unusedResults`` () =
    let source =
        """def size: [Console] () -> int = { -> 2 }

def f: [Console] int -> int = { n ->
  n + size()
  n
}
"""
    Assert.Empty(reported source)

[<Fact>]
let ``A function value that requires a context is not pure`` () =
    let source =
        """def f: [Console] int -> int = { n ->
  { -> println("hi") }
  n
}
"""
    Assert.Empty(reported source)

[<Fact>]
let ``A pure lambda thrown away is reported`` () =
    let source =
        """def f: int -> int = { n ->
  { x: int -> x + n }
  n
}
"""
    Assert.Equal<string list>([ "{ x: int -> x + n }" ], reported source |> List.map snd)

[<Fact>]
let ``A pure value a for loop's body ends with is reported, with a fix only where it is a statement`` () =
    let source =
        """def f: [Console] list(int) -> () = { xs ->
  for xs | x -> x * 2
  for xs | x ->
    println("{x}")
    x + 1
}
"""
    let source = source.ReplaceLineEndings "\n"
    match reported source with
    | [ single, "x * 2"; last, "x + 1" ] ->
        Assert.Empty(single.Fixes)
        let fixedSource = applyFix source last.Fixes.Head
        Assert.DoesNotContain("x + 1", fixedSource)
        Assert.Equal<string list>([ "x * 2" ], reported fixedSource |> List.map snd)
    | other -> failwith $"Expected two reports, got %A{other}"
