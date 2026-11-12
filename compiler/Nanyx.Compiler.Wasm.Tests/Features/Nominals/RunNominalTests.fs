module TranspilerWasmTests.Features.Nominals.Run

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - two modules' nominal types of the same name each keep their own shape and attached definitions`` () =
    let people =
        """
module people

export type $Person = (name: string)

export def $Person.new: string -> $Person = { name -> $Person(name = name) }

export def $Person.describe: $Person -> string = { p -> "person {p.name}" }

export def everyone: list($Person) = [ $Person(name = "Ada"), $Person(name = "Bo") ]
"""
    let main =
        """
module main

import people as p

type $Person = (age: int, years: int)

def $Person.describe: $Person -> string = { q -> "age {q.age}, {q.years} years" }

def same = { a, b -> a == b }

@entry
def main = {
  def theirs = p.$Person.new("Ada")
  def mine: $Person = $Person(age = 36, years = 2)
  println(theirs \p.$Person.describe)
  println(mine \describe)
  println("{same(theirs, p.$Person.new("Ada"))} {same(mine, $Person(age = 1, years = 2))}")
  println("{p.everyone \list.length}")
}
"""
    let outcome, _ = nanyxOnPackage [ "people.nyx", people; "main.nyx", main ] [ "run"; "{package}" ]
    assertRan "person Ada\nage 36, 2 years\ntrue false\n2\n" outcome
