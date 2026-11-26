/// Inlining in the core IR (#745, see `docs/src/content/deep-dives/inlining.md`): which calls are replaced by the
/// body of the function they call, and that the inlined code does what the call did. The functions the tests look into
/// call themselves, so that they are not inlined in turn: a function inlined wherever it is called is not emitted.
module TranspilerWasmTests.Features.Core.Inlining

open System
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``A small function is inlined into its caller`` () =
    let source =
        normalized """def double: int -> int = { n -> n * 2 }

def sum: int, int -> int = { n, acc -> if n == 0 then acc else sum(n - 1, acc + double(n)) }

println("{sum(3, 0)}")
"""
    let wat = transpileWat source
    Assert.DoesNotContain("call $double", functionText "sum" wat)
    Assert.DoesNotContain("(func $double ", wat)
    Assert.Contains("call $double", functionText "sum" (transpileWatNotInlined source))
    runSource source |> assertRan "12\n"

[<Fact>]
let ``A recursive function is not inlined`` () =
    let source =
        normalized """def countdown: int -> int = { n -> if n == 0 then 0 else countdown(n - 1) }

println("{countdown(5)}")
"""
    Assert.Contains("return_call $countdown", functionText "countdown" (transpileWat source))
    runSource source |> assertRan "0\n"

[<Fact>]
let ``A function over the size limit is not inlined`` () =
    let steps = List.init 20 (fun index -> $"  def a{index + 1} = a{index} * 3 + {index}") |> String.concat "\n"
    let template =
        normalized """def long: int -> int = { a0 ->
STEPS
  a20
}

def go: int -> int = { n -> if n == 0 then long(1) + long(2) else go(n - 1) }

println("{go(2)}")
"""
    let source = template.Replace("STEPS", steps)
    Assert.Contains("call $long", functionText "go" (transpileWat source))

[<Fact>]
let ``A function over the size limit that the module calls once is inlined`` () =
    // Its body only moves, so nothing grows (#1883).
    let steps = List.init 20 (fun index -> $"  def a{index + 1} = a{index} * 3 + {index}") |> String.concat "\n"
    let template =
        normalized """def long: int -> int = { a0 ->
STEPS
  a20
}

def go: int -> int = { n -> if n == 0 then long(1) else go(n - 1) }

println("{go(2)}")
"""
    let wat = transpileWat (template.Replace("STEPS", steps))
    Assert.DoesNotContain("call $long", functionText "go" wat)
    Assert.DoesNotContain("(func $long", wat)

[<Fact>]
let ``A handler and the lambda it is given are inlined`` () =
    let source =
        normalized """def apply: (int -> int), int -> int = { f, x -> f(x) }

def answer: int -> int = { n -> if n == 0 then apply({ m -> m + 1 }, 41) else answer(n - 1) }

println("{answer(2)}")
"""
    // `apply` ends in a call of its lambda parameter and is called once, so it is inlined whatever its size, and then
    // the lambda, which is called once: `answer` calls neither.
    let answer = functionText "answer" (transpileWat source)
    Assert.DoesNotContain("call $apply", answer)
    Assert.DoesNotContain("call_indirect", answer)
    runSource source |> assertRan "42\n"

/// A chain of `levels` functions after `g0`, each printing and calling the one before twice, and a call of the last.
/// Each is a handler, since it ends in a call through the `Console` member it is given.
let private doublingChain (levels: int) =
    let level (index: int) =
        $"""def g{index}: [Console] int -> () = {{ x ->
  println("step")
  g{index - 1}(x + 1)
  g{index - 1}(x + 2)
}}
"""
    let definitions = List.init levels (fun index -> level (index + 1)) |> String.concat "\n"
    normalized $"""def g0: [Console] int -> () = {{ x -> println("step {{x}}") }}

{definitions}
g{levels}(0)
"""

[<Fact>]
let ``A handler called twice at every level is not copied twice at every level`` () =
    // Inlined wherever it was called whatever its size, each level's body held two copies of the level below's, up to
    // the growth limit: 1.2 MB of WAT for twenty levels (#1664).
    Assert.True((transpileWat (doublingChain 20)).Length < 150_000, "the module should grow with the chain, not double with each level")
    runSource (doublingChain 2) |> assertRan "step\nstep\nstep 2\nstep 3\nstep\nstep 3\nstep 4\n"

