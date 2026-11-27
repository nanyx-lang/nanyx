module Nanyx.Compiler.Tests.Features.TypeArguments

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// Type arguments: a parameter that takes a type, of type `Type(a)`, and `type Point`, a value
// of type `Type(Point)` (see `TypeArguments`).

let private decode =
    """context Decode(a) = (decode: string -> a)

type Point = (x: int, y: int)

def Point.decode: string -> Point = { text -> (x = text.length, y = 0) }

def deserialise: [Decode(a)] (Type(a), string) -> a = { t, text -> decode(text) }

def parse: [Decode(into)] (string, into: Type(into)) -> into = { text, into -> decode(text) }
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
def again: [Decode(a)] (Type(a), string) -> a = { t, text -> parse(text, into = type a) }

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
    let error = onlyError DiagnosticCodes.InvalidTypeParameter (withDecode "\ndef f: (string, list: Type(list)) -> int = { text, list -> 1 }\n")
    Assert.Equal("'list' is a built-in type, so it can't be the name of a type parameter", error.Message)

[<Fact>]
let ``A type parameter has no default`` () =
    let error = onlyError DiagnosticCodes.InvalidTypeParameter (withDecode "\ndef f: (string, into: Type(into) = 1) -> int = { text, into -> 1 }\n")
    Assert.StartsWith("A type parameter can't have a default", error.Message)

[<Fact>]
let ``type names a type that exists`` () =
    let error = onlyError DiagnosticCodes.UnknownType (withDecode "\ndef t = type Pointt\n")
    Assert.StartsWith("'Pointt' is not in scope", error.Message)

[<Fact>]
let ``A named type parameter's field and its type variable can have different names`` () =
    let source =
        withDecode
            """
def into: [Decode(a)] (string, into: Type(a)) -> a = { text, into -> decode(text) }

def q = into("abc", into = type Point)
"""
    let typed = compileClean source
    Assert.Equal("Point", Unifier.tyToString typed.Types.["q"])
    Assert.Equal("(string, into: Type(a)) -> a", Unifier.tyToString typed.Types.["into"])

[<Fact>]
let ``A lambda's parameter takes a type argument written as Type(a)`` () =
    let source =
        withDecode
            """
def read = { t: Type(a), text: string -> deserialise(t, text) }

def p = read(type Point, "abc")
"""
    let typed = compileClean source
    Assert.Equal("Point", Unifier.tyToString typed.Types.["p"])

[<Fact>]
let ``Typed(a)'s typeOf is passed for a Type(a) parameter`` () =
    let source =
        withDecode
            """
def viaTyped: [Typed(a), Decode(a)] string -> a = { text -> deserialise(typeOf, text) }

def p: Point = viaTyped("abc")
"""
    let typed = compileClean source
    Assert.Equal("Point", Unifier.tyToString typed.Types.["p"])

/// The error compiling `source` gives for a type parameter written the old way, and `source` with its fix applied.
let private oldForm (source: string) =
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.OldFormTypeParameter.Code, diagnostic.Code)
    Assert.Equal(Severity.Error, diagnostic.Severity)
    let fix = Assert.Single(diagnostic.Fixes)
    diagnostic, applyFix source fix, fix.Title

[<Fact>]
let ``A positional type parameter written type a is an error whose fix writes Type(a)`` () =
    let source = normalized "context Decode(a) = (decode: string -> a)\n\ndef f: [Decode(a)] type a, string -> a = { t, text -> decode(text) }\n"
    let diagnostic, fixedSource, title = oldForm source
    Assert.Equal(
        "A parameter that takes a type is written with its type, `Type(a)`: write `Type(a)`, not `type a`",
        diagnostic.Message
    )
    Assert.Equal("Write `Type(a)`", title)
    Assert.Contains("def f: [Decode(a)] Type(a), string -> a", fixedSource)
    let typed = compileClean fixedSource
    Assert.Equal("Type(a), string -> a", Unifier.tyToString typed.Types.["f"])

[<Fact>]
let ``A named type parameter written into: type is an error whose fix writes into: Type(into)`` () =
    let source =
        normalized "context Decode(a) = (decode: string -> a)\n\ndef f: [Decode(into)] (string, into: type) -> into = { text, into -> decode(text) }\n"
    let diagnostic, fixedSource, title = oldForm source
    Assert.Equal("Write `into: Type(into)`", title)
    Assert.StartsWith("A parameter that takes a type is written with its type, `Type(a)`: write `into: Type(into)`", diagnostic.Message)
    Assert.Contains("(string, into: Type(into)) -> into", fixedSource)
    compileClean fixedSource |> ignore

[<Fact>]
let ``A type parameter written the old way in a function type's parameter is the error too`` () =
    let source = normalized "def build: ((type a) -> list(a)) -> list(a) = { f -> f(type a) }\n"
    let _, fixedSource, _ = oldForm source
    Assert.Contains("def build: ((Type(a)) -> list(a)) -> list(a) = { f -> f(type a) }", fixedSource)
    compileClean fixedSource |> ignore

[<Fact>]
let ``A type parameter written the old way is read as Type(a), so that it is the only error`` () =
    let source = withDecode "\ndef old: [Decode(a)] type a, string -> a = { t, text -> decode(text) }\n\ndef p = old(type Point, \"abc\")\n"
    let result = Compiler.compile source
    Assert.Equal<string list>([ DiagnosticCodes.OldFormTypeParameter.Code ], result.Diagnostics |> List.map _.Code)
    Assert.Equal("Point", Unifier.tyToString result.Typed.Value.Types.["p"])

[<Fact>]
let ``A built-in type's name written the old way is only the error about the name`` () =
    let result = Compiler.compile (withDecode "\ndef f: (string, list: type) -> int = { text, list -> 1 }\n")
    Assert.Equal<string list>([ DiagnosticCodes.InvalidTypeParameter.Code ], result.Diagnostics |> List.map _.Code)

[<Fact>]
let ``A module that imports one writing a type parameter the old way reports its error, as any error of an import`` () =
    let old = "module old\n\nexport def none: type a -> list(a) = { t -> [] }\n"
    let main = "module main\n\nimport app/old\n\ndef xs: list(int) = none(type int)\n"
    use package =
        new TempPackage([ "app.nyxp", """(package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"]))"""; "src/old.nyx", old; "src/main.nyx", main ])
    let results = (Package.compilePackageReading (SeededPackages.user ()) System.IO.File.ReadAllText (package.Path "src/main.nyx")).Value
    let codesOf (name: string) =
        results |> List.find (fun (file, _) -> System.IO.Path.GetFileName file = name) |> snd |> _.Diagnostics |> List.map _.Code
    Assert.Equal<string list>([ DiagnosticCodes.OldFormTypeParameter.Code ], codesOf "main.nyx")
    Assert.Equal<string list>([ DiagnosticCodes.OldFormTypeParameter.Code ], codesOf "old.nyx")
