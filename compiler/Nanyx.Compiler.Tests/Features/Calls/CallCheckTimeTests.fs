/// Functions of thousands of calls, each checked in time that doesn't grow with the calls before it.
module Nanyx.Compiler.Tests.Features.CallCheckTime

open Xunit
open Nanyx.Compiler.Tests.TestSupport

// A call of several arguments works out which of them a mismatch would be reported at, taking a number literal among
// them to be of its default type (see `LambdaArguments.reportedSpan`). That is worked out only when a mismatch is
// reported, so a def of many such calls, each with a literal nothing settles until the def is generalised, checks in
// time that grows with its length (#2552). 16,000 calls took 67 s to check in a Release build when each call looked at
// every literal before it.
[<Fact; Trait("Category", "Timing")>]
let ``A function of 6,000 calls with a literal among two arguments checks in time that grows with its length`` () =
    checksInLinearTime 6_000 (fun n ->
        let lines = [ for i in 1..n -> $"  def a{i} = pair(x, {i})" ] |> String.concat "\n"
        $"def pair: (a, b) -> (a, b) = {{ a, b -> (a, b) }}\n\ndef work: int -> int = {{ x ->\n{lines}\n  x\n}}\n\ndef main = work(1)\n")
