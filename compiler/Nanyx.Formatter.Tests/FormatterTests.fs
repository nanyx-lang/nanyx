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
let ``Operators a statement cannot take without parentheses keep them`` () =
    unchanged (lines [ "def f = { x ->"; "  (x % 2)"; "}" ])
    unchanged (lines [ "def g = { x, y ->"; "  match x"; "    | 0 -> (x and y)"; "    | _ -> x + 1"; "}" ])

[<Fact>]
let ``An annotated lambda parameter is bracketed`` () =
    unchanged "def f = { (x: int) -> x + 1 }\n"
    Assert.Equal("def f = { (x: int), y -> x + y }\n", format "def f = { x: int, y -> x + y }")

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
let ``&& and || stay as written`` () =
    unchanged "def a = b && c || d\n"
    unchanged "def a = b and c or d\n"

[<Fact>]
let ``A match that is not the last arm's body is bracketed`` () =
    unchanged (lines [ "def f = { x, y ->"; "  match x"; "    | 0 -> (match y"; "      | 0 -> 1"; "      | _ -> 2)"; "    | _ -> 3"; "}" ])

[<Fact>]
let ``An except keeps one arm on its line and puts several on the lines after it`` () =
    unchanged "def v = Int.fromString(s) except #invalidInt -> #invalidVersionNumber\n"
    unchanged "def v = xs \\find(p) except #notFound | #empty -> #none\n"
    unchanged (lines [ "def v = load(n) except"; "  | #error(#notFound) -> #some(0)"; "  | #error(#io(message)) -> #some(1)" ])
    unchanged (lines [ "def f = { s ->"; "  def n = Int.fromString(s) except #invalidInt -> #some(0)"; "  n"; "}" ])
    Assert.Equal("def v = x except #a -> #b\n", format "def v = x   except   #a->#b")

[<Fact>]
let ``An except that is not the last thing in its construct is bracketed`` () =
    unchanged "def v = (x except #a -> #b) \\describe\n"
    unchanged "def v = f((x except #a -> #b), y)\n"
    unchanged "def v = x except #a -> y except #b -> #c\n"

[<Fact>]
let ``Loops, ranges and with are printed as written`` () =
    unchanged (lines [ "def f = { xs ->"; "  for xs"; "    | 0 -> println(\"zero\")"; "    | _ -> println(\"other\")"; "}" ])
    unchanged (lines [ "def g = {"; "  for 1..3 -> println(\"again\")"; "}" ])
    unchanged "def h = { r -> r with a = 1, b = 2 }\n"

[<Fact>]
let ``Loops over several iterables and ranges without an end are printed as written`` () =
    unchanged (lines [ "def f = { xs ->"; "  for xs, 0.."; "    | 0, i -> println(\"zero at {i}\")"; "    | _, _ -> println(\"other\")"; "}" ])
    unchanged (lines [ "def g = {"; "  for 1..3, xs -> println(\"again\")"; "}" ])
    unchanged "def naturals = 0..\n"
    unchanged "def later = n + 1.. \\take(3)\n"
    Assert.Equal("def later = n + 1..\n", format "def later = (n + 1)..")

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
    unchanged "def g = items \\filter { % 2 == 0 }\n"
    unchanged "def h = people \\filter { .age > 18 } \\map { .name.length }\n"
    unchanged "def i = xs \\map { * 2 + 1 \\abs }\n"
    unchanged "def j = xs \\foldBack(0) { - }\n"
    unchanged "def k = { -> -1 }\n"

[<Fact>]
let ``Guard patterns are printed as written`` () =
    unchanged (lines [ "def f = { n ->"; "  match n"; "    | { < 0 } -> 0"; "    | { % 2 == 0 } -> 1"; "    | { x -> x > limit * 2 } -> 2"; "    | > 5 -> 3"; "    | _ -> 4"; "}" ])
    unchanged (lines [ "def g = {"; "  | (0, { .length > 5 }) -> 1"; "  | #some({ >= 2 }) -> 2"; "  | [{ % 2 == 0 }, ...] -> 3"; "  | _ -> 4"; "}" ])
    Assert.Equal(
        lines [ "def h = {"; "  | { .length > 5 } -> 1"; "  | >= 2 -> 2"; "  | _ -> 3"; "}" ],
        format (lines [ "def h = {"; "  | {.length>5} -> 1"; "  | >=   2 -> 2"; "  | _ -> 3"; "}" ]))

[<Fact>]
let ``And-patterns are printed as written`` () =
    unchanged (lines [ "def f = { n ->"; "  match n"; "    | { < 0 } & m -> m"; "    | m & { > 100 } & _ -> m"; "    | _ -> 0"; "}" ])
    unchanged (lines [ "def g = {"; "  | #some(n & { > 1 }) -> n"; "  | (a & 1, b) -> b"; "  | #error e & whole -> 0"; "  | _ -> 4"; "}" ])
    Assert.Equal("def h = { | a & b -> 1 }\n", format "def h = { | a&b -> 1 }")
    // A tag's payload written after a space keeps its brackets: without them the tag would be a part.
    unchanged (lines [ "def i = {"; "  | #some (a & b) -> 1"; "  | _ -> 2"; "}" ])

