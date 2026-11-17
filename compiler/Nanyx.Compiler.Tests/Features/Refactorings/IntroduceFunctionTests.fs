module Nanyx.Compiler.Tests.Features.Refactorings.IntroduceFunction

open Xunit
open NyxCompiler
open NyxCompiler.Refactorings
open Nanyx.Formatter
open Nanyx.Compiler.Tests.TestSupport

// Introduce function: the selection becomes a def of a function, and a call to it.

/// The offers for the selection between `[[` and `]]` in `marked`, and the source without the marks.
let private offersAt (marked: string) =
    let marked = normalized marked
    let start = marked.IndexOf "[["
    let finish = marked.IndexOf "]]" - 2
    let source = marked.Replace("[[", "").Replace("]]", "")
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let selection : Parser.Program.SourceSpan = { File = ""; Start = start; End = finish; StartLine = 0; StartColumn = 0 }
    source, IntroduceFunction.at Formatter.Options.defaults "" source result selection Compiler.compile |> List.map Offer.decided

/// The text after the offer titled starting `title` at the selection, checked to compile and be formatted, and that
/// the new name is where the offer says it is.
let private rewrittenBy (title: string) (marked: string) =
    match offersAt marked with
    | source, offers ->
        match offers |> List.tryPick (function Available refactoring when refactoring.Title.StartsWith title -> Some refactoring | _ -> None) with
        | Some refactoring ->
            let after = refactoredText source refactoring
            let at = Option.get refactoring.Rename
            Assert.Equal("extracted", after.Substring(at, "extracted".Length))
            after
        | None -> failwithf "expected %s, got %A" title offers

let private rewritten (marked: string) = rewrittenBy "Introduce function" marked

/// The reason the refactoring is refused at the selection.
let private refusal (marked: string) =
    match snd (offersAt marked) with
    | Refused(_, RefactoringKind.Extract, reason) :: _ -> reason
    | offers -> failwithf "expected a refusal, got %A" offers

[<Fact>]
let ``An expression becomes a top-level function of the locals it uses, after the def that holds it`` () =
    Assert.Equal(
        normalized
            """def area = { (w: int), (h: int) ->
  def inner = extracted(w, h)
  inner + 1
}

def extracted: [Num(a)] a, a -> a = { w, h ->
  w * h
}
""",
        rewritten
            """def area = { (w: int), (h: int) ->
  def inner = [[w * h]]
  inner + 1
}
""")

[<Fact>]
let ``The parameters are in the order the selection first uses them`` () =
    let after =
        rewritten
            """def area = { (w: int), (h: int) ->
  [[h - w * h]]
}
"""
    Assert.True(after.Contains "extracted(h, w)", after)

[<Fact>]
let ``Top-level defs are not parameters`` () =
    let after =
        rewritten
            """def scale = 3

def area = { (w: int) ->
  [[w * scale]]
}
"""
    Assert.True(after.Contains "extracted(w)", after)

[<Fact>]
let ``An expression that uses no locals takes no parameters`` () =
    let after = rewritten "def total = [[2 * 3]] + 1\n"
    Assert.True(after.Contains "def total = extracted() + 1", after)
    Assert.True(after.Contains "def extracted: () -> int = { () ->\n  2 * 3\n}", after)

[<Fact>]
let ``A local function goes before the statement that holds the selection`` () =
    Assert.Equal(
        normalized
            """def area = { (w: int), (h: int) ->
  def extracted: int, int -> int = { w, h ->
    w * h
  }

  def inner = extracted(w, h)
  inner + 1
}
""",
        rewrittenBy
            "Introduce local function"
            """def area = { (w: int), (h: int) ->
  def inner = [[w * h]]
  inner + 1
}
""")

[<Fact>]
let ``Statements whose locals are not used after them become a call`` () =
    Assert.Equal(
        normalized
            """def show = { (n: int) ->
  extracted(n)
  n
}

def extracted: [Console] int -> () = { n ->
  def doubled = n * 2
  println(doubled)
}
""",
        rewritten
            """def show = { (n: int) ->
  [[def doubled = n * 2
  println(doubled)]]
  n
}
""")

[<Fact>]
let ``A local the statements define and the statements after use is the function's result`` () =
    let after =
        rewritten
            """def area = { (w: int) ->
  [[def doubled = w * 2
  def tripled = w * 3]]
  doubled + tripled
}
"""
    Assert.True(after.Contains "def (doubled, tripled) = extracted(w)", after)
    Assert.True(after.Contains "  (doubled, tripled)\n", after)

[<Fact>]
let ``One local used after is defined by the call`` () =
    let after =
        rewritten
            """def area = { (w: int) ->
  [[def doubled = w * 2]]
  doubled + 1
}
"""
    Assert.True(after.Contains "def doubled = extracted(w)", after)

[<Fact>]
let ``The type written requires the contexts the selection does`` () =
    let after =
        rewritten
            """def greet = { (name: string) ->
  [[println(name)]]
}
"""
    Assert.True(after.Contains "def extracted: [ToString(a), Console] a -> () = { name ->", after)

[<Fact>]
let ``A cell the selection uses is passed as a cell`` () =
    let after =
        rewritten
            """def counted = { () -> memory {
  mut count = 0
  [[count := count + 1]]
  count
} }
"""
    Assert.True(after.Contains "extracted(mut count)", after)
    Assert.True(after.Contains "def extracted: [Memory] mut int -> () = { mut count ->\n  count := count + 1\n}", after)

[<Fact>]
let ``A selection inside a lambda takes the lambda's parameter`` () =
    let after =
        rewritten
            """def bump = { (xs: list(int)) -> xs \map { x -> [[x * 2]] + 1 } }
"""
    Assert.True(after.Contains "xs \\map { x -> extracted(x) + 1 }", after)

[<Fact>]
let ``A selection that uses return is refused`` () =
    Assert.StartsWith(
        "It uses `try` or `return`",
        refusal
            """def half = { (n: int) -> proc {
  def m = try (if n > 0 then #some(n) else #error(#negative))
  [[return m / 2]]
} }
""")

[<Fact>]
let ``Statements that declare a cell used after them are refused`` () =
    Assert.Equal(
        "It declares the cell `count`, which the statements after it use",
        refusal
            """def counted = { () -> memory {
  [[mut count = 0]]
  count := count + 1
  count
} }
""")

[<Fact>]
let ``Statements that end with an attribute are refused`` () =
    Assert.Equal(
        "It ends with an attribute, which applies to the def after it",
        refusal
            """def area = { (s: int) ->
  [[def a = s
  @allow(default/unusedBindings)]]
  def b = 1
  a
}
""")

[<Fact>]
let ``A selection inside a def that is not a whole expression is refused`` () =
    Assert.Equal(
        "The selection is not a whole expression or whole statements",
        refusal "def area = { (s: int) -> s [[* 2]] }\n")

[<Fact>]
let ``Nothing is offered outside any def`` () =
    Assert.Empty(snd (offersAt "[[import nanyx/math]]\n\ndef area = 1\n"))
