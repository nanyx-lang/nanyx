module Nanyx.Compiler.Tests.Features.Linters.RepeatedPulls

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The `default/repeatedPulls` diagnostics for `source`, compiled with every linter on. The module must type-check, or
/// no linter would run.
let private repeatedPulls (source: string) =
    let source = normalized source
    let result = Compiler.compileWith (everyLinter ()) source
    Assert.True(result.TypeChecked && not (result.Diagnostics |> List.exists Diagnostics.isError), sprintf "%A" result.Diagnostics)
    result.Diagnostics |> List.filter (fun diagnostic -> diagnostic.Code = "default/repeatedPulls")

/// What `default/repeatedPulls` reports for `source`: each message with the text it is reported at.
let private reported (source: string) =
    repeatedPulls source |> List.map (fun diagnostic -> diagnostic.Message, textAt (normalized source) diagnostic.Span.Value)

/// A context, and a sequence whose steps require it: `numbers()` logs as each element is pulled.
let private numbers =
    """context Log = (log: string -> ())

def numbers: [Log] () -> [Log] seq(int) = { () ->
  seq.map(1..3) { x ->
    log("x")
    x
  }
}

"""

let private again (name: string) =
    $"'{name}' is pulled again here, and its steps, which require Log, run again: pull it once into a list with `seq.toList` and use that"

[<Fact>]
let ``A sequence with effects pulled twice is reported where it is pulled again, as a warning`` () =
    let source =
        numbers
        + """def main: [Log] () -> () = { () ->
  def lines = numbers()
  def n = seq.length(lines)
  for lines | line -> log("{line} of {n}")
}
"""
    let diagnostic = Assert.Single(repeatedPulls source)
    Assert.Equal(Severity.Warning, diagnostic.Severity)
    Assert.Equal<(string * string) list>([ again "lines", "lines" ], reported source)
    Assert.Equal<string list>([ "lines" ], diagnostic.Related |> List.map (fst >> textAt (normalized source)))
    Assert.Equal(Some "lines", diagnostic.Subject)

[<Fact>]
let ``Its fix pulls it once into a list, and passes the list as a sequence where a sequence is needed`` () =
    let source =
        numbers
        + """def main: [Log] () -> () = { () ->
  def lines = numbers()
  def n = seq.length(lines)
  for lines | line -> log("{line} of {n}")
}
"""
    let fix = Assert.Single((Assert.Single(repeatedPulls source)).Fixes)
    Assert.Equal("Pull `lines` once into a list", fix.Title)
    let fixedSource = applyFix (normalized source) fix
    Assert.Contains("  def lines = numbers() \\seq.toList\n  def n = seq.length(lines \\list.toSeq)\n  for lines |", fixedSource)
    Assert.Empty(repeatedPulls fixedSource)

[<Fact>]
let ``A pure sequence pulled twice is not reported`` () =
    Assert.Empty(reported "def main: () -> int = { () ->\n  def s = seq.map(1..3) { x -> x * 2 }\n  seq.length(s) + seq.length(s)\n}\n")

[<Fact>]
let ``A sequence with effects pulled once through a chain of filter and map is not reported`` () =
    Assert.Empty(
        reported (
            numbers
            + "def main: [Log] () -> list(int) = { () ->\n  numbers() \\seq.filter { x -> x > 1 } \\seq.map { x -> x * 2 } \\seq.toList\n}\n"
        )
    )

[<Fact>]
let ``Pulls in different arms of a match are on different paths`` () =
    let source =
        numbers
        + """def main: [Log] bool -> int = { flag ->
  def lines = numbers()
  match flag
    | true -> seq.length(lines)
    | false -> seq.fold(lines, 0) { total, x -> total + x }
}
"""
    Assert.Empty(reported source)

[<Fact>]
let ``Pulls in different branches of an if are on different paths, but one after the if is not`` () =
    let source =
        numbers
        + """def main: [Log] bool -> int = { flag ->
  def lines = numbers()
  def n = if flag then seq.length(lines) else 0
  n + seq.length(lines)
}
"""
    Assert.Equal<(string * string) list>([ again "lines", "lines" ], reported source)

[<Fact>]
let ``Pulling a sequence made from one with effects pulls that one`` () =
    let source =
        numbers
        + """def main: [Log] () -> int = { () ->
  def lines = numbers()
  def big = seq.filter(lines) { x -> x > 1 }
  seq.length(big) + seq.length(lines)
}
"""
    Assert.Equal<(string * string) list>([ again "lines", "lines" ], reported source)

[<Fact>]
let ``A pull inside a loop counts as many`` () =
    let source =
        numbers
        + """def main: [Log] () -> () = { () ->
  def lines = numbers()
  for 1..3 | i -> log("{seq.length(lines)}")
}
"""
    Assert.Equal<(string * string) list>(
        [ "'lines' is pulled each time round the loop, and its steps, which require Log, run again: pull it once into a list with `seq.toList` and use that", "lines" ],
        reported source)

