module TranspilerWasmTests.Features.Contexts.RunGenericContextMember

open Xunit
open TranspilerWasmTestHelpers

/// A program whose context `Proc` has the member types `members`, which a function loads with `use` and a block calls
/// through it, as a workflow builder's are (#1152).
let private procProgram (members: string) =
    """import nanyx/console

context Proc = (
MEMBERS
)

def procH: [c] ([c] () -> #some(int) | #error(string)) -> #some(int) | #error(string) = { f ->
  use Proc(
    bind = {
      | #some(value), continue -> continue(value)
      | failure, _ -> failure
    }
    ret = { value -> #some(value) }
  ) in f()
}

def parse: int -> #some(int) | #error(string) = { n -> if n % 1000 == 999 then #error("bad") else #some(n % 97) }

def step: [Proc] int -> #some(int) | #error(string) = { i ->
  procH({ bind(parse(i), { a -> ret(a + 1) }) })
}

@entry
def main = {
  use Proc(
    bind = { _, _ -> #error("outer") }
    ret = { _ -> #error("outer") }
  )
  match step(3)
    | #some(v) -> println("{v}")
    | #error(e) -> println(e)
}
"""
        .Replace("MEMBERS", members)
        .ReplaceLineEndings "\n"

[<Fact>]
let ``Run - a context member generic over its payload, called at int, is reported at the call`` () =
    // #1152: the member's value is made once, with its own type variables unit, so the call trapped.
    let outcome =
        procProgram
            """  bind: [c] ((#some(a) | r), ([c] a -> #some(b) | r)) -> #some(b) | r
  ret: a -> #some(a) | r"""
        |> runSource
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("", outcome.StandardOutput)
    Assert.Matches(
        ":21:11: error NYX0301: The WebAssembly backend cannot compile a call to the context member 'bind' with one of its own type variables at int yet\\r?\\n$",
        outcome.StandardError)

[<Fact>]
let ``Run - the same context member written at int runs`` () =
    procProgram
        """  bind: [c] ((#some(int) | #error(string)), ([c] int -> #some(int) | #error(string))) -> #some(int) | #error(string)
  ret: int -> #some(int) | #error(string)"""
    |> runSource
    |> assertRan "4\n"
