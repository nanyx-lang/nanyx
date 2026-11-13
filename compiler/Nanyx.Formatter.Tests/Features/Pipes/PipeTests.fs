module Nanyx.Formatter.Tests.Features.Pipes.Pipes

open Xunit
open Nanyx.Formatter
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``A piped match stays piped, and a pipe after its arms goes on a line of its own`` () =
    unchanged (lines [ "def r = s \\trim \\match"; "  | \"\" -> 0"; "  | _ -> 1" ])
    unchanged (lines [ "def f = { n ->"; "  n \\match"; "    | 0 -> \"zero\""; "    | _ -> \"other\""; "    \\println"; "}" ])

[<Fact>]
let ``A pipe chain written over several lines keeps each step on a line of its own`` () =
    unchanged (lines [ "def r ="; "  rules"; "    \\map { x -> f(x) }"; "    \\string.join(\"\")" ])
    unchanged (lines [ "def r = rules"; "  \\map { x -> f(x) }"; "  \\string.join(\"\")" ])
    // One step on a line of its own puts them all on lines of their own.
    Assert.Equal(lines [ "def r = rules"; "  \\map { x -> f(x) }"; "  \\string.join(\"\")" ], format (lines [ "def r = rules \\map { x -> f(x) }"; "      \\string.join(\"\")" ]))
    unchanged "def r = rules \\map { x -> f(x) } \\string.join(\"\")\n"

[<Fact>]
let ``A pipe chain over several lines in a lambda body is indented once from its line`` () =
    unchanged (lines [ "def f = { xs ->"; "  xs"; "    \\map { x -> x + 1 }"; "    \\sum"; "}" ])
    unchanged (lines [ "def f = { xs ->"; "  def total = xs"; "    \\map { x -> x + 1 }"; "    \\sum"; ""; "  total"; "}" ])
    unchanged (lines [ "def f = { xs -> xs"; "    \\map { x -> x + 1 }"; "    \\sum"; "}" ])

[<Fact>]
let ``A pipe chain over several lines can go through a \match`` () =
    unchanged (lines [ "def f = { n ->"; "  n"; "    \\trim"; "    \\match"; "      | \"\" -> \"empty\""; "      | s -> s"; "    \\toUpper"; "    \\println"; "}" ])
    unchanged (lines [ "def f = { n ->"; "  n \\match"; "    | 0 -> \"zero\""; "    | _ -> \"other\""; "    \\trim \\println"; "}" ])

[<Fact>]
let ``A pipe chain that spans lines keeps a multi-line lambda and the comments between its steps`` () =
    unchanged (lines [ "def r ="; "  xs"; "    \\map { x ->"; "      def y = x + 1"; "      y * 2"; "    } -- doubled"; "    -- add them up"; "    \\sum" ])
    unchanged (lines [ "def r = xs \\map { x ->"; "  x + 1"; "} \\sum" ])

[<Fact>]
let ``A pipe chain that does not fit puts each step on a line of its own`` () =
    let long = "def total = someLongValueName \\list.map { x -> x + someOtherLongName } \\list.filter { y -> y > 100 } \\list.sum"
    Assert.Equal(lines [ "def total = someLongValueName"; "  \\list.map { x -> x + someOtherLongName }"; "  \\list.filter { y -> y > 100 }"; "  \\list.sum" ], format long)

[<Fact>]
let ``A tuple a pipe passes as one argument keeps its brackets`` () =
    unchanged "def r = xs \\fold((0, 0)) { (sum, count), x -> (sum + x, count + 1) }\n"
    unchanged "def r = xs \\f((0, 0), y)\n"
    unchanged "def r = xs \\f(a, b) { y -> y }\n"
    unchanged "def r = xs \\f(a, y = z) { w -> w }\n"

[<Fact>]
let ``A record a pipe passes as one argument keeps its brackets`` () =
    unchanged "def r = x \\f((y = 1))\n"
    unchanged "def r = x \\f((y = 1), b) { w -> w }\n"
    unchanged "def r = x \\f(y = 1)\n"
