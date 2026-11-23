/// Functions of thousands of lines, which the type checker generalises a local def of at each line.
module TranspilerWasmTests.Features.Core.RunLongFunctions

open Xunit
open TranspilerWasmTestHelpers

// An operator on a parameter of a function that writes no type is left until the function is inferred, and so is the
// literal it adds, which nothing settles until then. Each local def looks only at the variables the ones left so far
// are of, and at those of the names in scope, each once, so such a function checks in time that grows with its length
// (#2407). 8,000 lines took 34 s to check in a Release build when each local def looked at every one before it.
[<Fact>]
let ``A function of 8,000 additions to a parameter whose type it does not write compiles and runs in time that grows with its length`` () =
    let source n =
        let lines = [ for i in 1..n -> $"  def x{i} = x0 + 1" ] |> String.concat "\n"
        $"def run = {{ x0 ->\n{lines}\n  x{n}\n}}\n\nprintln(run(0))\n"
    runsInLinearTime 8_000 source (fun _ -> "1\n")
