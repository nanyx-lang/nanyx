module Nanyx.Compiler.Tests.Features.Refactorings.Rename

open Xunit
open NyxCompiler
open NyxCompiler.Refactorings
open Nanyx.Compiler.Tests.TestSupport

// Rename: a name and every use of it, within the module.

/// `marked` without its `$$`, and where the `$$` was.
let private unmark (marked: string) =
    let marked = normalized marked
    let offset = marked.IndexOf "$$"
    marked.Remove(offset, 2), offset

/// Renaming the name at `$$` in `marked` to `newName`: the text after it, or why it is refused.
let private renamed (newName: string) (marked: string) =
    let source, offset = unmark marked
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Rename.edits "" source result offset newName Compiler.compile |> Result.map (applyEdits source)

/// The text after renaming the name at `$$` in `marked` to `newName`.
let private renamedTo (newName: string) (marked: string) =
    match renamed newName marked with
    | Ok after -> after
    | Error reason -> failwithf "expected the rename, got: %s" reason

let private refusedBecause (newName: string) (marked: string) =
    match renamed newName marked with
    | Error reason -> reason
    | Ok after -> failwithf "expected a refusal, got\n%s" after

[<Fact>]
let ``A local and its uses are renamed`` () =
    Assert.Equal(
        normalized
            """def area = { (s: int) ->
  def width = s * 2
  width * width
}
""",
        renamedTo
            "width"
            """def area = { (s: int) ->
  def si$$de = s * 2
  side * side
}
""")

[<Fact>]
let ``A parameter is renamed from one of its uses`` () =
    Assert.Equal(
        "def double = { (n: int) -> n + n }\n",
        renamedTo "n" "def double = { (x: int) -> x + x$$ }\n")

[<Fact>]
let ``A top-level def is renamed where it is called`` () =
    Assert.Equal(
        normalized
            """def twice = { (n: int) -> n * 2 }

def four = twice(2)
""",
        renamedTo
            "twice"
            """def double = { (n: int) -> n * 2 }

def four = dou$$ble(2)
""")

[<Fact>]
let ``A pattern variable is renamed in its arm`` () =
    Assert.Equal(
        normalized
            """def first = { o -> match o
  | #some(value) -> value
  | #none -> 0 }
""",
        renamedTo
            "value"
            """def first = { o -> match o
  | #some(n$$) -> n
  | #none -> 0 }
""")

[<Fact>]
let ``A type is renamed in the types that name it`` () =
    Assert.Equal(
        normalized
            """type Size = (width: int, height: int)
def unit: Size = (width = 1, height = 1)
def area = { (s: Size) -> s.width * s.height }
""",
        renamedTo
            "Size"
            """type Box$$ = (width: int, height: int)
def unit: Box = (width = 1, height = 1)
def area = { (s: Box) -> s.width * s.height }
""")

[<Fact>]
let ``An attached definition keeps its type's part`` () =
    Assert.Equal(
        normalized
            """def string.yell = { (s: string) -> "{s}!" }
def loud = "hi" \yell
""",
        renamedTo
            "yell"
            """def string.sh$$out = { (s: string) -> "{s}!" }
def loud = "hi" \shout
""")

[<Fact>]
let ``A rename that would hide another name at a use is refused`` () =
    Assert.Equal(
        "`limit` on line 4 would refer to something else",
        refusedBecause
            "limit"
            """def limit = 10
def check = { (n: int) ->
  def ca$$p = n * 2
  cap < limit
}
""")

[<Fact>]
let ``A rename that another name would hide at a use is refused`` () =
    let reason =
        refusedBecause
            "n"
            """def total = 1
def add = { (n: int) -> n + tot$$al }
"""
    Assert.EndsWith("would refer to something else", reason)

[<Fact>]
let ``A rename to a name defined beside it is refused`` () =
    Assert.StartsWith(
        "The renamed module would not compile",
        refusedBecause
            "two"
            """def one$$ = 1
def two = 2
""")

[<Fact>]
let ``A new name that is not a name is refused`` () =
    Assert.Equal("`2x` is not a name", refusedBecause "2x" "def o$$ne = 1\n")

[<Fact>]
let ``A keyword is refused as a new name`` () =
    Assert.Equal("`match` is a keyword", refusedBecause "match" "def o$$ne = 1\n")

[<Fact>]
let ``A one-letter lowercase name is refused for a type`` () =
    Assert.Equal("`t` would be a type variable, not a type's name", refusedBecause "t" "type Bo$$x = (width: int)\n")

[<Fact>]
let ``An exported def is refused, as other modules may use it`` () =
    Assert.StartsWith("It is exported", refusedBecause "two" "export def o$$ne: int = 1\n")

[<Fact>]
let ``A standard library name can't be renamed`` () =
    let source, offset = unmark "def n = [1, 2] \\su$$m\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    match Rename.prepare Rename.Module "" source result offset with
    | Some (Error reason) -> Assert.StartsWith("It is declared in another module", reason)
    | other -> failwithf "expected a refusal, got %A" other

[<Fact>]
let ``Prepare gives the name's own part`` () =
    let source, offset = unmark "def string.sh$$out = { (s: string) -> s }\n"
    let result = Compiler.compile source
    match Rename.prepare Rename.Module "" source result offset with
    | Some (Ok target) -> Assert.Equal(("shout", "shout"), (target.Name, source.Substring(target.Span.Start, target.Span.End - target.Span.Start)))
    | other -> failwithf "expected a target, got %A" other
