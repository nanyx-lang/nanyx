/// Breaking lines that do not fit in the line width (100 by default).
module Nanyx.Formatter.Tests.LineWidthTests

open Xunit
open Nanyx.Formatter

let private lines (ls: string list) = String.concat "\n" ls + "\n"

let private formatWith width (source: string) =
    match Formatter.formatSource { Formatter.Options.defaults with LineWidth = width } "" source with
    | Result.Ok formatted -> formatted
    | Result.Error error -> failwith $"Parse failed: {error.Message}"

let private format = formatWith 100

let private unchanged (source: string) = Assert.Equal(source, format source)

[<Fact>]
let ``A list that does not fit has one item per line, and the ] on a line of its own`` () =
    Assert.Equal(
        lines [ "def xs = ["; "  first(1)"; "  second(2)"; "  ...rest"; "]" ],
        formatWith 30 "def xs = [first(1), second(2), ...rest]")

[<Fact>]
let ``A call that does not fit has one argument per line`` () =
    Assert.Equal(
        lines [ "def a = f("; "  alpha = 1"; "  beta = 2"; "  gamma = 3"; ")" ],
        formatWith 30 "def a = f(alpha = 1, beta = 2, gamma = 3)")

[<Fact>]
let ``The outermost brackets break first`` () =
    Assert.Equal(
        lines [ "def a = f("; "  g(1, 2)"; "  h(3, 4)"; ")" ],
        formatWith 20 "def a = f(g(1, 2), h(3, 4))")

[<Fact>]
let ``What fits stays on one line`` () =
    unchanged "def a = f(alpha = 1, beta = 2, gamma = 3)\n"
    unchanged "def xs = [1, 2, 3]\n"

[<Fact>]
let ``A long chain of operators breaks after an operator, as many operands to a line as fit`` () =
    Assert.Equal(lines [ "def s = alpha ++ beta ++"; "  gamma ++ delta" ], formatWith 25 "def s = alpha ++ beta ++ gamma ++ delta")

[<Fact>]
let ``A chain in a statement stays on one line, since a statement cannot cross lines`` () =
    unchanged (lines [ "def f = {"; "  alpha ++ beta ++ gamma ++ delta ++ epsilon ++ zeta ++ eta ++ theta ++ iota ++ kappa ++ lambda ++ mu"; "}" ])

[<Fact>]
let ``An if that does not fit puts then and else on lines of their own`` () =
    Assert.Equal(
        lines [ "def f = { x ->"; "  if x > limit"; "    then something(x)"; "    else otherwise(x)"; "}" ],
        formatWith 30 (lines [ "def f = { x ->"; "  if x > limit then something(x) else otherwise(x)"; "}" ]))

[<Fact>]
let ``A signature that does not fit goes on the line after the def's name`` () =
    Assert.Equal(
        lines [ "def priceLine"; "  : list(Product), OrderLine -> #some(Priced) | #unknownSku(string)"; "  = { products, line ->"; "    find(products, line)"; "  }" ],
        formatWith 80 "def priceLine: list(Product), OrderLine -> #some(Priced) | #unknownSku(string) = { products, line -> find(products, line) }")

[<Fact>]
let ``A short signature stays on the def's line even when the value breaks`` () =
    Assert.Equal(
        lines [ "def xs: list(int) = ["; "  first(1)"; "  second(2)"; "]" ],
        formatWith 30 "def xs: list(int) = [first(1), second(2)]")

[<Fact>]
let ``A construct that breaks because it is long gets blank lines around it`` () =
    Assert.Equal(
        lines [ "def a = 1"; ""; "def xs = ["; "  first(1)"; "  second(2)"; "]"; ""; "def b = 2" ],
        formatWith 25 (lines [ "def a = 1"; "def xs = [first(1), second(2)]"; "def b = 2" ]))

[<Fact>]
let ``Breaking to fit is idempotent`` () =
    let once = formatWith 30 "def f = { x -> if x > limit then f(alpha = 1, beta = 2) else [first(1), second(2), third(3)] }"
    Assert.Equal(once, formatWith 30 once)
