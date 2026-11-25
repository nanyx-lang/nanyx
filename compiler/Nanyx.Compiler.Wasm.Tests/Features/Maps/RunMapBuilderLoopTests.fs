module TranspilerWasmTests.Features.Maps.RunMapBuilderLoop

open Xunit
open TranspilerWasmTestHelpers

// A `for` loop over a map or a set whose body uses a keyword is run by the builder's `for`, which takes a list: the map
// or set is read as the list `map.toList` or `set.toList` makes of it (#2672).

[<Fact>]
let ``Run - a proc returns from inside a for over a map or a set, mid-walk in a deep trie, and frees everything`` () =
    let source =
        """
def find: map(int, string), int -> #some(string) | #none = { m, wanted ->
  proc {
    for m | (k, v) ->
      if k == wanted then
        return v
    return "none"
  }
}

def findIn: set(int), int -> #some(int) | #none = { s, wanted ->
  proc {
    for s | n ->
      if n == wanted then
        return n
    return -1
  }
}

-- keys that share their low ten bits, so the trie is three levels deep
def deep = (0..300) \seq.map { i -> (i * 1024, "v{i}") } \seq.toList \map.fromList
def deepSet = (0..300) \seq.map { i -> i * 1024 } \seq.toList \set.fromList

println(find(deep, 150 * 1024))
println(find(deep, 7))
println(findIn(deepSet, 150 * 1024))
println(findIn(deepSet, 7))
"""
    runSourceWithOptions true true "" source |> assertRan "#some(v150)\n#some(none)\n#some(153600)\n#some(-1)\n"

[<Fact>]
let ``Run - a for over a map in a proc keeps the rewrites in its body, as a map called as a lookup`` () =
    // The body's own rewrites, here a map called as a lookup, are kept when the loop is read as a call of `for`.
    let source =
        """
def names = [1 => "one", 2 => "two"]

def firstNamed: map(int, int) -> #some(string) | #none = { m ->
  proc {
    for m | (k, v) ->
      match names(v)
        | #some(name) -> return "{k}:{name}"
        | #notFound -> ()
    return "none"
  }
}

println(firstNamed([10 => 3, 20 => 2]))
println(firstNamed([10 => 3]))
"""
    runSourceWithOptions true true "" source |> assertRan "#some(20:two)\n#some(none)\n"
