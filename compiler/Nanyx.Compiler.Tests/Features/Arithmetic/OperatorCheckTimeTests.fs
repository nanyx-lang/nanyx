/// Functions of thousands of lines of arithmetic, which the type checker generalises a local def of at each line.
module Nanyx.Compiler.Tests.Features.OperatorCheckTime

open Xunit
open Nanyx.Compiler.Tests.TestSupport

// An operator on a parameter of a function that writes no type is left until the function is inferred, and so is the
// literal it adds, which nothing settles until then. Each local def looks only at the variables the ones left so far
// are of, and at each variable of the names in scope once, so such a function checks in time that grows with its length
// (#2407). 8,000 lines took 34 s to check in a Release build when each local def looked at every one before it.
[<Fact; Trait("Category", "Timing")>]
let ``A function of 8,000 additions to a parameter whose type it does not write checks in time that grows with its length`` () =
    checksInLinearTime 8_000 (fun n ->
        let lines = [ for i in 1..n -> $"  def x{i} = x0 + 1" ] |> String.concat "\n"
        $"def run = {{ x0 ->\n{lines}\n  x{n}\n}}\n\ndef main = run(0)\n")