[<Fact>]
let ``A parameter pulled twice is reported, with no fix`` () =
    let source =
        numbers
        + """def total: [Log] ([Log] seq(int)) -> int = { s ->
  seq.length(s) + seq.fold(s, 0) { sum, x -> sum + x }
}
"""
    let diagnostic = Assert.Single(repeatedPulls source)
    Assert.Empty(diagnostic.Fixes)
    Assert.Equal<(string * string) list>([ again "s", "s" ], reported source)

[<Fact>]
let ``A sequence whose contexts are only a variable is not reported`` () =
    Assert.Empty(reported "def total: [c] ([c] seq(int)) -> int = { s ->\n  seq.length(s) + seq.length(s)\n}\n")

[<Fact>]
let ``Passing a sequence to a function generic in its parameter does not pull it`` () =
    let source =
        numbers
        + """def keep: a -> (value: a) = { x -> (value = x) }

def main: [Log] () -> int = { () ->
  def lines = numbers()
  def kept = keep(lines)
  def again = keep(lines)
  seq.length(lines)
}
"""
    Assert.Empty(reported source)

[<Fact>]
let ``A for whose body yields makes a sequence from the one it walks, and does not pull it`` () =
    let source (pulls: string) =
        numbers
        + $"""def main: [Log] () -> int = {{ () ->
  def lines = numbers()
  def doubled = seq {{
    for lines | x -> yield x * 2
  }}
  {pulls}
}}
"""
    Assert.Empty(reported (source "seq.length(lines)"))
    Assert.Equal<(string * string) list>([ again "lines", "lines" ], reported (source "seq.length(doubled) + seq.length(lines)"))

[<Fact>]
let ``A user function that takes a sequence and gives back something else pulls it`` () =
    let source =
        numbers
        + """def count: [c] ([c] seq(int)) -> int = { s -> seq.length(s) }

def main: [Log] () -> int = { () ->
  def lines = numbers()
  count(lines) + count(lines)
}
"""
    Assert.Equal<(string * string) list>([ again "lines", "lines" ], reported source)

[<Fact>]
let ``A pipe into a function that pulls is a pull`` () =
    let source =
        numbers
        + """def main: [Log] () -> int = { () ->
  def lines = numbers()
  def all = lines \seq.toList
  lines \seq.length
}
"""
    Assert.Equal<(string * string) list>([ again "lines", "lines" ], reported source)

[<Fact>]
let ``A pull inside a lambda that isn't a loop's counts once`` () =
    let source =
        numbers
        + """def main: [Log] () -> list(int) = { () ->
  def lines = numbers()
  list.map([1, 2]) { i -> i + seq.length(lines) }
}
"""
    Assert.Empty(reported source)

[<Fact>]
let ``The fix puts a value that isn't a call in parentheses before pulling it into a list`` () =
    let source =
        numbers
        + """def main: [Log] bool -> () = { flag ->
  def lines = if flag then numbers() else numbers()
  for lines | x -> log("{x}")
  for lines | x -> log("{x}")
}
"""
    let fix = Assert.Single((Assert.Single(repeatedPulls source)).Fixes)
    let fixedSource = applyFix (normalized source) fix
    Assert.Contains("  def lines = (if flag then numbers() else numbers()) \\seq.toList\n", fixedSource)
    Assert.Empty(repeatedPulls fixedSource)

[<Theory>]
// A pull, `xs` given to a function that gives back something else, in each way a call is written (#3034).
[<InlineData("def first = seq.contains(xs, 2)\n  ignore(first)\n  seq.length(xs)", "xs")>]
[<InlineData("def first = xs \\seq.contains(2)\n  ignore(first)\n  seq.length(xs)", "xs")>]
[<InlineData("def total = seq.fold(xs, 0, { a, b -> a + b })\n  total + seq.length(xs)", "xs")>]
[<InlineData("def total = seq.fold(xs, 0) { a, b -> a + b }\n  total + seq.length(xs)", "xs")>]
// A sequence made from `xs`, pulled, then `xs` pulled again: the first pull is the pull of what was made from it.
[<InlineData("def ys = seq.map(xs, { x -> x + 1 })\n  seq.length(ys) + seq.length(xs)", "ys")>]
[<InlineData("def ys = seq.map(xs) { x -> x + 1 }\n  seq.length(ys) + seq.length(xs)", "ys")>]
[<InlineData("def ys = seq.take(xs, 2)\n  seq.length(ys) + seq.length(xs)", "ys")>]
[<InlineData("def ys = xs \\seq.take(2)\n  seq.length(ys) + seq.length(xs)", "ys")>]
let ``A call pulls the sequence it is given however its arguments are written`` (body: string, firstPull: string) =
    let source = numbers + $"def main: [Log] () -> int = {{ () ->\n  def xs = numbers()\n  {body}\n}}\n"
    let diagnostic = Assert.Single(repeatedPulls source)
    Assert.Equal(again "xs", diagnostic.Message)
    Assert.Equal<string list>([ firstPull ], diagnostic.Related |> List.map (fst >> textAt (normalized source)))
