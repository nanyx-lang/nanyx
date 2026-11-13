module Nanyx.Compiler.Tests.Features.Definitions.ModuleBlocks

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

/// A `module money =` block with a nominal `Money`, its constructor and printer, an exported value and a private one.
let private moneyBlock =
    "module money =\n" +
    "  export type Money = protected (pence: int)\n" +
    "  export def Money.new: int -> Money = { pence -> Money(pence = pence) }\n" +
    "  export def Money.toString: Money -> string = { m -> \"{m.pence}p\" }\n" +
    "  export def zero: Money = Money.new(0)\n" +
    "  def secret = 42\n\n"

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
    match codesAt (moneyBlock + "import money\n\ndef p: Money = (pence = 1)\n") with
    | [ code, _ ] -> Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    | other -> failwith $"Expected one type mismatch, got %A{other}"

/// A `module ids =` block with a protected `UserId`, whose shape only the block can see.
let private idsBlock =
    "module ids =\n" +
    "  export type UserId = private int\n" +
    "  export def UserId.of: int -> UserId = { n -> UserId(n) }\n" +
    "  export def UserId.value: UserId -> int = { id -> match id | UserId(n) -> n }\n" +
    "  export def first: UserId = UserId.of(1)\n\n"

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
    let fileImport = "import nanyx/console\n\n" + moneyBlock.Replace("def secret = 42", "def secret = { println(\"x\") }")
    Assert.Equal<(string * string) list>([ DiagnosticCodes.OutsideModuleBlock.Code, "println(\"x\")" ], codesAt fileImport)
    let blockImport = moneyBlock.Replace("module money =\n", "module money =\n  import nanyx/console\n").Replace("def secret = 42", "def secret = { println(\"x\") }")
    Assert.Empty(codesAt blockImport)
    Assert.Equal<(string * string) list>([ DiagnosticCodes.OutsideModuleBlock.Code, "println(\"y\")" ], codesAt (blockImport + "println(\"y\")\n"))

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
