/// Inlining in the core IR (#745, see `docs/src/content/work-in-progress/inlining.md`): which calls are replaced by the
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
        normalized """import nanyx/console

def double: int -> int = { n -> n * 2 }

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
        normalized """import nanyx/console

def countdown: int -> int = { n -> if n == 0 then 0 else countdown(n - 1) }

println("{countdown(5)}")
"""
    Assert.Contains("return_call $countdown", functionText "countdown" (transpileWat source))
    runSource source |> assertRan "0\n"

[<Fact>]
let ``A function over the size limit is not inlined`` () =
    let steps = List.init 20 (fun index -> $"  def a{index + 1} = a{index} * 3 + {index}") |> String.concat "\n"
    let template =
        normalized """import nanyx/console

def long: int -> int = { a0 ->
STEPS
  a20
}

def go: int -> int = { n -> if n == 0 then long(1) else go(n - 1) }

println("{go(2)}")
"""
    let source = template.Replace("STEPS", steps)
    Assert.Contains("call $long", functionText "go" (transpileWat source))

[<Fact>]
let ``A handler and the lambda it is given are inlined`` () =
    let source =
        normalized """import nanyx/console

def apply: (int -> int), int -> int = { f, x -> f(x) }

def answer: int -> int = { n -> if n == 0 then apply({ m -> m + 1 }, 41) else answer(n - 1) }

println("{answer(2)}")
"""
    // `apply` ends in a call of its lambda parameter, so it is inlined whatever its size, and then the lambda, which is
    // called once: `answer` calls neither.
    let answer = functionText "answer" (transpileWat source)
    Assert.DoesNotContain("call $apply", answer)
    Assert.DoesNotContain("call_indirect", answer)
    runSource source |> assertRan "42\n"

[<Fact>]
let ``A tail call in inlined code stays a tail call`` () =
    let source =
        normalized """import nanyx/console

def apply: (int -> int), int -> int = { f, x -> f(x) }

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
        normalized """import nanyx/console

def broken: int -> int = { n -> n + missing }

def run: int -> int = { n -> if n == 0 then broken(1) else run(n - 1) }

println("{run(2)}")
"""
    Assert.DoesNotContain("call $broken", functionText "run" (transpileWat source))
    let outcome = runSource source
    Assert.NotEqual(0, outcome.ExitCode)
    Assert.Contains(":3:37: error NYX0101: Unknown identifier 'missing' (deferred from compile time)", outcome.StandardError)

[<Fact>]
let ``nanyx run --no-inline runs the program without inlining`` () =
    let source =
        normalized """import nanyx/console

def double: int -> int = { n -> n * 2 }

println("{double(21)}")
"""
    let path = Path.Combine(Path.GetTempPath(), $"nanyx-inline-{Guid.NewGuid():N}.nyx")
    File.WriteAllText(path, source)
    try
        nanyxWithInput "" [ "run"; "--no-inline"; path ] |> assertRan "42\n"
    finally
        File.Delete path

/// How many blocks `run(100)` allocates in `program`, and what it returns.
let private allocationsOfRun (program: WasmProgram) =
    let before = program.Allocations
    let result = program.Call("run", 100)
    program.Allocations - before, result

[<Fact>]
let ``A closure inlining leaves unread is not made`` () =
    let source =
        normalized """def apply: (int -> int), int -> int = { f, x -> f(x) }

def run: int -> int = { n -> apply({ m -> m + n }, 1) }
"""
    use inlined = instantiateWasm source []
    use notInlined = instantiateWat (transpileWatWith { checkedOptions with Inline = false } source) []
    let inlinedAllocations, inlinedResult = allocationsOfRun inlined
    let notInlinedAllocations, _ = allocationsOfRun notInlined
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
    let inlinedAllocations, inlinedResult = allocationsOfRun inlined
    let notInlinedAllocations, _ = allocationsOfRun notInlined
    Assert.Equal(101, inlinedResult)
    // Inlined, `orZero`'s match is on the `#some` made just before it, so it takes that arm, and nothing reads the tag.
    Assert.Equal(1, notInlinedAllocations)
    Assert.Equal(0, inlinedAllocations)

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
    let inlinedAllocations, inlinedResult = allocationsOfRun inlined
    let notInlinedAllocations, notInlinedResult = allocationsOfRun notInlined
    Assert.Equal(15450, inlinedResult)
    Assert.Equal(notInlinedResult, inlinedResult)
    // Not inlined, the block makes 29 allocations. Inlined, `proc`, the block, the members and the continuations are
    // inlined, and no closure is made for any of them, but the members' generic values are still boxed: 17.
    Assert.Equal(2900, notInlinedAllocations)
    Assert.Equal(1700, inlinedAllocations)
