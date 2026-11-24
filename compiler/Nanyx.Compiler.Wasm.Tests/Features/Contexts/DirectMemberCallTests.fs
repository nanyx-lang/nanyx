/// A context instance a `use` makes is a record of its members' closures. Once its block is inlined, a field read from
/// the record is the closure it was made with, so a call of a member is a direct call of the closure's function, which
/// is inlined in turn, and the closure and the record are made no more (#2578).
module TranspilerWasmTests.Features.Contexts.DirectMemberCall

open System.Text.RegularExpressions
open Xunit
open TranspilerWasmTestHelpers

/// `selected(n)` emits each of the numbers below `n`, and the odd one after each even one, through `list.select`, whose
/// `list.build` handles `Emit` with a closure over the list it builds; `listed(n)` only makes the list it selects from.
let private selecting =
    normalized
        """def selected: int -> int = { n ->
  def ys = list.fromSeq(0..n) \list.select { x ->
    emit(x)
    if x % 2 == 0 then emit(x + 1)
  }
  list.length(ys)
}

def listed: int -> int = { n -> list.length(list.fromSeq(0..n)) }
"""

[<Fact>]
let ``A member of a use's instance is called directly once its block is inlined`` () =
    let selected = functionText "selected" (transpileWat selecting)
    Assert.Equal(0, Regex.Matches(selected, "call_indirect").Count)

[<Fact>]
let ``A use whose members are all called directly makes neither its instance nor their closures`` () =
    use program = instantiateWasm selecting []
    let liveBefore = program.LiveBlocks
    let forSelected, count = allocationsOf program "selected" 0
    Assert.Equal(2, count)
    let forListed, _ = allocationsOf program "listed" 0
    // Beyond the list it selects from: the two cells it emits and the three blocks of `list.build`'s `memory` block. The
    // instance of `Emit` and the closure of its `emit` were two more.
    Assert.Equal(forListed + 5, forSelected)
    Assert.Equal(liveBefore, program.LiveBlocks)
