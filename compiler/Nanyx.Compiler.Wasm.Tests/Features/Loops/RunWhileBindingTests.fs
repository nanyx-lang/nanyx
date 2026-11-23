module TranspilerWasmTests.Features.Loops.RunWhileBinding

open Xunit
open TranspilerWasmTestHelpers

// A `while` condition's `is` tests bind their names in the body on each pass, as an `if` condition's do in its `then`
// branch (#2436).

[<Fact>]
let ``Run - a while condition's is binds the pattern's names in the body, read again each time round`` () =
    let source = """
def countdown: [Console] int -> () = { n ->
  memory {
    mut state: #continue(int) | #done = #continue(n)

    while state is #continue(k) ->
      println(k)
      state := if k > 1 then #continue(k - 1) else #done
  }
}

countdown(3)
"""
    runSource (normalized source) |> assertRan "3\n2\n1\n"

[<Fact>]
let ``Run - a while condition binds through an and chain, which can read what it binds`` () =
    let source = """
def positivePrefix: [Console] list(int) -> () = { xs ->
  memory {
    mut rest = xs
    mut total = 0

    while rest is [x, ...tail] and x > 0 ->
      total := total + x
      rest := tail
    println(total)
  }
}

positivePrefix([1, 2, 3, -1, 5])
positivePrefix([])
"""
    runSource (normalized source) |> assertRan "6\n0\n"

[<Fact>]
let ``Run - a while condition binds the names of a nested pattern`` () =
    let source = """
def labelled: [Console] () -> () = {
  memory {
    mut state: #some((int, #some(string) | #none)) | #none = #some((1, #some("a")))

    while state is #some((n, #some(label))) ->
      println("{label}{n}")
      state := if n < 3 then #some((n + 1, #some(label))) else #some((n, #none))
  }
}

labelled()
"""
    runSource (normalized source) |> assertRan "a1\na2\na3\n"

[<Fact>]
let ``Run - a name a while condition binds shadows an outer one in the body only`` () =
    let source = """
def loopWith: [c] (s, ([c] (s) -> #continue(s) | #done)) -> () = { first, f ->
  memory {
    mut state: #continue(s) | #done = #continue(first)

    while state is #continue(s) ->
      state := f(s)
  }
}

def shadowing: [Console] () -> () = {
  memory {
    def k = 10
    mut state: #continue(int) | #done = #continue(2)

    while state is #continue(k) ->
      println(k)
      state := if k > 1 then #continue(k - 1) else #done
    println(k)
  }
}

loopWith(3, { n ->
  println(n)
  if n > 1 then #continue(n - 1) else #done
})
shadowing()
"""
    runSource (normalized source) |> assertRan "3\n2\n1\n2\n1\n10\n"
