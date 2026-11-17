module TranspilerWasmTests.Features.Workflows.RunKeywordKinds

open Xunit
open TranspilerWasmTestHelpers

/// A context whose `later` is a `#delay` keyword: `later! e` is `later({ e })`.
let private later =
    """context Later = (
  @customKeywordType(#delay)
  later: [c] ([c] () -> int) -> int
)

def computed: [Console] int -> int = { n ->
  println("computing {n}")
  n * 2
}
"""

/// `main` after `later`.
let private withLater (main: string) = normalized (later + main)

[<Fact>]
let ``Run - a #delay keyword needs no builder, and its member is passed the expression as a lambda`` () =
    // The instance prints before it calls the lambda, so `computed(21)` runs only then.
    let source =
        withLater """
@entry
def main = {
  use Later(later = { thunk ->
    println("before")
    thunk() + 1
  })
  def result = later! computed(21)
  println("{result}")
}
"""
    runSource source |> assertRan "before\ncomputing 21\n43\n"

[<Fact>]
let ``Run - a #delay keyword takes the whole expression after it, a pipe included`` () =
    // `later! computed(5) \shown` is `later({ computed(5) \shown })`: `shown` runs in the lambda.
    let source =
        withLater """
def shown: [Console] int -> int = { n ->
  println("shown {n}")
  n
}

@entry
def main = {
  use Later(later = { thunk ->
    println("before")
    thunk()
  })
  println("{later! computed(5) \shown}")
}
"""
    runSource source |> assertRan "before\ncomputing 5\nshown 10\n10\n"

[<Fact>]
let ``Run - a #delay keyword can be used in a lambda`` () =
    let source =
        withLater """
def doubled: [Later, Console] list(int) -> list(int) = { xs -> xs \list.map { x -> later! computed(x) } }

@entry
def main = {
  use Later(later = { thunk -> thunk() })
  println("{doubled([1, 2])}")
}
"""
    runSource source |> assertRan "computing 1\ncomputing 2\n[2, 4]\n"

[<Fact>]
let ``Run - a #delay keyword declared in an imported module is desugared as one`` () =
    let lazy' =
        """module lazy

export context Later = (
  @customKeywordType(#delay)
  later: [c] ([c] () -> int) -> int
)
"""
    let main =
        """module main

import lazy as (Later)

@entry
def main = {
  use Later(later = { thunk -> thunk() + 1 })
  println("{later! 6 * 7}")
}
"""
    let outcome, _ = nanyxOnPackage [ "lazy.nyx", lazy'; "main.nyx", main ] [ "run"; "{package}" ]
    assertRan "43\n" outcome

[<Fact>]
let ``Run - a #bindF defer runs its expression after the rest of the block`` () =
    // `defer! println("cleaned up")` is `defer({ println("cleaned up") }, { _ -> <the rest of the block> })`.
    let source =
        normalized """context Cleanup = (
  @customKeywordType(#bindF)
  defer: [c] (([c] () -> ()), ([c] () -> int)) -> int
  return: int -> int
)

def cleaning: [c] ([Cleanup, c] () -> int) -> int = { block ->
  use Cleanup(
    defer = { cleanUp, continue ->
      def result = continue(())
      cleanUp()
      result
    }
    return = { value -> value }
  ) in block()
}

@entry
def main = {
  def result = cleaning {
    defer! println("cleaned up")
    println("working")
    return 42
  }
  println("{result}")
}
"""
    runSource source |> assertRan "working\ncleaned up\n42\n"
