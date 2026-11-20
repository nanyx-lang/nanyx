module Nanyx.Compiler.Tests.Features.TypeArguments

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// Type arguments: a parameter that takes a type, `type a` or `into: type`, of type `Type(a)`, and `type Point`, a value
// of type `Type(Point)` (see `TypeArguments`).

let private decode =
    """context Decode(a) = (decode: string -> a)

type Point = (x: int, y: int)

def Point.decode: string -> Point = { text -> (x = text.length, y = 0) }

def deserialise: [Decode(a)] (type a, string) -> a = { t, text -> decode(text) }

def parse: [Decode(into)] (string, into: type) -> into = { text, into -> decode(text) }
"""

let private withDecode (source: string) = normalized (decode + source)

/// The diagnostic with `code` that compiling `source` reports, which is its only error.
let private onlyError (code: DiagnosticCode) (source: string) =
    let errors = (Compiler.compile source).Diagnostics |> List.filter (fun diagnostic -> diagnostic.Severity = Severity.Error)
    Assert.Equal<string list>([ code.Code ], errors |> List.map _.Code)
    errors.Head

[<Fact>]
let ``type Point is a value of type Type(Point)`` () =
    let typed = compileClean (withDecode "\ndef t = type Point\n")
    Assert.Equal("Type(Point)", Unifier.tyToString typed.Types.["t"])

[<Fact>]
let ``A positional type argument fixes the type variable it is passed for`` () =
    let typed = compileClean (withDecode "\ndef p = deserialise(type Point, \"abc\")\n")
    Assert.Equal("Point", Unifier.tyToString typed.Types.["p"])

[<Fact>]
let ``A named type parameter is a type variable over the whole signature`` () =
    let typed = compileClean (withDecode "\ndef q = parse(\"abc\", into = type Point)\n")
    Assert.Equal("Point", Unifier.tyToString typed.Types.["q"])
    Assert.Equal("(string, into: Type(into)) -> into", Unifier.tyToString typed.Types.["parse"])

[<Fact>]
let ``Generic code passes on a type argument of its own type variable`` () =
    let source =
        withDecode
            """
def again: [Decode(a)] (type a, string) -> a = { t, text -> parse(text, into = type a) }

def r = again(type Point, "ab")
"""
    let typed = compileClean source
    Assert.Equal("Point", Unifier.tyToString typed.Types.["r"])

[<Fact>]
let ``A named type parameter is passed by name`` () =
    let error = onlyError DiagnosticCodes.PositionalForNamedArgument (withDecode "\ndef p = parse(\"abc\", type Point)\n")
    Assert.Contains("'parse' takes 'into' by name", error.Message)

[<Fact>]
let ``A positional type argument is never left out, even where the result type says it`` () =
    let error = onlyError DiagnosticCodes.MissingTypeArgument (withDecode "\ndef p: Point = deserialise(\"abc\")\n")
    Assert.Equal(
        "'deserialise' takes a type argument, which this call leaves out: a type argument is never left out, so pass `type Point`",
        error.Message
    )

[<Fact>]
let ``A named type argument is never left out`` () =
    let error = onlyError DiagnosticCodes.MissingTypeArgument (withDecode "\ndef q: Point = parse(\"abc\")\n")
    Assert.Equal(
        "'parse' takes the type argument 'into', which this call leaves out: a type argument is never left out, so pass 'into = type Point'",
        error.Message
    )

[<Fact>]
let ``A type argument can't be chosen with if`` () =
    let error = onlyError DiagnosticCodes.TypeChosenAtRunTime (withDecode "\ndef t = { c -> if c then type int else type string }\n")
    Assert.StartsWith("A type argument can't be chosen with `if`", error.Message)

[<Fact>]
let ``A type argument can't be chosen with match`` () =
    let source =
        withDecode
            """
def t = { b ->
  match b
    | #yes -> type int
    | #no -> type string
}
"""
    let error = onlyError DiagnosticCodes.TypeChosenAtRunTime source
    Assert.StartsWith("A type argument can't be chosen with `match`", error.Message)

[<Fact>]
let ``A list can't hold Type values of different types`` () =
    let error = onlyError DiagnosticCodes.TypeChosenAtRunTime (withDecode "\ndef ts = [type int, type string]\n")
    Assert.StartsWith("A list can't hold `Type` values of different types", error.Message)

[<Fact>]
let ``A built-in type can't name a type parameter`` () =
    let error = onlyError DiagnosticCodes.InvalidTypeParameter (withDecode "\ndef f: (string, list: type) -> int = { text, list -> 1 }\n")
    Assert.Equal("'list' is a built-in type, so it can't be the name of a type parameter", error.Message)

[<Fact>]
let ``A type parameter has no default`` () =
    let error = onlyError DiagnosticCodes.InvalidTypeParameter (withDecode "\ndef f: (string, into: type = 1) -> int = { text, into -> 1 }\n")
    Assert.StartsWith("The type parameter 'into' can't have a default", error.Message)

[<Fact>]
let ``type names a type that exists`` () =
    let error = onlyError DiagnosticCodes.UnknownType (withDecode "\ndef t = type Pointt\n")
    Assert.StartsWith("'Pointt' is not in scope", error.Message)
