/// A match tests its tag arms against the tag's code, read once (#2309): a value of a union with no payloads is its own
/// code, and the code of one with payloads is read into a local before the first arm, so an arm costs a comparison, and
/// the order the arms are written in hardly changes a match's speed.
module TranspilerWasmTests.Features.Matching.TagArms

open System.Text.RegularExpressions
open Xunit
open TranspilerWasmTestHelpers

/// How many times `code` calls the helper that reads a tag's code.
let private tagCodeCalls (code: string) = Regex.Matches(code, @"call \$__nanyx_tag_code\b").Count

/// A union of twelve tags, a list of them, the commonest written last and first, and a match on it with an arm for each:
/// `last` matches the commonest tag in its last arm, `first` in its first.
let private interpreter =
    normalized
        """type Op = #a | #b | #c | #d | #e | #f | #g | #h | #i | #j | #k | #l

def build: int, list(Op) -> list(Op) = { n, acc ->
  if n == 0 then acc else build(n - 1, [if n % 16 == 0 then #a else #l, ...acc])
}

def last: list(Op), int -> int = {
  | [], acc -> acc
  | [op, ...rest], acc -> match op
    | #a -> last(rest, acc + 1)
    | #b -> last(rest, acc + 2)
    | #c -> last(rest, acc + 3)
    | #d -> last(rest, acc + 4)
    | #e -> last(rest, acc + 5)
    | #f -> last(rest, acc + 6)
    | #g -> last(rest, acc + 7)
    | #h -> last(rest, acc + 8)
    | #i -> last(rest, acc + 9)
    | #j -> last(rest, acc + 10)
    | #k -> last(rest, acc + 11)
    | #l -> last(rest, acc + 12)
}

def first: list(Op), int -> int = {
  | [], acc -> acc
  | [op, ...rest], acc -> match op
    | #l -> first(rest, acc + 12)
    | #k -> first(rest, acc + 11)
    | #j -> first(rest, acc + 10)
    | #i -> first(rest, acc + 9)
    | #h -> first(rest, acc + 8)
    | #g -> first(rest, acc + 7)
    | #f -> first(rest, acc + 6)
    | #e -> first(rest, acc + 5)
    | #d -> first(rest, acc + 4)
    | #c -> first(rest, acc + 3)
    | #b -> first(rest, acc + 2)
    | #a -> first(rest, acc + 1)
}

def passesLast: list(Op), int, int -> int = { ops, n, acc ->
  if n == 0 then acc else passesLast(ops, n - 1, acc + last(ops, 0))
}

def passesFirst: list(Op), int, int -> int = { ops, n, acc ->
  if n == 0 then acc else passesFirst(ops, n - 1, acc + first(ops, 0))
}

def runLast: int -> int = { passes -> passesLast(build(100000, []), passes, 0) }

def runFirst: int -> int = { passes -> passesFirst(build(100000, []), passes, 0) }
"""

[<Fact>]
let ``A match on a union with no payloads compares each arm's tag with the value itself`` () =
    let wat = transpileWat interpreter
    Assert.Equal(0, tagCodeCalls (functionText "last" wat))

[<Fact>]
let ``A match on a union with payloads reads the tag's code once`` () =
    let source =
        normalized
            """type Token = #push(int) | #add | #sub | #mul | #neg | #dup

def step: Token, int -> int = { token, top ->
  match token
  | #push(n) -> n
  | #add -> top + 1
  | #sub -> top - 1
  | #mul -> top * 2
  | #neg -> 0 - top
  | #dup -> top
}

println(step(#dup, 3) + step(#push(4), 0))
"""
    let wat = transpileWatNotInlined source
    Assert.Equal(1, tagCodeCalls (functionText "step" wat))
    runSource source |> assertRan "7\n"

[<Fact>]
let ``A match takes about as long whichever arm its commonest tag is in`` () =
    use program = instantiateWasm interpreter []
    let time (name: string) =
        Assert.Equal(program.Call(name, 1), program.Call(name, 1))
        let clock = System.Diagnostics.Stopwatch.StartNew()
        program.Call(name, 40) |> ignore
        clock.Elapsed.TotalMilliseconds
    // With a call per arm, the commonest tag in the last arm took over three times as long.
    let holds (last: float) (first: float) = last / first < 2.0
    let last, first = time "runLast", time "runFirst"
    // When the first runs miss, each is run again in turn, and each one's quickest run counts, so that load on the
    // machine, which slows one run and not the other, can't make the test fail (#2388).
    let last, first =
        if holds last first then last, first
        else
            let again = List.init 5 (fun _ -> time "runLast", time "runFirst")
            min last (again |> List.map fst |> List.min), min first (again |> List.map snd |> List.min)
    Assert.True(holds last first, $"the commonest tag in the last arm took {last:F1} ms, in the first {first:F1} ms")
