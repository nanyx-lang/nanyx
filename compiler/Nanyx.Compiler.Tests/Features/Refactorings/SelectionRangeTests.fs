module Nanyx.Compiler.Tests.Features.Refactorings.SelectionRange

open Xunit
open NyxCompiler.Refactorings
open Nanyx.Compiler.Tests.TestSupport

// Expand and shrink selection: the chain of syntax around a position, smallest first.

/// The text of each selection range at `$$` in `marked`, smallest first, stopping before the whole file.
let private chain (marked: string) : string list =
    let marked = normalized marked
    let offset = marked.IndexOf "$$"
    let source = marked.Remove(offset, 2)
    (SelectionRanges.at "" source offset).Value
    |> List.map (fun span -> source.Substring(span.Start, span.End - span.Start))
    |> List.filter (fun text -> text <> source)

[<Fact>]
let ``The chain grows from a name to the call, its arguments, the statement, the lambda and the def`` () =
    let source =
        """def area = { s ->
  def side = max(s$$, 2)
  side * side
}
"""
    Assert.Equal<string list>(
        [ "s"
          "s, 2"
          "max(s, 2)"
          "def side = max(s, 2)"
          """def side = max(s, 2)
  side * side"""
          """{ s ->
  def side = max(s, 2)
  side * side
}"""
          """def area = { s ->
  def side = max(s, 2)
  side * side
}""" ],
        chain source)

[<Fact>]
let ``In a pipe the chain grows from an argument to the call the pipe makes, the pipe so far, then the whole pipe`` () =
    let source =
        """def evens = xs \map(f$$) \filter(g)
"""
    Assert.Equal<string list>(
        [ "f"; "map(f)"; """xs \map(f)"""; """xs \map(f) \filter(g)"""; """def evens = xs \map(f) \filter(g)""" ],
        chain source)

[<Fact>]
let ``In a match the chain grows from a pattern's part to the pattern, the arm and the match`` () =
    let source =
        """def describe = { o ->
  match o
  | #some(n$$) -> n
  | #none -> 0
}
"""
    Assert.Equal<string list>(
        [ "n"
          "#some(n)"
          "#some(n) -> n"
          """match o
  | #some(n) -> n
  | #none -> 0""" ],
        chain source |> List.take 4)

[<Fact>]
let ``In a match arm the chain grows from the body to the arm`` () =
    let source =
        """def describe = { o ->
  match o
  | #some(n) -> n + 1$$
  | #none -> 0
}
"""
    Assert.Equal<string list>([ "1"; "n + 1"; "#some(n) -> n + 1" ], chain source |> List.take 3)

[<Fact>]
let ``In a guard the chain grows from the predicate's parts to the guard and the arm`` () =
    let source =
        """def sign = { n ->
  match n
  | { < 0$$ } -> -1
  | _ -> 1
}
"""
    let ranges = chain source
    Assert.Equal("0", ranges.Head)
    Assert.Contains("{ < 0 }", ranges)
    Assert.Contains("{ < 0 } -> -1", ranges)

[<Fact>]
let ``In an interpolated string the chain grows from a hole's expression to the string`` () =
    let source =
        """def greeting = { name -> "Hello, {name$$ \trim}!" }
"""
    let ranges = chain source
    Assert.Equal<string list>([ "name"; """name \trim""" ], ranges |> List.take 2)
    Assert.Contains("\"Hello, {name \\trim}!\"", ranges)

[<Fact>]
let ``In a record the chain grows from a field's value to the field, the fields and the record`` () =
    let source =
        """def origin = (x = 0$$, y = 0)
"""
    Assert.Equal<string list>([ "0"; "x = 0"; "x = 0, y = 0"; "(x = 0, y = 0)"; "def origin = (x = 0, y = 0)" ], chain source)

[<Fact>]
let ``In a type annotation the chain grows from a part of the type to the type and the def`` () =
    let source =
        """def pairs: list((int$$, string)) = []
"""
    Assert.Equal<string list>(
        [ "int"; "(int, string)"; "list((int, string))"; "def pairs: list((int, string)) = []" ],
        chain source)

[<Fact>]
let ``A lambda's parameters are in the chain`` () =
    let source =
        """def add = { a$$, b -> a + b }
"""
    Assert.Equal<string list>([ "a"; "a, b"; "{ a, b -> a + b }"; "def add = { a, b -> a + b }" ], chain source)

[<Fact>]
let ``The chain reaches a module block`` () =
    let source =
        """module geometry =
  def unit = 1$$
"""
    let ranges = chain source
    Assert.Equal<string list>([ "1"; "def unit = 1" ], ranges |> List.take 2)
    Assert.StartsWith("module geometry =", List.last ranges)

[<Fact>]
let ``In whitespace the chain starts at the smallest node around it`` () =
    let source =
        """def area = { s ->
  def side = s
$$
  side * side
}
"""
    Assert.Equal<string list>(
        [ """def side = s

  side * side""" ],
        chain source |> List.take 1)

[<Fact>]
let ``Every step grows the selection`` () =
    let source =
        """def run = { () -> (f$$) }
"""
    let ranges = chain source
    ranges |> List.pairwise |> List.iter (fun (inner, outer) -> Assert.True(outer.Length > inner.Length, $"{inner} then {outer}"))

[<Fact>]
let ``A source that does not parse has no chain`` () =
    Assert.True((SelectionRanges.at "" "def = = (" 3).IsNone)
