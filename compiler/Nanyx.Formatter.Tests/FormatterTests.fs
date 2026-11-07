module Nanyx.Formatter.Tests.FormatterTests

open Xunit
open Nanyx.Formatter

let private lines (ls: string list) = String.concat "\n" ls + "\n"

let private format (source: string) =
    match Formatter.formatSource Formatter.Options.defaults "" source with
    | Result.Ok formatted -> formatted
    | Result.Error error -> failwith $"Parse failed: {error.Message}"

/// Formatting `source` gives it back unchanged.
let private unchanged (source: string) = Assert.Equal(source, format source)

[<Fact>]
let ``Spacing is made regular`` () =
    Assert.Equal("def add = { a, b -> a + b }\n", format "def add={a,b->a+b}")

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
let ``Operators a statement cannot take without parentheses keep them`` () =
    unchanged (lines [ "def f = { x ->"; "  (x % 2)"; "}" ])
    unchanged (lines [ "def g = { x, y ->"; "  match x"; "    | 0 -> (x and y)"; "    | _ -> x + 1"; "}" ])

[<Fact>]
let ``&& and || stay as written`` () =
    unchanged "def a = b && c || d\n"
    unchanged "def a = b and c or d\n"

[<Fact>]
let ``A match that is not the last arm's body is bracketed`` () =
    unchanged (lines [ "def f = { x, y ->"; "  match x"; "    | 0 -> (match y"; "      | 0 -> 1"; "      | _ -> 2)"; "    | _ -> 3"; "}" ])

[<Fact>]
let ``Loops, ranges and with are printed as written`` () =
    unchanged (lines [ "def f = { xs ->"; "  for xs"; "    | 0 -> println(\"zero\")"; "    | _ -> println(\"other\")"; "}" ])
    unchanged (lines [ "def g = {"; "  for 1..3 -> println(\"again\")"; "}" ])
    unchanged "def h = { r -> r with a = 1, b = 2 }\n"

[<Fact>]
let ``List spreads are printed as written`` () =
    unchanged "def a = [1, ...xs, 2, ...ys]\n"
    unchanged "def b = [1] ++ xs\n"

[<Fact>]
let ``Shorthand lambdas are printed as written`` () =
    unchanged "def a = xs \\map { * 2 }\n"
    unchanged "def b = xs \\fold(0, { + })\n"
    unchanged "def c = xs \\map { .name }\n"
    unchanged "def d = { }\n"
    unchanged "def e = { | 0 -> 1 }\n"
    unchanged (lines [ "def f = {"; "  | 0 -> 1"; "  | _ -> 2"; "}" ])

[<Fact>]
let ``A lambda argument stays in brackets or after them, as written`` () =
    unchanged "def a = f({ x -> x })\n"
    unchanged "def b = f(1) { x -> x }\n"
    unchanged "def c = xs \\map { x -> x }\n"

[<Fact>]
let ``if, if-is and multi-branch if are printed as written`` () =
    unchanged "def a = { x -> if x > 0 then 1 else 0 }\n"
    unchanged "def b = { x -> if x is #some(y) then y else 0 }\n"
    unchanged (lines [ "def c = { x ->"; "  if x > 0 then"; "    println(\"positive\")"; "}" ])
    unchanged (lines [ "def d = { x ->"; "  if"; "    | x < 0 -> \"negative\""; "    | x is 0 -> \"zero\""; "    | else -> \"positive\""; "}" ])

[<Fact>]
let ``Or-patterns, rec and destructuring defs are printed as written`` () =
    unchanged (lines [ "def f = {"; "  | 0 | 1 -> \"small\""; "  | _ -> \"big\""; "}" ])
    unchanged (lines [ "def g = {"; "  rec loop = { n -> loop(n) }"; "  loop(1)"; "}" ])
    unchanged "def (a, (b, _)) = pair\n"

[<Fact>]
let ``Workflow keywords are printed as written`` () =
    unchanged (lines [ "def f = { args -> proc {"; "  def a = try g(args)"; "  def b = try! h(a)"; "  return b"; "}"; "}" ])

[<Fact>]
let ``Literals are printed from the source`` () =
    unchanged "def a = 'single'\n"
    unchanged "def b = \"one\n\ntwo\"\n"
    unchanged "def c = \"{a} and {b}\"\n"

[<Fact>]
let ``Type annotations read as the docs write them`` () =
    unchanged (lines [ "def f: int, int -> int = { a, b ->"; "  a + b"; "}" ])
    unchanged (lines [ "def g: (int -> int), int -> int = { h, x ->"; "  h(x)"; "}" ])
    unchanged "def h: list(a -> b) -> int = { fs -> 0 }\n"
    unchanged "type Point = (x: int, y: int)\n"
    unchanged "type Shape = #circle(radius: float) | #square(float)\n"
    unchanged "export context Console(a) = (print: a -> ())\n"

[<Fact>]
let ``Comments stay where they were written`` () =
    unchanged (lines [ "-- the answer"; "def a = 42 -- and no other"; ""; "-- the end" ])
    unchanged (lines [ "def f = { x ->"; "  -- add one"; "  def y = x + 1"; "  y * 2 -- doubled"; "}" ])

[<Fact>]
let ``A record with comments in it gets a line per field`` () =
    Assert.Equal(
        lines [ "def x = ("; "  a = 1"; "  -- the second field"; "  b = 2 -- trailing"; ")" ],
        format (lines [ "def x = ("; "  a = 1"; ""; "  -- the second field"; "  b = 2 -- trailing"; ")" ]))

