module BracketErrorTests

open Xunit
open Parser.Program

/// The line and column (both from 1) a parse of `source` fails at, and the message.
let private failure (source: string) =
    match parseModuleWithTrivia "t.nyx" source with
    | Result.Error error -> (error.Span.StartLine + 1, error.Span.StartColumn + 1), error.Message
    | Result.Ok parsed -> failwith $"Expected a parse error, got %A{parsed.Items}"

[<Fact>]
let ``A list in a block missing its closing bracket fails where the bracket was expected`` () =
    let position, message =
        failure """
def main = {
  def xs = [
    1,
    2

  xs
}
"""
    Assert.Equal((7, 3), position)
    Assert.Contains("Expecting: whitespace, '--' or ']'", message)
    Assert.DoesNotContain("backtracked", message)

[<Fact>]
let ``A list in a block with a comma before its closing bracket fails at the bracket`` () =
    let position, message =
        failure """
def main = {
  def xs = [
    1,
    2,
  ]
  xs
}
"""
    Assert.Equal((6, 3), position)
    Assert.Contains("Expecting: for loop, function call", message)
    Assert.DoesNotContain("backtracked", message)

[<Fact>]
let ``A list on one line in a block missing its closing bracket fails on the next line`` () =
    let position, message =
        failure """
def main = {
  def xs = [1, 2
  xs
}
"""
    Assert.Equal((4, 3), position)
    Assert.Contains("Expecting: whitespace, '--' or ']'", message)

[<Fact>]
let ``A nested list with a comma before its closing bracket fails in the nested list`` () =
    let position, message =
        failure """
def main = {
  def xs = [
    [1, 2],
    [3, 4,
  ]
  xs
}
"""
    Assert.Equal((6, 3), position)
    Assert.Contains("Expecting: for loop, function call", message)
    Assert.DoesNotContain("backtracked", message)

[<Fact>]
let ``A record in a block with a comma before its closing parenthesis fails at the parenthesis`` () =
    let position, message =
        failure """
def main = {
  def r = (
    a = 1,
    b = 2,
  )
  r
}
"""
    Assert.Equal((6, 3), position)
    Assert.Contains("Expecting: record field", message)
    Assert.DoesNotContain("backtracked", message)

[<Fact>]
let ``A call's arguments in a block with a comma before the closing parenthesis fail at the parenthesis`` () =
    let position, message =
        failure """
def main = {
  f(
    1,
    2,
  )
}
"""
    Assert.Equal((6, 3), position)
    Assert.Contains("Expecting: record field", message)
    Assert.DoesNotContain("backtracked", message)

[<Fact>]
let ``A list at the top level missing its closing bracket still fails where the bracket was expected`` () =
    let position, message =
        failure """
def xs = [
  1,
  2

def y = 1
"""
    Assert.Equal((6, 1), position)
    Assert.Contains("Expecting: whitespace, '--' or ']'", message)
