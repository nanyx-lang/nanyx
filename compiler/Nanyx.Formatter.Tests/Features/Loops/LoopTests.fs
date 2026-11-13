module Nanyx.Formatter.Tests.Features.Loops.Loops

open Xunit
open Nanyx.Formatter
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``Loops, ranges and with are printed as written`` () =
    unchanged (lines [ "def f = { xs ->"; "  for xs"; "    | 0 -> println(\"zero\")"; "    | _ -> println(\"other\")"; "}" ])
    unchanged (lines [ "def g = {"; "  for 1..3 -> println(\"again\")"; "}" ])
    unchanged "def h = { r -> r with a = 1, b = 2 }\n"

[<Fact>]
let ``While loops are printed as written`` () =
    unchanged """memory {
  def remaining = mut 3

  while remaining > 0 ->
    println("{remaining}...")
    remaining := remaining - 1
}
"""
    unchanged "def f = { while ready() -> step() }\n"
    Assert.Equal("def f = { while ready() -> step() }\n", format "def f = {   while   ready()->step() }")

[<Fact>]
let ``Updates written in parentheses after with stay there`` () =
    unchanged """def older = user with (age = 31, name = "Bob")
"""
    unchanged "def x = (p with (x = 1)).x\n"
    Assert.Equal("def older = user with (age = 31)\n", format "def older = user with(  age=31 )")
    // They are laid out as a record's fields are: on one line where they fit, one per line where they don't.
    let nested =
        """
def updatedLine = line with (
  range = line.range with (
    start = line.range.start with (
      character = 4
    )
  )
)
"""
    let formatted =
        """
def updatedLine = line with (
  range = line.range with (start = line.range.start with (character = 4))
)
"""
    let formatted = formatted.TrimStart().ReplaceLineEndings "\n"
    Assert.Equal(formatted, format (nested.TrimStart().ReplaceLineEndings "\n"))
    unchanged formatted

[<Fact>]
let ``A for loop keeps one arm on its line and puts several on the lines after it`` () =
    unchanged "for data | item -> println(item)\n"
    unchanged (lines [ "for things | thing ->"; "  println(thing)" ])
    unchanged "for xs, 0.. | x, i -> println(\"{i}: {x}\")\n"
    unchanged "for xs | 0 | 1 -> println(\"small\")\n"
    unchanged (lines [ "def f = { xs ->"; "  for xs | x ->"; "    def y = x + 1"; "    println(y)"; "}" ])
    Assert.Equal("for data | item -> println(item)\n", format "for data\n  | item -> println(item)")
    unchanged (lines [ "for xs"; "  | 0 -> println(\"zero\")"; "  | _ -> println(\"other\")" ])

[<Fact>]
let ``Loops over several iterables and ranges without an end are printed as written`` () =
    unchanged (lines [ "def f = { xs ->"; "  for xs, 0.."; "    | 0, i -> println(\"zero at {i}\")"; "    | _, _ -> println(\"other\")"; "}" ])
    unchanged (lines [ "def g = {"; "  for 1..3, xs -> println(\"again\")"; "}" ])
    unchanged "def naturals = 0..\n"
    unchanged "def later = n + 1.. \\take(3)\n"
    Assert.Equal("def later = n + 1..\n", format "def later = (n + 1)..")
