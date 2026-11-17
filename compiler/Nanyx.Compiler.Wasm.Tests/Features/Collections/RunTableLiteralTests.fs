/// Table literals (#1255): a list of records written as a header and rows.
module TranspilerWasmTests.Features.Collections.Tables

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a table literal is a list of its rows' records`` () =
    runSource
        """
def namesAndAges =
  [| name     | age |
   | "Alex"   | 22  |
   | "Taylor" | 30  |
   | "Sam"    | 28  |
   | "Morgan" | 45  |]

for namesAndAges | person -> println("{person.name} is {person.age}")
println(list.length(namesAndAges))
"""
    |> assertRan "Alex is 22\nTaylor is 30\nSam is 28\nMorgan is 45\n4\n"
