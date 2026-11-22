/// The top level, at the places where reading it takes care: each file is read with no mistake.
module Nanyx.Parser.Tests.DescentTopLevelTests

open Xunit
open Parser.Program
open ParserTestHelpers

[<Theory>]
[<InlineData("module collections.list\n\ndef x = 1\n")>]
[<InlineData("export module acme.web\n")>]
[<InlineData("import nanyx/mref\nimport \"./util.nyx\" as u\nimport math as (sqrt, pi)\nimport strings as s & (trim, `++`)\n")>]
[<InlineData("import (\n  math\n  \"./a.nyx\" as a\n)\n")>]
[<InlineData("export collections.list (map, filter)\nexport shapes\n")>]
[<InlineData("@entry\ndef main = { println(\"hi\") }\n")>]
[<InlineData("@allow(default/unusedBindings)\ndef x = 1\n")>]
[<InlineData("type Point = (x: int, y: int)\nexport type Shape =\n  | #circle(r: float)\n  | #square\n")>]
[<InlineData("type Email = protected string\ntype Id = private int\ntype Cursor = [Tx] protected (row: int)\n")>]
[<InlineData("type Pair(a, b :: Show) = (a, b)\ntype Bag.elem = int\n")>]
[<InlineData("type Positive = int where { > 0 }\n")>]
[<InlineData("context Show(a) = (show: a -> string)\nexport scoped context Memory = protected ()\n")>]
[<InlineData("pattern int -> #Even | #Odd = { n -> if n % 2 == 0 then #Even else #Odd }\n")>]
[<InlineData("def f: int -> int\ndef g: [HostRuntime] I32 -> I32  -- a foreign function\n")>]
[<InlineData("def (a, b) = (1, 2)\nexport def (low, high) = (1, 2)\n")>]
[<InlineData("def r =\n  def a = 1\n  a + 1  -- two\n\nprintln(r)\n")>]
[<InlineData("module shapes =\n  export type Shape = #circle | #square\n  def area = { s -> 0 }\n\n  module inner =\n    def x = 1\n\ndef y = 2\n")>]
[<InlineData("::host nanyx.console\n\nprintln(\"hi\")\n")>]
[<InlineData("use Log(log = { m -> () }) in greet()\n")>]
let ``A file is read`` (source: string) =
    readsWhole source

[<Fact>]
let ``Local types and imports in a block are read`` () =
    let source = "def main = {\n  import math\n  type Pair = (a: int, b: int)\n  def p = Pair(a = 1, b = 2)\n  p.a\n}\n"
    readsWhole source

[<Fact>]
let ``A match with no arms is a mistake`` () =
    match parseModuleWithTrivia "t.nyx" "def f = { x ->\n  match x\n}\n" with
    | Result.Error error -> Assert.Equal(3, error.Span.StartLine + 1)
    | Result.Ok parsed -> Assert.Fail $"%A{parsed.Items}"
