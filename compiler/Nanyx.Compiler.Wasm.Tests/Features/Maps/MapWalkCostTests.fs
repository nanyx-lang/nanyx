module TranspilerWasmTests.Features.Maps.MapWalkCost

open Xunit
open TranspilerWasmTestHelpers

// A `for` loop over a map walks its trie in place, reading each bucket straight from its node's array (#2671).

let private walks =
    normalized
        """def entries = (0..9999) \seq.map { i -> (i, 1) } \seq.toList

def m = map.fromList(entries)

def walkMap: int -> int = { passes ->
  memory {
    mut total = 0
    mut pass = 0
    while pass < passes ->
      for m | (_, v) -> total := total + v
      pass := pass + 1
    total
  }
}

def walkList: int -> int = { passes ->
  memory {
    mut total = 0
    mut pass = 0
    while pass < passes ->
      for entries | (_, v) -> total := total + v
      pass := pass + 1
    total
  }
}
"""

[<Fact>]
let ``A for loop over a 10,000-key map takes under one and a half times as long as one over a list of its entries`` () =
    use program = instantiateWasm walks []
    let time (name: string) =
        Assert.Equal(10000, program.Call(name, 1))
        let clock = System.Diagnostics.Stopwatch.StartNew()
        program.Call(name, 500) |> ignore
        clock.Elapsed.TotalMilliseconds
    let holds (map: float) (list: float) = map / list < 1.5
    let map, list = time "walkMap", time "walkList"
    // When the first runs miss, each is run again in turn, and each one's quickest run counts, so that load on the
    // machine, which slows one run and not the other, can't make the test fail (#2388).
    let map, list =
        if holds map list then map, list
        else
            let again = List.init 5 (fun _ -> time "walkMap", time "walkList")
            min map (again |> List.map fst |> List.min), min list (again |> List.map snd |> List.min)
    Assert.True(holds map list, $"a walk of the map took {map:F1} ms, of the list {list:F1} ms")