[<Fact>]
let ``Single blank lines between statements are kept, and runs of them become one`` () =
    Assert.Equal(lines [ "def a = 1"; ""; "def b = 2"; "def c = 3" ], format (lines [ "def a = 1"; ""; ""; ""; "def b = 2"; "def c = 3" ]))
    Assert.Equal(lines [ "def f = {"; "  def a = 1"; ""; "  a"; "}" ], format (lines [ "def f = {"; ""; "  def a = 1"; ""; ""; "  a"; ""; "}" ]))

[<Fact>]
let ``Imports and module blocks are printed as written`` () =
    unchanged (lines [ "import nanyx/console"; "import (" ; "  nanyx/text as t"; "  \"./local.nyx\""; ")" ])
    unchanged (lines [ "module app"; ""; "module geometry ="; "  def origin = 0"; "  def unit = 1"; ""; "def p = geometry.origin" ])

[<Fact>]
let ``A construct that spans lines gets a blank line before and after it at the top level`` () =
    Assert.Equal(
        lines [ "def zero = 0"; "def one = 1"; ""; "def add = { a, b ->"; "  a + b"; "}"; ""; "def two = 2" ],
        format (lines [ "def zero = 0"; "def one = 1"; "def add = { a, b ->"; "  a + b"; "}"; "def two = 2" ]))

[<Fact>]
let ``Two constructs that span lines get one blank line between them, not two`` () =
    Assert.Equal(
        lines [ "def f = {"; "  1"; "}"; ""; "def g = {"; "  2"; "}" ],
        format (lines [ "def f = {"; "  1"; "}"; "def g = {"; "  2"; "}" ]))

[<Fact>]
let ``Statements in a lambda that span lines are separated, but not from its braces`` () =
    Assert.Equal(
        lines [ "def main = {"; "  def f = { x ->"; "    x"; "  }"; ""; "  def a = 1"; "  def b = 2"; ""; "  match a"; "    | 1 -> b"; "    | _ -> 0"; "}" ],
        format (lines [ "def main = {"; "  def f = { x ->"; "    x"; "  }"; "  def a = 1"; "  def b = 2"; "  match a"; "    | 1 -> b"; "    | _ -> 0"; "}" ]))

[<Fact>]
let ``The blank line goes before a construct's leading comment`` () =
    Assert.Equal(
        lines [ "def a = 1"; ""; "-- Adds two numbers."; "def add = { a, b ->"; "  a + b"; "}" ],
        format (lines [ "def a = 1"; "-- Adds two numbers."; "def add = { a, b ->"; "  a + b"; "}" ]))

[<Fact>]
let ``An attribute stays on the line before its item`` () =
    Assert.Equal(
        lines [ "def a = 1"; ""; "@entry"; "def main = {"; "  a"; "}" ],
        format (lines [ "def a = 1"; "@entry"; "def main = {"; "  a"; "}" ]))

[<Fact>]
let ``Imports stay together, however many lines they span`` () =
    unchanged (lines [ "import nanyx/console"; "import ("; "  nanyx/text"; "  nanyx/math"; ")"; "import nanyx/result"; ""; "def a = 1" ])

[<Fact>]
let ``An indented block separates multi-line constructs and keeps the author's blank lines`` () =
    Assert.Equal(
        lines [ "def x ="; "  def f = { y ->"; "    y"; "  }"; ""; "  f(1)" ],
        format (lines [ "def x ="; "  def f = { y ->"; "    y"; "  }"; "  f(1)" ]))
    Assert.Equal(
        lines [ "module m ="; "  def f = { y ->"; "    y"; "  }"; ""; "  def g = 2" ],
        format (lines [ "module m ="; "  def f = { y ->"; "    y"; "  }"; "  def g = 2" ]))
    unchanged (lines [ "def x ="; "  def a = 1"; ""; "  -- the result"; "  a" ])
    unchanged (lines [ "module m ="; "  def a = 1"; ""; "  def b = 2" ])
    Assert.Equal(
        lines [ "def x ="; "  def a = 1"; ""; "  a" ],
        format (lines [ "def x ="; "  def a = 1"; ""; ""; "  a" ]))

[<Fact>]
let ``Separating constructs is idempotent`` () =
    let once = format (lines [ "def a = 1"; "-- note"; "def f = {"; "  1"; "}"; "def b = 2" ])
    Assert.Equal(once, format once)

[<Fact>]
let ``Fields go one per line when one of them spans lines`` () =
    Assert.Equal(
        lines [ "def main = {"; "  use Log("; "    log = { message ->"; "      println(\"\")"; "      println(message)"; "    }"; "  )"; ""; "  run()"; "}" ],
        format (lines [ "def main = {"; "  use Log(log = { message ->"; "    println(\"\")"; "    println(message)"; "  })"; "  run()"; "}" ]))
    Assert.Equal(
        lines [ "def pair = ("; "  1"; "  { x ->"; "    x"; "  }"; ")" ],
        format (lines [ "def pair = (1, { x ->"; "  x"; "})" ]))

[<Fact>]
let ``Fields that each fit on one line stay on one line`` () =
    unchanged "def a = f(x = 1, y = { z -> z })\n"

[<Fact>]
let ``An annotated def's lambda body goes on the line after the arrow`` () =
    Assert.Equal(
        lines [ "def heading: [Log] string -> () = { title ->"; "  log(\"== \" ++ String.uppercase(title) ++ \" ==\")"; "}" ],
        format "def heading: [Log] string -> () = { title -> log(\"== \" ++ String.uppercase(title) ++ \" ==\") }")
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
