/// Functions that start thousands of tasks.
module Nanyx.Compiler.Tests.Features.Tasks.TaskCheckTime

open Xunit
open Nanyx.Compiler.Tests.TestSupport

// Each `start!` requires the `TaskScope` of the def it is in, which is made the def's scope where it is required, so
// the def's lambda uses the context once however many tasks it starts. When each kept a scope of its own until the
// lambda was inferred, every local def generalised looked at all of them, and a def that starts many tasks checked in
// quadratic time (#2551): 1,600 took 22 s to check in a Release build.
[<Fact; Trait("Category", "Timing")>]
let ``A function that starts 1,600 tasks checks in time that grows with its length`` () =
    checksInLinearTime 1_600 (fun n ->
        let lines = [ for i in 1..n -> $"  def t{i} = start! {i}" ] |> String.concat "\n"
        $"import nanyx/async as (Async)\n\ndef work = {{ n: int ->\n{lines}\n  n\n}}\n")
