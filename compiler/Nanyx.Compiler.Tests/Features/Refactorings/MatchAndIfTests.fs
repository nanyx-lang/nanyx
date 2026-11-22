module Nanyx.Compiler.Tests.Features.Refactorings.MatchAndIf

open Xunit
open NyxCompiler
open NyxCompiler.Refactorings
open Nanyx.Formatter
open Nanyx.Compiler.Tests.TestSupport

// Convert a match to an `if`, and an `if` to a match.

/// The offers at `$$` in `marked`, and the source without the mark.
let private offersAt (marked: string) =
    let source, offset = unmarked marked
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    source, MatchAndIf.at Formatter.Options.defaults "" source result offset Compiler.compile |> List.map Offer.decided

/// The text after the one offer at `$$`, checked to compile and to be formatted.
let private converted (marked: string) =
    match offersAt marked with
    | source, [ Available refactoring ] ->
        refactoredText source refactoring
    | _, offers -> failwithf "expected one offer, got %A" offers

/// `text` without the line break it starts with, with `\n` line endings.
let private program (text: string) = text.TrimStart().ReplaceLineEndings "\n"

/// The reason the one offer at `$$` is refused.
let private refusal (marked: string) =
    match offersAt marked with
    | _, [ Refused(_, _, reason) ] -> reason
    | _, offers -> failwithf "expected one refusal, got %A" offers

[<Fact>]
let ``A match on true and false becomes an if`` () =
    Assert.Equal(
        program
            """
def pick = { (ok: bool) ->
  if ok then 1 else 2
}
""",
        converted (
            program
                """
def pick = { (ok: bool) ->
  $$match ok
    | true -> 1
    | false -> 2
}
"""
        ))

[<Fact>]
let ``A match on false first keeps each branch`` () =
    Assert.Equal(
        program
            """
def pick = { (ok: bool) ->
  if ok then 1 else 2
}
""",
        converted (
            program
                """
def pick = { (ok: bool) ->
  ma$$tch ok
    | false -> 2
    | _ -> 1
}
"""
        ))

[<Fact>]
let ``A tag and _ become a comparison`` () =
    Assert.Equal(
        program
            """
def describe = { (o: #none | #some(int)) ->
  if o == #none then "nothing" else "something"
}
""",
        converted (
            program
                """
def describe = { (o: #none | #some(int)) ->
  $$match o
    | #none -> "nothing"
    | _ -> "something"
}
"""
        ))

[<Fact>]
let ``Literal arms become an else if chain`` () =
    Assert.Equal(
        program
            """
def name = { (n: int) ->
  if n == 1 then "one" else if n == 2 then "two" else "many"
}
""",
        converted (
            program
                """
def name = { (n: int) ->
  $$match n
    | 1 -> "one"
    | 2 -> "two"
    | _ -> "many"
}
"""
        ))

[<Fact>]
let ``A branch of several lines stays a block`` () =
    Assert.Equal(
        program
            """
def pick = { (ok: bool) ->
  if ok then
    def x = 1
    x + 1
  else 2
}
""",
        converted (
            program
                """
def pick = { (ok: bool) ->
  $$match ok
    | true ->
      def x = 1
      x + 1
    | false -> 2
}
"""
        ))

[<Fact>]
let ``An arm that binds a variable is refused`` () =
    Assert.Equal(
        "An arm binds a variable, which an `if` has no place for",
        refusal
            """
def get = { (o: #none | #some(int)) ->
  $$match o
    | #some(n) -> n
    | _ -> 0
}
""")

[<Fact>]
let ``An if whose condition binds names for its then branch is refused`` () =
    Assert.Equal(
        "Its condition binds names for the `then` branch, which a match on `true` and `false` has no place for",
        refusal
            """
def get = { (o: #none | #some(int)) ->
  $$if o is #some(n) and n > 1 then n else 0
}
""")

[<Fact>]
let ``An arm with a guard is refused`` () =
    Assert.Equal(
        "An arm has a guard, which an `if` has no place for",
        refusal
            """
def sign = { (n: int) ->
  $$match n
    | { < 0 } -> -1
    | _ -> 1
}
""")

[<Fact>]
let ``A match whose last arm is not _ is refused`` () =
    Assert.Equal(
        "The last arm is not `_`, which an `if` needs for its `else`",
        refusal
            """
def name = { (c: #red | #green | #blue) ->
  $$match c
    | #red -> "red"
    | #green -> "green"
    | #blue -> "blue"
}
""")

[<Fact>]
let ``A chain on a value that is not a name is refused`` () =
    Assert.Equal(
        "The matched value would be evaluated once for each arm, not once",
        refusal
            """
def name = { (n: int) ->
  $$match n + 1
    | 1 -> "one"
    | 2 -> "two"
    | _ -> "many"
}
""")

[<Fact>]
let ``A value whose type has no == is refused`` () =
    Assert.StartsWith(
        "The if would not compile",
        refusal
            """
def describe = { (o: #none | #some(int -> int)) ->
  $$match o
    | #none -> "nothing"
    | _ -> "a function"
}
""")

[<Fact>]
let ``An if becomes a match on true and false`` () =
    Assert.Equal(
        program
            """
def pick = { (ok: bool) ->
  match ok
    | true -> 1
    | false -> 2
}
""",
        converted "def pick = { (ok: bool) ->\n  $$if ok then 1 else 2\n}\n")

[<Fact>]
let ``An else if chain comparing one value becomes a match on it`` () =
    Assert.Equal(
        program
            """
def name = { (n: int) ->
  match n
    | 1 -> "one"
    | 2 -> "two"
    | _ -> "many"
}
""",
        converted "def name = { (n: int) ->\n  i$$f n == 1 then \"one\" else if n == 2 then \"two\" else \"many\"\n}\n")

[<Fact>]
let ``An else if chain of other conditions keeps the rest an if`` () =
    Assert.Equal(
        program
            """
def sign = { (n: int) ->
  match n < 0
    | true -> -1
    | false -> if n > 0 then 1 else 0
}
""",
        converted "def sign = { (n: int) ->\n  $$if n < 0 then -1 else if n > 0 then 1 else 0\n}\n")

[<Fact>]
let ``A branch of several lines stays a block in a match`` () =
    Assert.Equal(
        program
            """
def pick = { (ok: bool) ->
  match ok
    | true ->
      def x = 1
      x + 1
    | false -> 2
}
""",
        converted (
            program
                """
def pick = { (ok: bool) ->
  $$if ok
    then
      def x = 1
      x + 1
    else 2
}
"""
        ))

[<Fact>]
let ``Conditions comparing different values are refused`` () =
    Assert.Equal(
        "The conditions don't compare the same value",
        refusal "def pick: int, int -> string = { a, b -> $$if a == 1 then \"a\" else if b == 1 then \"b\" else \"neither\" }\n")

[<Fact>]
let ``A chain comparing a value that is not a name is refused`` () =
    Assert.Equal(
        "The compared value would be evaluated once, not once for each condition",
        refusal "def name = { (n: int) -> $$if n + 1 == 1 then \"one\" else if n + 1 == 2 then \"two\" else \"many\" }\n")

[<Fact>]
let ``Elsewhere nothing is offered`` () =
    Assert.Empty(snd (offersAt "def pick = { (ok: bool) -> if $$ok then 1 else 2 }\n"))

[<Fact>]
let ``A left-out else is unit`` () =
    Assert.Equal(
        program
            """
def check = { (ok: bool) ->
  match ok
    | true -> ()
    | false -> ()
}
""",
        converted "def check = { (ok: bool) ->\n  $$if ok then ()\n}\n")