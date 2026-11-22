module TranspilerWasmTests.Features.Nominals.Run

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - two modules' nominal types of the same name each keep their own shape and attached definitions`` () =
    let people =
        """
module people

export type Person = protected (name: string)

export def Person.new: string -> Person = { name -> Person(name = name) }

export def Person.describe: Person -> string = { p -> "person {p.name}" }

export def everyone: list(Person) = [ Person(name = "Ada"), Person(name = "Bo") ]
"""
    let main =
        """
module main

import people as p

type Person = protected (age: int, years: int)

def Person.describe: Person -> string = { q -> "age {q.age}, {q.years} years" }

def same = { a, b -> a == b }

@entry
def main = {
  def theirs = p.Person.new("Ada")
  def mine: Person = Person(age = 36, years = 2)
  println(theirs \p.Person.describe)
  println(mine \describe)
  println("{same(theirs, p.Person.new("Ada"))} {same(mine, Person(age = 1, years = 2))}")
  println("{p.everyone \list.length}")
}
"""
    let outcome, _ = nanyxOnPackage [ "people.nyx", people; "main.nyx", main ] [ "run"; "{package}" ]
    assertRan "person Ada\nage 36, 2 years\ntrue false\n2\n" outcome

[<Fact>]
let ``Run - a generic protected type written with its arguments holds what its constructor was given`` () =
    let source =
        """
type Box(a) = protected (v: a)
type Shelf = (left: Box(int), right: Box(string))

def get: Box(int) -> int = { x -> x.v }
def label: Box(string) -> string = { Box(r) -> r.v }
def shelf: Shelf = (left = Box(v = 2), right = Box(v = "two"))

println("{get(shelf.left) + 1} {label(shelf.right)}")
"""
    assertRan "3 two\n" (runSource (normalized source))
[<Fact>]
let ``Run - a value of a type in a scoped context's scope is used inside the call that supplies the context`` () =
    let source =
        """
scoped context Tx = private ()

type Cursor = [Tx] protected (query: string, row: int)

def transaction: [c] ([Tx, c] () -> a) -> a = { block -> use Tx() in block() }
def open: [Tx] string -> Cursor = { query -> Cursor(query = query, row = 41) }
def next: [Tx] Cursor -> int = { c -> c.row + 1 }

def row = transaction {
  def c = open("select")
  next(c)
}

println(row)
"""
    assertRan "42\n" (runSource (normalized source))
[<Fact>]
let ``Run - a private type's accessor takes it apart, at a file's top level and in a module block`` () =
    let accessor = "def UserId.value: UserId -> int = { UserId(n) -> n }"
    let topLevel =
        normalized ($"type UserId = private int\n{accessor}\n\n" + "println(UserId.value(UserId(3)))\n")
    runSource topLevel |> assertRan "3\n"
    let inBlock =
        normalized (
            $"module ids =\n  export type UserId = private int\n  export def UserId.new: int -> UserId = {{ n -> UserId(n) }}\n  export {accessor}\n  export def UserId.next: UserId -> int = {{ u ->\n    def UserId(n) = u\n    n + 1\n  }}\n\n"
            + "println(ids.UserId.value(ids.UserId.new(3)))\nprintln(ids.UserId.next(ids.UserId.new(3)))\n")
    runSource inBlock |> assertRan "3\n4\n"
