/// Functions of thousands of calls that pass a lambda to a function with a context variable.
module Nanyx.Compiler.Tests.Features.ContextVariableCallTime

open Xunit
open Nanyx.Compiler.Tests.TestSupport

// A lambda that requires nothing, given where a function with a context variable is expected, leaves the rest made up
// for the variable's room as it is. Fitted again, it stood for a rest made up afresh, which made the def's constraints
// solved again from its start at each such call, so a def of many checked in quadratic time (#2551). 3,200 calls took
// 59 s to check in a Release build.
[<Fact>]
let ``A function of 1,600 calls that each pass a lambda to a function with a context variable checks in time that grows with its length`` () =
    checksInLinearTime 1_600 (fun n ->
        let lines = [ for i in 1..n -> $"  def t{i} = go({{ n }})" ] |> String.concat "\n"
        $"def go: [c] ([c] () -> a) -> a = {{ f -> f() }}\n\ndef work = {{ n: int ->\n{lines}\n  n\n}}\n\ndef main = work(1)\n")
