module Nanyx.Compiler.Tests.Features.Definitions.ModuleBlocks

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

/// A `module money =` block with a nominal `Money`, its constructor and printer, an exported value and a private one.
let private moneyBlock =
    normalized """module money =
  export type Money = protected (pence: int)
  export def Money.new: int -> Money = { pence -> Money(pence = pence) }
  export def Money.toString: Money -> string = { m -> "{m.pence}p" }
  export def zero: Money = Money.new(0)
  def secret = 42

"""

/// The diagnostics compiling `source` gives: each one's code and the text it is reported at.
let private codesAt (source: string) =
    (Compiler.compile source).Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, textAt source (spanOf diagnostic))

[<Fact>]
let ``A module block can be imported by its file, unqualified or under an alias, before or after it`` () =
    let unqualified = moneyBlock + "import money\n\ndef price: Money = Money.new(250)\ndef text: string = Money.toString(price)\ndef nothing: string = zero \\toString\n"
    Assert.Empty(codesAt unqualified)
    let aliased = moneyBlock + "import money as m\n\ndef price: m.Money = m.Money.new(250)\ndef text: string = m.Money.toString(m.zero)\n"
    Assert.Empty(codesAt aliased)
    let first = "import money\n\n" + moneyBlock + "def text: string = Money.toString(zero)\n"
    Assert.Empty(codesAt first)

[<Fact>]
let ``A module block's import brings in only its exports, and a local still shadows them`` () =
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UnknownIdentifier.Code, "secret" ], codesAt (moneyBlock + "import money\n\ndef s = secret\n"))
    Assert.Empty(codesAt (moneyBlock + "import money\n\ndef f: int -> int = { zero -> zero + 1 }\n"))

[<Fact>]
let ``A nominal type in a module block can only be constructed in the block`` () =
    let viaImport = moneyBlock + "import money\n\ndef p = Money(pence = 1)\n"
    Assert.Equal<(string * string) list>([ DiagnosticCodes.NominalConstructedOutsideItsModule.Code, "Money" ], codesAt viaImport)
    let qualified = moneyBlock + "def p = money.Money(pence = 1)\n"
    Assert.Equal<(string * string) list>([ DiagnosticCodes.NominalConstructedOutsideItsModule.Code, "money.Money" ], codesAt qualified)
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.ProtectedShape.Code, "(pence = 1)" ],
        codesAt (moneyBlock + "import money\n\ndef p: Money = (pence = 1)\n"))

/// A `module ids =` block with a protected `UserId`, whose shape only the block can see.
let private idsBlock =
    normalized """module ids =
  export type UserId = private int
  export def UserId.of: int -> UserId = { n -> UserId(n) }
  export def UserId.value: UserId -> int = { id -> match id | UserId(n) -> n }
  export def first: UserId = UserId.of(1)

"""

[<Fact>]
let ``A protected type can only be taken apart in the module that defines it`` () =
    Assert.Empty(codesAt (idsBlock + "import ids\n\ndef n: int = UserId.value(first)\n"))
    Assert.Equal<(string * string) list>([ DiagnosticCodes.NominalConstructedOutsideItsModule.Code, "UserId(n)" ], codesAt (idsBlock + "import ids\n\ndef UserId(n) = first\n"))
    Assert.Equal<(string * string) list>([ DiagnosticCodes.NominalConstructedOutsideItsModule.Code, "UserId(n)" ], codesAt (idsBlock + "import ids\n\ndef n = match first | UserId(n) -> n\n"))
    // A nominal type that is not protected has no hidden shape, so its pattern is usable anywhere.
    Assert.Empty(codesAt (moneyBlock + "import money\n\ndef Money(m) = zero\ndef p: int = m.pence\n"))

[<Fact>]
let ``A module block cannot use its file's definitions`` () =
    let source = "def outerValue = 1\n\n" + moneyBlock.Replace("def secret = 42", "def secret = outerValue")
    Assert.Equal<(string * string) list>([ DiagnosticCodes.OutsideModuleBlock.Code, "outerValue" ], codesAt source)

[<Fact>]
let ``A module block's imports are its own`` () =
    let fileImport = "import nanyx/math\n\n" + moneyBlock.Replace("def secret = 42", "def secret = abs(-1)")
    Assert.Equal<(string * string) list>([ DiagnosticCodes.OutsideModuleBlock.Code, "abs(-1)" ], codesAt fileImport)
    let blockImport = moneyBlock.Replace("module money =\n", "module money =\n  import nanyx/math\n").Replace("def secret = 42", "def secret = abs(-1)")
    Assert.Empty(codesAt blockImport)
    Assert.Equal<(string * string) list>([ DiagnosticCodes.OutsideModuleBlock.Code, "abs(-2)" ], codesAt (blockImport + "def other = abs(-2)\n"))

[<Fact>]
let ``A module block can import the block beside it, but not reach it without an import`` () =
    let other = "module prices =\n  import money\n  export def price: Money = Money.new(5)\n"
    Assert.Empty(codesAt (moneyBlock + other))
    let unimported = "module prices =\n  export def nothing = money.zero\n"
    Assert.Equal<(string * string) list>([ DiagnosticCodes.OutsideModuleBlock.Code, "money.zero" ], codesAt (moneyBlock + unimported))

