module TranspilerWasmTests.Features.Memory.MatchBorrowing

open Xunit
open TranspilerWasmTestHelpers

// A function borrows a parameter it matches on when nothing bound from it is taken over, save by a call that borrows
// it in turn, and no arm could reuse its block, as Lean 4 infers it (#1468).

let private sumList =
    """
def sumList: list(int), int -> int = {
  | [], acc -> acc
  | [x, ...rest], acc -> sumList(rest, acc + x)
}
"""

let private eval =
    """
type Expr = #num(int) | #add(Expr, Expr) | #neg(Expr)

def eval: Expr -> int = {
  | #num(n) -> n
  | #add(a, b) -> eval(a) + eval(b)
  | #neg(e) -> 0 - eval(e)
}
"""

/// How many calls `text` makes that count references or free blocks.
let private counting (text: string) =
    [ "call $__nanyx_dup"; "call $__nanyx_drop"; "call $__nanyx_release"; "call $__nanyx_free" ] |> List.sumBy (fun call -> countOccurrences call text)

[<Fact>]
let ``Match borrowing - a fold over a list it only reads counts nothing`` () =
    let wat = transpileWatNotInlined (sumList.ReplaceLineEndings "\n")
    Assert.Equal(0, counting (functionText "sumList" wat))

[<Fact>]
let ``Match borrowing - an evaluator over a tree it only reads counts nothing`` () =
    let wat = transpileWatNotInlined (eval.ReplaceLineEndings "\n")
    Assert.Equal(0, counting (functionText "eval" wat))

[<Fact>]
let ``Match borrowing - a function that returns a part of what it matches owns it`` () =
    let source =
        """
def rest: list(int) -> list(int) = {
  | [] -> []
  | [_, ...tail] -> tail
}
"""
    let wat = transpileWatNotInlined (source.ReplaceLineEndings "\n")
    Assert.Contains("call $__nanyx_release", functionText "rest" wat)

[<Fact>]
let ``Match borrowing - a function whose arm builds a cell of the same size owns the list, to reuse it`` () =
    let source =
        """
def double: list(int), list(int) -> list(int) = {
  | [], acc -> acc
  | [x, ...rest], acc -> double(rest, [x * 2, ...acc])
}
"""
    let wat = transpileWatNotInlined (source.ReplaceLineEndings "\n")
    Assert.Contains("call $__nanyx_list_cons_at", functionText "double" wat)

[<Fact>]
let ``Run - a shared list and tree read again and again give their sums, and memory balances`` () =
    let source =
        sumList
        + eval
        + """
@entry
def main = {
  def xs = [1, 2, 3, 4]
  def tree = #add(#num(5), #neg(#add(#num(1), #num(2))))
  println(sumList(xs, 0) + sumList(xs, 0))
  println(eval(tree) + eval(tree))
  println(sumList(xs, eval(tree)))
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "20\n4\n12\n"
