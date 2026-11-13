module Nanyx.Compiler.Tests.Features.Types.TypeNames

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The diagnostics compiling `source` gives: each one's code, message and the text it is reported at.
let private compiled (source: string) =
    let source = source.ReplaceLineEndings "\n"
    (Compiler.compile source).Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, diagnostic.Message, textAt source (spanOf diagnostic))

[<Fact>]
let ``A type defined with a lowercase name is an error at the name`` () =
    Assert.Equal<(string * string * string) list>(
        [ DiagnosticCodes.InvalidTypeName.Code, "'point' cannot be a type's name, which starts with a capital letter: write 'Point'", "point" ],
        compiled """
type point = (x: int)
def p: point = (x = 1)
""")

[<Fact>]
let ``A nominal type, a context and a local type are named with a capital letter too`` () =
    let source =
        """
type $money = int

context log = (write: string -> ())

def f = {
  type pair = (int, int)
  1
}
"""
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.InvalidTypeName.Code, "$money"; DiagnosticCodes.InvalidTypeName.Code, "log"; DiagnosticCodes.InvalidTypeName.Code, "pair" ],
        compiled source |> List.map (fun (code, _, text) -> code, text))
    Assert.Contains("write '$Money'", (compiled source).Head |> fun (_, message, _) -> message)

[<Fact>]
let ``A type cannot be defined with a built-in type's name`` () =
    Assert.Equal<(string * string * string) list>(
        [ DiagnosticCodes.InvalidTypeName.Code, "'int' is a built-in type, so it cannot be defined again: a type's name starts with a capital letter", "int" ],
        compiled "type int = string\n")

[<Fact>]
let ``A lowercase name longer than one letter in a type is reserved, not an unknown type`` () =
    Assert.Equal<(string * string * string) list>(
        [ DiagnosticCodes.InvalidTypeName.Code,
          "'foo' is not a type: a type's name starts with a capital letter, and a lowercase name longer than one letter is reserved for associated types",
          "foo" ],
        compiled "def f: foo -> int = { x -> 1 }\n")
    Assert.Equal<string list>([ DiagnosticCodes.UnknownType.Code ], compiled "def f: Foo -> int = { x -> 1 }\n" |> List.map (fun (code, _, _) -> code))

[<Fact>]
let ``Capitalised names, type variables and lowercase built-in types are valid`` () =
    Assert.Empty(compiled "type Point = (x: int)\ntype $Money = int\ntype _Later = int\ndef f: (a, list(string), Point) -> a = { x, _, _ -> x }\n")
    Assert.Contains("write '_Later'", compiled "type _later = int\n" |> List.exactlyOne |> fun (_, message, _) -> message)
