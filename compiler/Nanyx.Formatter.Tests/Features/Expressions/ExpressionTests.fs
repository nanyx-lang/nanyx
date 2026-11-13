module Nanyx.Formatter.Tests.Features.Expressions.Expressions

open Xunit
open Nanyx.Formatter
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``Spacing is made regular`` () =
    Assert.Equal("def add = { a, b -> a + b }\n", format "def add={a,b->a +  b}")

[<Fact>]
let ``Parentheses are kept where precedence needs them, and only there`` () =
    Assert.Equal("def a = (b + c) * d\n", format "def a = ((b + c) * d)")
    Assert.Equal("def a = b - (c - d)\n", format "def a = b - (c - d)")
    Assert.Equal("def a = b - c - d\n", format "def a = (b - c) - d")
    Assert.Equal("def a = b ++ c ++ d\n", format "def a = b ++ (c ++ d)")
    Assert.Equal("def a = (b ++ c) ++ d\n", format "def a = (b ++ c) ++ d")
    unchanged "def a = not b == c\n"
    unchanged "def a = not (b == c)\n"

[<Fact>]
let ``Prefix minus is written against its operand`` () =
    unchanged "def a = -x * y\n"
    unchanged "def a = -(x * y)\n"
    unchanged "def a = 3 - -x\n"
    unchanged "def a = -f(x).y\n"
    // A number or another negation after it would be read as a literal or a comment.
    unchanged "def a = -(5)\n"
    unchanged "def a = -(-x)\n"
    unchanged (lines [ "def f = { x ->"; "  def y = x * 2"; "  -y"; "}" ])

[<Fact>]
let ``** groups to the right, and its brackets are kept where they say otherwise`` () =
    unchanged "def a = 2 ** 3 ** 2\n"
    unchanged "def a = (2 ** 3) ** 2\n"
    unchanged "def a = 2 * 3 ** 2\n"
    unchanged "def a = (2 * 3) ** 2\n"
    unchanged "def a = 2 ** -x\n"
    Assert.Equal("def a = (-x) ** 2\n", format "def a = -x ** 2")

[<Fact>]
let ``A statement or an arm's body takes every operator but a range without parentheses`` () =
    unchanged (lines [ "def f = { x ->"; "  x % 2"; "}" ])
    unchanged (lines [ "def f = { x ->"; "  (1..x)"; "}" ])
    unchanged (lines [ "def g = { x, y ->"; "  match x"; "    | 0 -> x and y"; "    | 1 -> not x"; "    | _ -> x or y"; "}" ])
    Assert.Equal(
        lines [ "def g = { x, y ->"; "  match x"; "    | 0 -> x and y"; "    | _ -> not y"; "}" ],
        format (lines [ "def g = { x, y ->"; "  match x"; "    | 0 -> (x and y)"; "    | _ -> (not y)"; "}" ]))

[<Fact>]
let ``Literals are printed from the source`` () =
    unchanged "def a = 'single'\n"
    unchanged "def b = \"one\n\ntwo\"\n"
    unchanged "def c = \"{a} and {b}\"\n"

[<Fact>]
let ``Names that are operators keep their backticks`` () =
    unchanged "def `<+>` = { a, b -> a + b }\n"
    unchanged "def Point.`==` = { p, q -> p.x == q.x }\n"
    unchanged (lines [ "def Point.`==`: Point, Point -> bool = { p, q ->"; "  p.x == q.x"; "}" ])
    unchanged "context Eq(a) = (`==`: (a, a) -> bool)\n"
    unchanged "def i = Eq(int).`==`(1, 2)\n"
    unchanged "def f = `+`\n"
    unchanged "def g = `+`(1, 2)\n"
    unchanged "def add: [`+`: a, a -> a] = { + }\n"
    Assert.Equal(
        "def f: [(`+`: a, a -> a) & (`*`: a, a -> a) & Eq(a)] a -> a = { x -> x }\n",
        format "def f: [`+`:(a, a) -> a + `*` : a,a -> a+Eq(a)] a -> a = { x -> x }\n")
    // A member's type runs to the end of the set, so a `&` in it is an intersection.
    unchanged "def f: [`-`: a & b -> a] a -> a = { x -> x }\n"
    unchanged "def f: [(`-`: a & b -> a) & Console] a -> a = { x -> x }\n"
    unchanged (lines [ "def main = {"; "  def eqInt: Eq(int) = (`==` = { a, b -> true })"; "  use eqInt"; "  1"; "}" ])

[<Fact>]
let ``Names that are sentences keep their backticks, and an identifier loses them`` () =
    unchanged "def `adds two points` = 1\n"
    unchanged "def `is it empty?` = `adds two points`(1)\n"
    unchanged "def builder = (try = { m, f -> f(m) }, return = { v -> v })\n"
    unchanged "def r = (`first name` = \"Ada\")\n"
    unchanged "def n = r.`first name`\n"
    Assert.Equal("def plain = 1\n", format "def `plain` = 1")
    unchanged "def x = t.0\n"

[<Fact>]
let ``An instance written out in a use is printed as written`` () =
    unchanged (lines [ "def main = {"; "  use Eq(int)(`==` = { a, b -> true })"; "  1"; "}" ])
    unchanged (lines [ "def main = {"; "  use Add(Point)(zero = origin, `+` = plus, `-` = minus, negate = flip)"; "  1"; "}" ])
    unchanged (lines [ "def shown = use ToString(int)(toString = { n -> \"#\" }) in \"{1}\"" ])