[<Fact>]
let ``Record patterns by member name keep their ...`` () =
    unchanged (lines [ "def f = {"; "  | (name = \"Alice\", ...) -> 1"; "  | (name = n, age = a) -> 2"; "}" ])
    Assert.Equal("def g = { | (a = 1, ...) -> 1 }\n", format "def g = { | ( a = 1 ,... ) -> 1 }")

[<Fact>]
let ``A shorthand lambda's spacing is tidied`` () =
    Assert.Equal("def a = xs \\map { * 2 }\n", format "def a = xs \\map {   *   2   }")
    Assert.Equal("def b = { .name }\n", format "def b = {.name}")

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
    unchanged "def a, (b, _) = pair\n"
    unchanged "def low, high = sort(3, -1)\n"
    unchanged (lines [ "def f = { pair ->"; "  def a, b = pair"; "  a + b"; "}" ])
    unchanged "def $Money(pence) = m\n"
    unchanged "def [first, ...rest] = xs\n"
    unchanged (lines [ "def f = { m ->"; "  def $Money(pence) = m"; "  pence"; "}" ])

[<Fact>]
let ``Workflow keywords are printed as written`` () =
    unchanged (lines [ "def f = { args -> proc {"; "    def a = try g(args)"; "    def b = try! h(a)"; "    return b"; "  }"; "}" ])

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
    unchanged (lines [ "import nanyx/console"; ""; "import ("; "  nanyx/text as t"; "  \"./local.nyx\""; ")" ])
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
let ``An attribute's argument is printed as a tag's payload is`` () =
    unchanged (lines [ "@test(#io)"; "def readsConfig = 1" ])
    unchanged (lines [ "@test()"; "def x = 1" ])
    Assert.Equal(lines [ "@retry(times = 3, #slow)"; "def x = 1" ], format (lines [ "@retry( times=3 , #slow )"; "def x = 1" ]))

[<Fact>]
let ``Imports stay together, however many lines they span`` () =
    unchanged (lines [ "import nanyx/console"; ""; "import ("; "  nanyx/text"; "  nanyx/math"; ")"; ""; "import nanyx/result"; ""; "def a = 1" ])

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

[<Fact>]
let ``A lambda body that spans lines is indented, with the closing braces on different lines`` () =
    Assert.Equal(
        lines [ "def checkOrder = { products, lines -> proc {"; "    match lines"; "      | [] -> (return [])"; "      | [first, ...rest] ->"; "        def line = try parseLine(first)"; "        return [line]"; "  }"; "}" ],
        format (lines [ "def checkOrder = { products, lines -> proc {"; "  match lines"; "    | [] -> (return [])"; "    | [first, ...rest] ->"; "      def line = try parseLine(first)"; "      return [line]"; "}"; "}" ]))
    Assert.Equal(
        lines [ "def f = { x -> match x"; "    | 1 -> a"; "    | _ -> b"; "}" ],
        format (lines [ "def f = { x -> match x"; "  | 1 -> a"; "  | _ -> b"; "}" ]))

[<Fact>]
let ``Imports in a row go in one bracketed import`` () =
    Assert.Equal(lines [ "import ("; "  nanyx/math"; "  nanyx/text"; ")" ], format (lines [ "import nanyx/math"; "import nanyx/text" ]))
    Assert.Equal(
        lines [ "import ("; "  nanyx/console"; "  nanyx/math as math"; "  nanyx/text"; ")"; ""; "def a = 1" ],
        format (lines [ "import nanyx/console"; "import nanyx/math as math"; "import (nanyx/text)"; ""; "def a = 1" ]))

[<Fact>]
let ``A comment between imports goes inside the brackets`` () =
    Assert.Equal(
        lines [ "import ("; "  nanyx/math"; "  -- for padding"; "  nanyx/text"; ")" ],
        format (lines [ "import nanyx/math"; "-- for padding"; "import nanyx/text" ]))

[<Fact>]
let ``A blank line between imports keeps them apart`` () =
    unchanged (lines [ "import nanyx/math"; ""; "import nanyx/text" ])

[<Fact>]
let ``A single import stays on one line`` () =
    unchanged (lines [ "import nanyx/math"; ""; "def a = 1" ])

[<Fact>]
let ``Imports in a row in a module block go in one bracketed import too`` () =
    Assert.Equal(
        lines [ "module m ="; "  import ("; "    nanyx/math"; "    nanyx/text"; "  )"; ""; "  def a = 1" ],
        format (lines [ "module m ="; "  import nanyx/math"; "  import nanyx/text"; "  def a = 1" ]))

[<Fact>]
let ``A hole in a type is printed as written`` () =
    unchanged "def xs: list(_) = [1, 2]\n"
    unchanged (lines [ "def f: int, _ -> _ = { a, b ->"; "  a + b"; "}" ])
    unchanged "def g = { (x: _), y -> x + y }\n"
    Assert.Equal("def m: map(string, _) = m\n", format "def m:map(string,_)=m")

[<Fact>]
let ``Names that are operators keep their backticks`` () =
    unchanged "def `<+>` = { a, b -> a + b }\n"
    unchanged "def Point.`==` = { p, q -> p.x == q.x }\n"
    unchanged (lines [ "def Point.`==`: Point, Point -> bool = { p, q ->"; "  p.x == q.x"; "}" ])
    unchanged "context Eq(a) = (`==`: (a, a) -> bool)\n"
    unchanged "def i = Eq(int).`==`(1, 2)\n"
    unchanged "def f = `+`\n"
    unchanged "def g = `+`(1, 2)\n"
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