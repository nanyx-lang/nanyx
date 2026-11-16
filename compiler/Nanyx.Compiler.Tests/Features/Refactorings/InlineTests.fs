module Nanyx.Compiler.Tests.Features.Refactorings.Inline

open Xunit
open NyxCompiler
open NyxCompiler.Refactorings
open Nanyx.Formatter
open Nanyx.Compiler.Tests.TestSupport

// Inline: a def's uses become its value, a function's calls its body.

/// The offers at `^` in `marked`, and the source without the mark.
let private offersAt (marked: string) =
    let marked = normalized marked
    let offset = marked.IndexOf "^"
    let source = marked.Replace("^", "")
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    source, Inline.at Formatter.Options.defaults "" source result offset Compiler.compile

/// The text after the offer titled starting `title` at the mark, checked to compile and be formatted.
let private rewrittenBy (title: string) (marked: string) =
    match offersAt marked with
    | source, offers ->
        match offers |> List.tryPick (function Available refactoring when refactoring.Title.StartsWith title -> Some refactoring | _ -> None) with
        | Some refactoring ->
            let after = applyEdits source refactoring.Edits
            let result = Compiler.compile after
            Assert.True(result.Diagnostics.IsEmpty, sprintf "%s\n%A" after result.Diagnostics)
            match FormatCommand.formatText Formatter.Options.defaults "" after with
            | FormatCommand.Formatting.Formatted formatted -> Assert.Equal(formatted, after)
            | other -> failwithf "%A" other
            after
        | None -> failwithf "expected %s, got %A" title offers

/// The reason the offer titled starting `title` is refused at the mark.
let private refusal (title: string) (marked: string) =
    match snd (offersAt marked) |> List.tryPick (function Refused(offered, RefactoringKind.Inline, reason) when offered.StartsWith title -> Some reason | _ -> None) with
    | Some reason -> reason
    | None -> failwithf "expected a refusal of %s, got %A" title (snd (offersAt marked))

[<Fact>]
let ``On a local's name every use becomes its value and the def is removed`` () =
    Assert.Equal(
        normalized
            """def area = { (s: int) ->
  s * 2 * (s * 2)
}
""",
        rewrittenBy
            "Inline `side`"
            """def area = { (s: int) ->
  def ^side = s * 2
  side * side
}
""")

[<Fact>]
let ``On a use only that use is inlined`` () =
    Assert.Equal(
        normalized
            """def area = { (s: int) ->
  def side = s * 2
  s * 2 + side
}
""",
        rewrittenBy
            "Inline this use"
            """def area = { (s: int) ->
  def side = s * 2
  ^side + side
}
""")

[<Fact>]
let ``A function's call becomes its body with the arguments put in`` () =
    Assert.Equal(
        normalized
            """def total = { (n: int) ->
  n * 2 + 1
}
""",
        rewrittenBy
            "Inline `double`"
            """def total = { (n: int) ->
  def ^double = { (x: int) -> x * 2 }
  double(n) + 1
}
""")

[<Fact>]
let ``An argument used twice that is not a name is bound by a def first`` () =
    let after =
        rewrittenBy
            "Inline this use"
            """def square = { (x: int) -> x * x }

def total = { (n: int) ->
  def r = ^square(n + 1)
  r
}
"""
    Assert.Contains("  def x = n + 1\n  def r = x * x\n", after)

[<Fact>]
let ``A function whose body is a block has its statements put before the statement`` () =
    let after =
        rewrittenBy
            "Inline this use"
            """def describe = { (x: int) ->
  def doubled = x * 2
  doubled + 1
}

def total = { (n: int) ->
  def r = ^describe(n)
  r
}
"""
    Assert.Contains("  def doubled = n * 2\n  def r = doubled + 1\n", after)

[<Fact>]
let ``A function used as a value becomes the lambda`` () =
    let after =
        rewrittenBy
            "Inline `double`"
            """def ^double = { (x: int) -> x * 2 }

def total = { (xs: list(int)) -> xs \map(double) }
"""
    Assert.Contains("xs \\map({ (x: int) -> x * 2 })", after)

[<Fact>]
let ``An exported def is inlined only at a use`` () =
    Assert.Equal(
        "It is exported: other modules may use it, so it is inlined at a use only",
        refusal
            "Inline `scale`"
            """export def ^scale = 3

def total = scale * 2
""")

[<Fact>]
let ``A recursive def is refused`` () =
    Assert.Equal(
        "It is recursive",
        refusal
            "Inline `count`"
            """def ^count: int -> int = { n -> if n == 0 then 0 else count(n - 1) }

def total = count(3)
""")

[<Fact>]
let ``A value whose name would be shadowed at a use is refused`` () =
    Assert.Equal(
        "`s` would refer to something else where it is used",
        refusal
            "Inline `side`"
            """def area = { (s: int) ->
  def ^side = s * 2
  def inner = { (s: int) -> side + s }
  inner(1)
}
""")

[<Fact>]
let ``A value that reads a cell is refused`` () =
    Assert.Equal(
        "It reads the cell `count`, which may change before it is used",
        refusal
            "Inline `seen`"
            """def counted = { () -> memory {
  mut count = 0
  def ^seen = count + 1
  count := 5
  seen
} }
""")

[<Fact>]
let ``A value that calls a function requiring a context is refused`` () =
    Assert.StartsWith(
        "It calls a function that requires a context",
        refusal
            "Inline `line`"
            """context Log = (next: () -> int)
def twice: [Log] () -> int = { () ->
  def ^line = next()
  line + line
}
""")

[<Fact>]
let ``Nothing is offered on a parameter`` () =
    Assert.Empty(snd (offersAt "def area = { (^s: int) -> s * 2 }\n"))
