module Nanyx.Formatter.Tests.Features.Lambdas.Lambdas

open Xunit
open Nanyx.Formatter
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``An annotated lambda parameter is bracketed`` () =
    unchanged "def f = { (x: int) -> x + 1 }\n"
    Assert.Equal("def f = { (x: int), y -> x + y }\n", format "def f = { x: int, y -> x + y }")
    // An unbracketed annotation ends at the arrow, so the body may start with a name (#272).
    Assert.Equal("def f = { (q: Point) -> q }\n", format "def f = { q: Point -> q }")
    Assert.Equal("def f = { (g: int -> int) -> g(1) }\n", format "def f = { g: (int -> int) -> g(1) }")

[<Fact>]
let ``Pattern parameters are printed as written`` () =
    unchanged "def a = { (x, y) -> x + y }\n"
    unchanged "def b: $Email -> string = { $Email(s) -> s }\n"
    unchanged "def c = pairs \\map { (k, v), n -> k }\n"
    unchanged (lines [ "def d = { [first, ...rest] ->"; "  -- the head"; "  first"; "}" ])
    unchanged "def e = { (), n -> n }\n"
    Assert.Equal("def f = { (a, b) -> a }\n", format "def f = {(a,b)->a}")

[<Fact>]
let ``A unit parameter is printed as written`` () =
    unchanged "def a = { () -> 5 }\n"
    unchanged "def b = { () -> -1 }\n"
    unchanged (lines [ "def main = { () ->"; "  println(\"Hello, world!\")"; "}" ])
    unchanged "def c = { 5 }\n"

[<Fact>]
let ``Shorthand lambdas are printed as written`` () =
    unchanged "def a = xs \\map { * 2 }\n"
    unchanged "def b = xs \\fold(0, { + })\n"
    unchanged "def c = xs \\map { .name }\n"
    unchanged "def d = { }\n"
    unchanged "def e = { | 0 -> 1 }\n"
    unchanged (lines [ "def f = {"; "  | 0 -> 1"; "  | _ -> 2"; "}" ])
    unchanged "def g = items \\filter { % 2 == 0 }\n"
    unchanged "def h = people \\filter { .age > 18 } \\map { .name.length }\n"
    unchanged "def i = xs \\map { * 2 + 1 \\abs }\n"
    unchanged "def j = xs \\foldBack(0) { - }\n"
    unchanged "def k = { -> -1 }\n"

[<Fact>]
let ``A shorthand lambda's spacing is tidied`` () =
    Assert.Equal("def a = xs \\map { * 2 }\n", format "def a = xs \\map {   *   2   }")
    Assert.Equal("def b = { .name }\n", format "def b = {.name}")

[<Fact>]
let ``A lambda argument stays in brackets or after them, as written`` () =
    unchanged "def a = f({ x -> x })\n"
    unchanged "def b = f(1) { x -> x }\n"
    unchanged "def c = xs \\map { x -> x }\n"
    unchanged "def d = f(1, 2) { x -> x }\n"
    unchanged "def e = f(1, 2, { x -> x })\n"
    unchanged "def g = f(1, y = 2) { x -> x }\n"
    unchanged "def h = f(y = 2, 1) { x -> x }\n"
    unchanged "def i = f(1, { x -> x }, y = 2)\n"
    unchanged "def j = f((1, 2)) { x -> x }\n"

[<Fact>]
let ``An annotated def's lambda body goes on the line after the arrow`` () =
    Assert.Equal(
        lines [ "def heading: [Log] string -> () = { title ->"; "  log(\"== \" ++ string.uppercase(title) ++ \" ==\")"; "}" ],
        format "def heading: [Log] string -> () = { title -> log(\"== \" ++ string.uppercase(title) ++ \" ==\") }")
    Assert.Equal(
        lines [ "def main = {"; "  def double: int -> int = { x ->"; "    x * 2"; "  }"; ""; "  double(2)"; "}" ],
        format (lines [ "def main = {"; "  def double: int -> int = { x -> x * 2 }"; "  double(2)"; "}" ]))

[<Fact>]
let ``A lambda body that is only a name or a literal stays on the def's line`` () =
    unchanged "def id: a -> a = { x -> x }\n"
    unchanged "def zero: () -> int = { 0 }\n"
    unchanged "def none: a -> #none = { _ -> #none }\n"

[<Fact>]
let ``A def without an annotation keeps a short lambda on one line`` () =
    unchanged "def add = { a, b -> a + b }\n"

[<Fact>]
let ``A body moved to its own line keeps the operators a statement can take bracketed`` () =
    Assert.Equal(lines [ "def odd: int -> bool = { n ->"; "  (n % 2) == 1"; "}" ], format "def odd: int -> bool = { n -> n % 2 == 1 }")

[<Fact>]
let ``A lambda body that spans lines is indented, with the closing braces on different lines`` () =
    Assert.Equal(
        lines [ "def checkOrder = { products, lines -> proc {"; "    match lines"; "      | [] -> (return [])"; "      | [first, ...rest] ->"; "        def line = try parseLine(first)"; "        return [line]"; "  }"; "}" ],
        format (lines [ "def checkOrder = { products, lines -> proc {"; "  match lines"; "    | [] -> (return [])"; "    | [first, ...rest] ->"; "      def line = try parseLine(first)"; "      return [line]"; "}"; "}" ]))
    Assert.Equal(
        lines [ "def f = { x -> match x"; "    | 1 -> a"; "    | _ -> b"; "}" ],
        format (lines [ "def f = { x -> match x"; "  | 1 -> a"; "  | _ -> b"; "}" ]))
