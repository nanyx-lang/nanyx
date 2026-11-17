module Nanyx.Compiler.Tests.Features.Refactorings.IntroduceLocal

open Xunit
open NyxCompiler
open NyxCompiler.Refactorings
open Nanyx.Formatter
open Nanyx.Compiler.Tests.TestSupport

// Introduce local: the selected expression becomes a def before the statement that holds it.

/// The offers for the selection between `[[` and `]]` in `marked`, and the source without the marks.
let private offersAt (marked: string) =
    let marked = normalized marked
    let start = marked.IndexOf "[["
    let finish = marked.IndexOf "]]" - 2
    let source = marked.Replace("[[", "").Replace("]]", "")
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let selection : Parser.Program.SourceSpan = { File = ""; Start = start; End = finish; StartLine = 0; StartColumn = 0 }
    source, IntroduceLocal.at Formatter.Options.defaults "" source result selection

/// The text after the offer titled starting `title` at the selection, checked to compile.
let private rewrittenBy (title: string) (marked: string) =
    match offersAt marked with
    | source, offers ->
        match offers |> List.tryPick (function Available refactoring when refactoring.Title.StartsWith title -> Some refactoring | _ -> None) with
        | Some refactoring ->
            refactoredText source refactoring
        | None -> failwithf "expected %s, got %A" title offers

let private rewritten (marked: string) = rewrittenBy "Introduce local" marked

/// The reason the refactoring is refused at the selection.
let private refusal (marked: string) =
    match snd (offersAt marked) with
    | [ Refused(_, RefactoringKind.Extract, reason) ] -> reason
    | offers -> failwithf "expected a refusal, got %A" offers

[<Fact>]
let ``A local goes before the statement that holds the expression`` () =
    Assert.Equal(
        normalized
            """def double = { (n: int) -> n * 2 }

def area = { (s: int) ->
  def result = double(s)
  def side = result + 1
  side * side
}
""",
        rewritten
            """def double = { (n: int) -> n * 2 }

def area = { (s: int) ->
  def side = [[double(s)]] + 1
  side * side
}
""")

[<Fact>]
let ``A local is named after the field it reads`` () =
    let after =
        rewritten
            """def area = { (p: (width: int, height: int)) ->
  [[p.width]] * p.height
}
"""
    Assert.Contains("def width = p.width", after)

[<Fact>]
let ``A name already in scope is not taken`` () =
    let after =
        rewritten
            """def area = { (s: int) ->
  def result = 1
  def side = [[s * 2]] + result
  side * side
}
"""
    Assert.Contains("def result2 = s * 2", after)

[<Fact>]
let ``In a nested block the local goes in the innermost block`` () =
    Assert.Equal(
        normalized
            """def area = { (s: int) ->
  def inner = { (t: int) ->
    def result = t * s
    result + 1
  }

  inner(s)
}
""",
        rewritten
            """def area = { (s: int) ->
  def inner = { (t: int) ->
    [[t * s]] + 1
  }
  inner(s)
}
""")

[<Fact>]
let ``A lambda whose body is one expression has its body made a block`` () =
    let after =
        rewritten
            """def bump = { (xs: list(int)) -> xs \map { x -> [[x * 2]] + 1 } }
"""
    Assert.Contains("{ x ->\n    def result = x * 2\n    result + 1\n", after)

[<Fact>]
let ``A pipe's value is introduced before the statement`` () =
    let after =
        rewritten
            """def total = { (xs: list(int)) ->
  def doubled = [[xs \map { x -> x * 2 }]] \sum
  doubled
}
"""
    Assert.Contains("  def result = xs \\map { x -> x * 2 }\n  def doubled = result \\sum", after)

[<Fact>]
let ``Outside any block the local is a def before the top-level item`` () =
    Assert.Equal(
        normalized
            """def result = 2 * 3

def total = 1 + result
""",
        rewritten
            """def total = 1 + [[2 * 3]]
""")

[<Fact>]
let ``Every occurrence after it in the block can be replaced too`` () =
    let after =
        rewrittenBy
            "Introduce local `result` for all 2 occurrences"
            """def area = { (s: int) ->
  def side = [[s * 2]] + 1
  side * (s * 2)
}
"""
    Assert.Equal(
        normalized
            """def area = { (s: int) ->
  def result = s * 2
  def side = result + 1
  side * result
}
""",
        after)

[<Fact>]
let ``A call that requires a context is not replaced elsewhere, as it may do something different each time`` () =
    let _, offers =
        offersAt
            """context Log = (next: () -> int)
def twice: [Log] () -> int = { () ->
  def first = [[next()]] + 1
  first + next()
}
"""
    Assert.Equal(1, offers.Length)

[<Fact>]
let ``An expression in a branch of an if is refused`` () =
    Assert.StartsWith(
        "It is in a branch of an `if`",
        refusal
            """def pick = { (b: bool), (s: int) ->
  def r = if b then [[s * 2]] else 0
  r
}
""")

[<Fact>]
let ``An expression in a match arm is refused`` () =
    Assert.StartsWith(
        "It is in a match arm",
        refusal
            """def pick = { (o: #some(int) | #none) ->
  def r = match o
    | #some(n) -> [[n * 2]]
    | #none -> 0
  r
}
""")

[<Fact>]
let ``An expression on the right of and is refused`` () =
    Assert.StartsWith(
        "It is on the right of `and`",
        refusal
            """def both = { (a: bool), (s: int) ->
  def r = a and [[s > 2]]
  r
}
""")

[<Fact>]
let ``An expression that reads a cell assigned before it is refused`` () =
    Assert.Equal(
        "It reads `count`, which is assigned before it",
        refusal
            """def counted = { () -> memory {
  mut count = 0
  def r = (count := 1, [[count + 1]])
  r
} }
""")

[<Fact>]
let ``Nothing is offered where the selection is not an expression`` () =
    Assert.Empty(snd (offersAt "def area = { (s: int) -> s [[* 2]] }\n"))
