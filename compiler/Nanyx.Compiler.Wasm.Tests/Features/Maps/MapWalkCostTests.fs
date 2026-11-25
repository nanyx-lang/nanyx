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
    Assert.Equal(10000, program.Call("walkMap", 1))
    Assert.Equal(10000, program.Call("walkList", 1))
    let time (name: string) =
        let clock = System.Diagnostics.Stopwatch.StartNew()
        program.Call(name, 200) |> ignore
        clock.Elapsed.TotalMilliseconds
    // One round times the two walks back to back, in turn first, and gives the map's time over the list's. The map's
    // nodes and buckets are spread over more of memory than the list's cells, so load on the machine can slow its walk
    // more than the list's for a while: the quickest round of up to 30 counts, and the test stops at the first under
    // the bound (#2671).
    let round (index: int) =
        if index % 2 = 0 then
            let map = time "walkMap"
            map, time "walkList"
        else
            let list = time "walkList"
            time "walkMap", list
    let bound = 1.5
    let rec best (index: int) (map: float, list: float) =
        if map / list < bound || index = 30 then map, list
        else
            let next = round index
            best (index + 1) (if fst next / snd next < map / list then next else (map, list))
    let map, list = best 1 (round 0)
    Assert.True(map / list < bound, $"a walk of the map took {map:F1} ms, of the list {list:F1} ms, at best of 30 rounds")