[<Fact>]
let ``A tail call in inlined code stays a tail call`` () =
    let source =
        normalized """def apply: (int -> int), int -> int = { f, x -> f(x) }

def loop: int, int -> int = { n, acc ->
  if n == 0 then acc else apply({ m -> loop(m, acc + 1) }, n - 1)
}

@entry
def main = {
  println("{loop(1000000, 0)}")
}
"""
    // `apply` is inlined into `loop`, and its call of the lambda, which calls `loop`, is a direct call and still the last
    // thing `loop` does: a million of them would overflow the stack if each were an ordinary call. The lambda calls
    // `loop`, so it is not inlined.
    let loop = functionText "loop" (transpileWat source)
    Assert.DoesNotContain("call $apply", loop)
    Assert.DoesNotContain("call_indirect", loop)
    Assert.Contains("return_call $__lambda", loop)
    runSource source |> assertRan "1000000\n"

[<Fact>]
let ``A trap in inlined code reports the line it is written on`` () =
    let source =
        normalized """def broken: int -> int = { n -> n + missing }

def run: int -> int = { n -> if n == 0 then broken(1) else run(n - 1) }

println("{run(2)}")
"""
    Assert.DoesNotContain("call $broken", functionText "run" (transpileWat source))
    let outcome = runSource source
    Assert.NotEqual(0, outcome.ExitCode)
    Assert.Contains(":1:37: error NYX0101: Unknown identifier 'missing': 'missing' is not in scope (deferred from compile time)", outcome.StandardError)

[<Fact>]
let ``nanyx run --no-inline runs the program without inlining`` () =
    let source =
        normalized """def double: int -> int = { n -> n * 2 }

println("{double(21)}")
"""
    let path = Path.Combine(Path.GetTempPath(), $"nanyx-inline-{Guid.NewGuid():N}.nyx")
    File.WriteAllText(path, source)
    try
        nanyxWithInput "" [ "run"; "--no-inline"; path ] |> assertRan "42\n"
    finally
        File.Delete path

[<Fact>]
let ``A closure inlining leaves unread is not made`` () =
    let source =
        normalized """def apply: (int -> int), int -> int = { f, x -> f(x) }

def run: int -> int = { n -> apply({ m -> m + n }, 1) }
"""
    use inlined = instantiateWasm source []
    use notInlined = instantiateWat (transpileWatWith { checkedOptions with Inline = false } source) []
    let inlinedAllocations, inlinedResult = allocationsOf inlined "run" 100
    let notInlinedAllocations, _ = allocationsOf notInlined "run" 100
    Assert.Equal(101, inlinedResult)
    // The lambda captures `n`, so it is a closure block. Inlined, its body reads `n` from the closure it is called
    // through, which is known, and then nothing reads the closure.
    Assert.Equal(1, notInlinedAllocations)
    Assert.Equal(0, inlinedAllocations)

[<Fact>]
let ``A match on a tag made where it is known takes its arm`` () =
    let source =
        normalized """def orZero: (#some(int) | #none) -> int = { option ->
  match option
    | #some(value) -> value
    | #none -> 0
}

def run: int -> int = { n -> orZero(#some(n + 1)) }
"""
    use inlined = instantiateWasm source []
    use notInlined = instantiateWat (transpileWatWith { checkedOptions with Inline = false } source) []
    let inlinedAllocations, inlinedResult = allocationsOf inlined "run" 100
    let notInlinedAllocations, _ = allocationsOf notInlined "run" 100
    Assert.Equal(101, inlinedResult)
    // Inlined, `orZero`'s match is on the `#some` made just before it, so it takes that arm, and nothing reads the tag.
    Assert.Equal(1, notInlinedAllocations)
    Assert.Equal(0, inlinedAllocations)

[<Fact>]
let ``A match on a variable given another's value, given another's, reads the first`` () =
    let source =
        normalized """def wrap: int -> #some(int) | #none = { n -> #some(n) }

def run: int -> int = { n ->
  def made: #some(int) | #none = #some(n + 1)
  def given = made
  def givenAgain = given
  match givenAgain
    | #some(value) -> value
    | #none -> 0
}
"""
    use inlined = instantiateWasm source []
    let allocations, result = allocationsOf inlined "run" 100
    Assert.Equal(101, result)
    // `givenAgain` is read as `given`, and `given` as `made`, so the match is on the tag made just before it: it takes
    // that arm, and nothing reads the tag. (`wrap` allocates, so that the module has an allocator to count with.)
    Assert.Equal(0, allocations)

