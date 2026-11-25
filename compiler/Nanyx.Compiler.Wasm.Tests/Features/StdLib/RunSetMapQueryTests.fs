module TranspilerWasmTests.Features.StdLib.RunSetMapQuery

open Xunit
open TranspilerWasmTestHelpers

// `set` and `map` have `any`, `all` and `filterMap`, and `set` has `select` (#2191). A map's take
// the key and the value as two arguments; a set's that make a new set keep values that come out
// equal once.

/// A program whose `main` runs `lines`.
let private program (lines: string) =
    $"""@entry
def main = {{
{lines}
}}
"""
        .ReplaceLineEndings "\n"

[<Fact>]
let ``Run - set any and all test each element, and an empty set gives false and true`` () =
    let source =
        program
            """  def s = set.fromList([1, 2, 3])
  println(s \set.any { x -> x > 2 })
  println(s \set.any { x -> x > 3 })
  println(s \set.all { x -> x > 0 })
  println(s \set.all { x -> x > 1 })
  def none: set(int) = set.empty
  println(none \set.any { x -> x > 0 })
  println(none \set.all { x -> x > 0 })"""
    runSource source |> assertRan "true\nfalse\ntrue\nfalse\nfalse\ntrue\n"

[<Fact>]
let ``Run - set and map any and all stop at the first element that settles them`` () =
    let source =
        program
            """  def s = set.fromList([1, 2, 3])
  println(set.any(s, { x ->
    println("any {x}")
    x == 2
  }))
  println(set.all(s, { x ->
    println("all {x}")
    x < 2
  }))
  def m = ["a" => 1, "b" => 2, "c" => 3]
  println(map.any(m, { k, v ->
    println("any {k}")
    v == 2
  }))
  println(map.all(m, { k, v ->
    println("all {k}")
    v < 2
  }))"""
    runSource source
    |> assertRan "any 1\nany 2\ntrue\nall 1\nall 2\nfalse\nany b\ntrue\nall b\nfalse\n"

[<Fact>]
let ``Run - set filterMap keeps the some values, once each`` () =
    let source =
        program
            """  def s = set.fromList([1, 2, 3, 4, 5, 6])
  println(s \set.filterMap { x -> if x > 1 then #some(x / 2) else #skip })
  println(set.length(s \set.filterMap { x -> if x > 1 then #some(x / 2) else #skip }))
  def none: set(int) = set.empty
  println(set.isEmpty(none \set.filterMap { x -> #some(x) }))"""
    runSource source |> assertRan "[1, 2, 3]\n3\ntrue\n"

[<Fact>]
let ``Run - set select keeps the values emitted, once each`` () =
    let source =
        program
            """  def s = set.fromList([1, 2, 3])
  def selected = s \set.select { x ->
    emit(x)
    emit(x + 1)
  }
  println(selected)
  println(set.length(selected))
  def none: set(int) = set.empty
  println(set.isEmpty(none \set.select { x -> emit(x) }))"""
    runSource source |> assertRan "[1, 2, 3, 4]\n4\ntrue\n"

[<Fact>]
let ``Run - map any and all pass the key and the value, and an empty map gives false and true`` () =
    let source =
        program
            """  def m = ["a" => 1, "bb" => 2]
  println(m \map.any { k, v -> string.length(k) == v })
  println(m \map.all { k, v -> string.length(k) == v })
  println(m \map.all { k, v -> v > 1 })
  println(m \map.any { k, v -> v > 2 })
  def none: map(string, int) = []
  println(none \map.any { k, v -> v > 0 })
  println(none \map.all { k, v -> v > 0 })"""
    runSource source |> assertRan "true\ntrue\nfalse\nfalse\nfalse\ntrue\n"

[<Fact>]
let ``Run - map filterMap keeps each key whose value gives some, with the new value`` () =
    let source =
        program
            """  def m = ["a" => 1, "b" => 2, "c" => 3]
  println(m \map.filterMap { k, v -> if v == 2 then #skip else #some("{k}{v}") })
  def none: map(string, int) = []
  println(none \map.filterMap { k, v -> #some(v) })"""
    runSource source |> assertRan "[a => a1, c => c3]\n[=>]\n"
