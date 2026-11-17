/// Associated types (see `AssociatedTypes`): a generic function over a context with one is specialised at each instance,
/// whose associated type its members fix, though the function's type need not write it.
module TranspilerWasmTests.Features.Contexts.RunAssociatedTypes

open Xunit
open TranspilerWasmTestHelpers

let private iter =
    """context Iter(c) = (elem: type, next: c -> #some(elem, c) | #done)

def headOr: [Iter(c)] (c, elem) -> elem = { items, fallback ->
  match next(items)
    | #some(value, _) -> value
    | #done -> fallback
}

def countAll: [Iter(c)] c -> int = { items ->
  match next(items)
    | #some(_, rest) -> 1 + countAll(rest)
    | #done -> 0
}

type Bag = protected (items: list(int))

def Bag.next: Bag -> #some(int, Bag) | #done = { bag ->
  match bag.items
    | [] -> #done
    | [first, ...rest] -> #some(first, Bag(items = rest))
}

type Words = protected (items: list(string))
"""

[<Fact>]
let ``Run - a function over a context with an associated type runs at an instance built from attached definitions`` () =
    let source =
        iter
        + """
println(headOr(Bag(items = [3, 4]), 0) + 1)
println(countAll(Bag(items = [3, 4, 5])))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "4\n3\n"

[<Fact>]
let ``Run - one function runs at two instances whose associated types differ, one from a use`` () =
    let source =
        iter
        + """
def firstWord: Words -> string = { words ->
  use Iter(Words)(next = { w ->
    match w.items
      | [] -> #done
      | [first, ...rest] -> #some(first, Words(items = rest))
  })
  headOr(words, "none") ++ " of {countAll(words)}"
}

println(headOr(Bag(items = [7]), 0))
println(firstWord(Words(items = ["hi", "there"])))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "7\nhi of 2\n"

[<Fact>]
let ``Run - a function over a context with an associated type is passed a member that captures a local`` () =
    let source =
        iter
        + """
def suffixed: Words, string -> string = { words, suffix ->
  use Iter(Words)(next = { w ->
    match w.items
      | [] -> #done
      | [first, ...rest] -> #some(first ++ suffix, Words(items = rest))
  })
  headOr(words, "none") ++ " of {countAll(words)}"
}

println(suffixed(Words(items = ["hi", "there"]), "!"))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "hi! of 2\n"

[<Fact>]
let ``Run - a constraint that fixes an associated type reads values of it`` () =
    let source =
        iter
        + """
def Words.next: Words -> #some(string, Words) | #done = { w ->
  match w.items
    | [] -> #done
    | [first, ...rest] -> #some(first, Words(items = rest))
}

def joinAll: [Iter(c, elem = string)] c -> string = { items ->
  match next(items)
    | #some(item, rest) -> item ++ joinAll(rest)
    | #done -> ""
}

println(joinAll(Words(items = ["a", "b", "c"])))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "abc\n"

[<Fact>]
let ``Run - two contexts' associated types are named by their contexts`` () =
    let source =
        iter
        + """
def Words.next: Words -> #some(string, Words) | #done = { w ->
  match w.items
    | [] -> #done
    | [first, ...rest] -> #some(first, Words(items = rest))
}

def firstPair: [Iter(c), Iter(d)] (c, d) -> #some((Iter(c).elem, Iter(d).elem)) | #none = { xs, ys ->
  match Iter(c).next(xs), Iter(d).next(ys)
    | #some(x, _), #some(y, _) -> #some((x, y))
    | _, _ -> #none
}

match firstPair(Bag(items = [1]), Words(items = ["z"]))
  | #some((n, s)) -> println("{n} {s}")
  | #none -> println("none")
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "1 z\n"

[<Fact>]
let ``Run - an attached alias fixes an associated type no member mentions`` () =
    let source =
        """context Sized(c) = (elem: type, size: c -> int)

type Bag = protected (items: list(int))

def Bag.size: Bag -> int = { bag -> bag.items.length }

type Bag.elem = int

def total: [Sized(c)] c -> int = { xs -> size(xs) }

println(total(Bag(items = [1, 2])))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "2\n"

[<Fact>]
let ``Run - an associated type is passed as a type argument, qualified or bare`` () =
    let source =
        """context Named(a) = (nameOf: Type(a) -> string)

def int.nameOf: Type(int) -> string = { t -> "int" }

def string.nameOf: Type(string) -> string = { t -> "string" }

def describe: [Named(a)] type a -> string = { t -> nameOf(t) }

def describeElements: [Iterable(c), Named(elem)] c -> string = { xs -> describe(type Iterable(c).elem) }

def ofElements: [Iterable(c), Named(elem)] c -> string = { xs -> "of " ++ describe(type elem) }

println(describeElements([1, 2]))
println(ofElements(["a"]))
println(describeElements(1..3))
"""
    runSource source |> assertRan "int\nof string\nint\n"