[<Fact>]
let ``A match on what an if gives is copied into its branches`` () =
    let source =
        normalized """def classify: int -> #positive(int) | #other = { n -> if n > 0 then #positive(n) else #other }

def run: int -> int = { n ->
  match classify(n)
    | #positive(value) -> value
    | #other -> 0
}
"""
    use inlined = instantiateWasm source []
    use notInlined = instantiateWat (transpileWatWith { checkedOptions with Inline = false } source) []
    let inlinedAllocations, inlinedResult = allocationsOf inlined "run" 100
    let notInlinedAllocations, _ = allocationsOf notInlined "run" 100
    Assert.Equal(100, inlinedResult)
    // Inlined, the match is on the tag `classify`'s `if` gives. Copied into each branch, it is on a known tag there, so
    // it takes its arm, and no tag is made.
    Assert.Equal(1, notInlinedAllocations)
    Assert.Equal(0, inlinedAllocations)

[<Fact>]
let ``A function whose declared result has tags its body never makes is inlined`` () =
    let source =
        normalized """def half: int -> #some(int) | #error(#odd | #negative) = { n ->
  if n % 2 == 1 then #error(#odd) else #some(n // 2)
}

def run: int -> int = { n ->
  match half(n)
    | #some(h) -> h
    | #error(_) -> 0
}
"""
    Assert.DoesNotContain("call $half", functionText "run" (transpileWat source))
    use inlined = instantiateWasm source []
    let allocations, result = allocationsOf inlined "run" 100
    Assert.Equal(50, result)
    // `half` makes only `#odd` of the two errors it declares. Inlined all the same, its `if` meets the match, and no tag
    // is made (#1882).
    Assert.Equal(0, allocations)

[<Fact>]
let ``A proc block compiles to the nested match written by hand`` () =
    let source =
        normalized """def getA: int -> #some(int) | #none = { n -> if n > 0 then #some(n) else #none }

def byProc: int -> #some(int) | #none = { n ->
  proc {
    def a = try getA(n)
    def b = try getA(n + 1)
    def c = try getA(n + 2)
    return a + b + c
  }
}

def byHand: int -> #some(int) | #none = { n ->
  match getA(n)
    | #some(a) ->
      match getA(n + 1)
        | #some(b) ->
          match getA(n + 2)
            | #some(c) -> #some(a + b + c)
            | other -> other
        | other -> other
    | other -> other
}
"""
    // `proc`, its block, the members, the continuations and `getA` are inlined, nothing is boxed, and what is left is
    // the code of the nested match: the same instructions, on locals named differently. The two are in one program, so
    // their tags are numbered alike.
    let wat = transpileWat source
    let block name = (functionText name wat |> withLocalsNumbered).Replace($"${name}", "$block")
    Assert.Equal(block "byHand", block "byProc")

[<Fact>]
let ``A proc block allocates fewer inlined`` () =
    let source =
        normalized """def getA: int -> #some(int) | #none = { n -> if n > 0 then #some(n) else #none }

def block: int -> #some(int) | #none = { n ->
  proc {
    def a = try getA(n)
    def b = try getA(n + 1)
    def c = try getA(n + 2)
    return a + b + c
  }
}

def go: int, int -> int = { i, acc ->
  if i == 0 then acc
  else
    match block(i)
      | #some(v) -> go(i - 1, acc + v)
      | #none -> go(i - 1, acc)
}

def run: int -> int = { n -> go(n, 0) }
"""
    use inlined = instantiateWasm source []
    use notInlined = instantiateWat (transpileWatWith { checkedOptions with Inline = false } source) []
    let inlinedAllocations, inlinedResult = allocationsOf inlined "run" 100
    let notInlinedAllocations, notInlinedResult = allocationsOf notInlined "run" 100
    Assert.Equal(15450, inlinedResult)
    Assert.Equal(notInlinedResult, inlinedResult)
    // Not inlined, the block makes 29 allocations. Inlined, `proc`, the block, the members and the continuations are
    // inlined, no closure is made for any of them, and each `try`'s match is copied into both branches of `getA`'s `if`,
    // where the payload is known, so nothing is boxed. The block is inlined into `go` as well, whose match on the
    // `#some` it returns is copied in the same way, so not even that is made.
    Assert.Equal(2900, notInlinedAllocations)
    Assert.Equal(0, inlinedAllocations)
    let go = functionText "go" (transpileWat source)
    Assert.DoesNotContain("$__box_", go)
    Assert.DoesNotContain("call_indirect", go)