[<Fact>]
let ``A module block has no top-level code`` () =
    let source = moneyBlock.Replace("def secret = 42", "def secret = 42\n  secret + 1")
    Assert.Equal<(string * string) list>([ DiagnosticCodes.CodeInModuleBlock.Code, "secret + 1" ], codesAt source)
[<Fact>]
let ``A module block cannot name or make its file's types`` () =
    let point = "type Point = (x: int, y: int)\n\n"
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.OutsideModuleBlock.Code, "Point" ],
        codesAt (point + "module shapes =\n  export def origin: Point = (x = 0, y = 0)\n"))
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.OutsideModuleBlock.Code, "Point" ],
        codesAt (point + "module shapes =\n  export def all: list(Point) = []\n"))
    match codesAt (point + "module shapes =\n  export def origin = Point(x = 0, y = 0)\n") with
    | [ code, text ] ->
        Assert.Equal(DiagnosticCodes.OutsideModuleBlock.Code, code)
        Assert.StartsWith("Point", text)
    | other -> failwith $"Expected one diagnostic, got %A{other}"
    Assert.Empty(codesAt (point + "def p: Point = (x = 1, y = 2)\n"))

[<Fact>]
let ``A module block names the types of the blocks it imports, and its own`` () =
    let shapes = "module shapes =\n  export type Point = (x: int, y: int)\n  export def origin: Point = Point(x = 0, y = 0)\n\n"
    Assert.Empty(codesAt (shapes + "module draw =\n  import shapes\n  export def dot: Point -> int = { p -> p.x }\n"))
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.OutsideModuleBlock.Code, "shapes.Point" ],
        codesAt (shapes + "module draw =\n  export def dot: shapes.Point -> int = { p -> p.x }\n"))

[<Fact>]
let ``A module block names only the imported types it imports itself`` () =
    withTempDir (fun dir ->
        File.WriteAllText(Path.Combine(dir, "people.nyx"), "module people\nexport type Person = protected (name: string)\n")
        let compile (main: string) =
            let mainPath = Path.Combine(dir, "main.nyx")
            File.WriteAllText(mainPath, main)
            (Compiler.compileFile mainPath).Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, textAt main (spanOf diagnostic))
        Assert.Equal<(string * string) list>(
            [ DiagnosticCodes.OutsideModuleBlock.Code, "Person" ],
            compile "import people\n\nmodule greet =\n  export def name: Person -> string = { p -> p.name }\n")
        Assert.Empty(compile "module greet =\n  import people\n  export def name: Person -> string = { p -> p.name }\n"))

[<Fact>]
let ``A module block nested in another is imported by the outer block and qualified with both`` () =
    let source =
        normalized """module tally =
  module steps =
    export def step = 1

  import steps
  export def total = step + 1

import tally

def t: int = total
def s: int = tally.steps.step
"""
    Assert.Empty(codesAt source)

/// A block `outer` that re-exports the block nested in it and a selection of another file's module.
let private reExportingBlock =
    normalized """module outer =
  export inner
  export util (double)

  module inner =
    export def inc: int -> int = { x -> x + 1 }

  export def twice: int -> int = { x -> double(inc(x)) }

"""

let private utilModule = [ "util.nyx", "module util\n\nexport def double: int -> int = { x -> x * 2 }\nexport def triple: int -> int = { x -> x * 3 }\n" ]

[<Fact>]
let ``A module block's import brings in what the block re-exports, of a nested block and of another file's module`` () =
    let result = compileImportingAll utilModule (reExportingBlock + "import outer\n\ndef value = twice(inc(1)) + double(2)\n")
    Assert.Empty(result.Diagnostics)

[<Fact>]
let ``What a module block re-exports of another file's module is not imported outside it unless the block is`` () =
    let source = reExportingBlock + "def value = double(2)\n"
    let diagnostic = Assert.Single((compileImportingAll utilModule source).Diagnostics)
    Assert.Equal(DiagnosticCodes.OutsideModuleBlock.Code, diagnostic.Code)

[<Fact>]
let ``A module block's import that selects names brings in only those`` () =
    Assert.Empty(codesAt (moneyBlock + "import money as (zero)\n\ndef value = zero\n"))
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.NotExported.Code, "Coin"; DiagnosticCodes.UnknownFunction.Code, "Money.new" ],
        codesAt (moneyBlock + "import money as (Coin, zero)\n\ndef price = Money.new(1)\n"))

[<Fact>]
let ``A module block takes its own nominal value apart by its type's name`` () =
    // The Nominal types page's accessor, as a lambda's parameter pattern and as a def's, in a block (#2342).
    let source =
        normalized
            """module ids =
  export type UserId = private int
  export def UserId.new: int -> UserId = { n -> UserId(n) }
  export def UserId.value: UserId -> int = { UserId(n) -> n }
  export def UserId.next: UserId -> int = { u ->
    def UserId(n) = u
    n + 1
  }
  export def UserId.isZero: UserId -> bool = { u ->
    match u
      | UserId(0) -> true
      | _ -> false
  }

println(ids.UserId.value(ids.UserId.new(3)))
"""
    Assert.Empty(codesAt source)
