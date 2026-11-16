module TranspilerWasmTests.Features.Nominals.RunRecursiveNominal

open Xunit
open TranspilerWasmTestHelpers

// A protected or private type that names itself in its definition (#1663). Each program runs with its memory checked,
// so a value that is not freed, or freed twice, fails the test.

let private run (source: string) = runSource (source.ReplaceLineEndings "\n")

[<Fact>]
let ``Run - a protected type that names itself in a field is built and recursed over`` () =
    run """
type Node = protected (value: int, rest: #some(Node) | #none)

def n = Node(value = 1, rest = #some(Node(value = 2, rest = #some(Node(value = 3, rest = #none)))))

def sum: Node -> int = { node ->
  match node.rest
    | #some(next) -> node.value + sum(next)
    | #none -> node.value
}

println(sum(n))
"""
    |> assertRan "6\n"

[<Fact>]
let ``Run - a generic protected type whose function gives the type again is pulled to its end`` () =
    run """
type Stream(a) = protected (next: () -> #next(a, Stream(a)) | #done)

def u = Stream(next = { #next(1, Stream(next = { #next(2, Stream(next = { #done })) })) })

def total: Stream(int) -> int = { s ->
  match s.next()
    | #next(x, rest) -> x + total(rest)
    | #done -> 0
}

println(total(u))
"""
    |> assertRan "3\n"

[<Fact>]
let ``Run - the rest of a value of a type generic in contexts requires the same contexts`` () =
    run """
type Stream(a) = [k] protected (next: [k] () -> #next(a, Stream(a)) | #done)

def loud: [Console] Stream(int) = Stream(next = {
  println("one")
  #next(1, Stream(next = {
    println("two")
    #done
  }))
})

def ones: Stream(int) = Stream(next = { #next(1, Stream(next = { #next(2, Stream(next = { #done })) })) })

def total: [c] ([c] Stream(int)) -> int = { s ->
  match s.next()
    | #next(x, rest) -> x + total(rest)
    | #done -> 0
}

def totalLoud: [Console] ([Console] Stream(int)) -> int = { s ->
  match s.next()
    | #next(x, rest) -> x + totalLoud(rest)
    | #done -> 0
}

println(total(loud))
println(total(ones))
println(totalLoud(loud))
"""
    |> assertRan "one\ntwo\n1\n3\none\ntwo\n1\n"
