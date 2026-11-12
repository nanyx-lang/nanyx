module Nanyx.Compiler.Tests.Features.StdLib.StdLib

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``Typecheck accepts the Console context from nanyx/console`` () =
    let source =
        "import nanyx/console\n\n" +
        "def greet: [Console] string -> () = { name -> println(name) }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``println comes from nanyx/console and gives unit`` () =
    let result = Compiler.compile "import nanyx/console\n\ndef result = println(\"hi\")"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "unit", result.Typed.Value.Types.["result"])

[<Fact>]
let ``println takes any value with a ToString`` () =
    let source = """import nanyx/console

type Point = (x: int, y: int)

println(42)
println(1.5)
println(#some([1, 2]))
println((1, "two"))
println(Point(x = 1, y = 2))
"""
    compileClean source |> ignore

[<Fact>]
let ``println of a function says that functions have no toString`` () =
    let source = """import nanyx/console

def inc: int -> int = { n -> n + 1 }

println(inc)
"""
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
    Assert.Equal(
        "'println' requires the context 'ToString(int -> int)', and 'int -> int' is a function: functions have no toString",
        diagnostic.Message)

[<Fact>]
let ``println of a protected type without a toString names what to define`` () =
    let source = """import nanyx/console

type $Id = private int

println($Id(3))
"""
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
    Assert.Contains("'println' requires the context 'ToString($Id)'", diagnostic.Message)
    Assert.Contains("'$Id.toString' is not defined", diagnostic.Message)

[<Fact>]
let ``println is not in scope without importing nanyx/console`` () =
    let result = Compiler.compile "def main = { println(\"hi\") }"
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Unknown function 'println'", message)

[<Fact>]
let ``Standard library import is resolved without a file`` () =
    withTempDir (fun dir ->
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(mainPath, "import nanyx/console\n\nprintln(\"Hello, Nanyx!\")\n")
        let result = Compiler.compileFile mainPath
        Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
        // The module is compiled together with the program: the backend sees its definitions, not the import.
        let hasImport = result.Typed.Value.Module |> List.exists (function Kind (Import _) -> true | _ -> false)
        Assert.False(hasImport)
        let hasPrintln = result.Typed.Value.Module |> List.exists (function Kind (Def (ValueDef(_, "println", _, _))) -> true | _ -> false)
        Assert.True(hasPrintln))

[<Fact>]
let ``nanyx/console declares the Console context`` () =
    let source =
        "import nanyx/console\n" +
        "def greet: [Console] string -> () = { name -> println(name) }\n" +
        "def main = { greet(\"hi\") }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.True(result.Typed.Value.TypeDefs.ContainsKey "Console")

[<Fact>]
let ``nanyx/text exports string functions with their types`` () =
    let source =
        "import nanyx/text\n" +
        "def shout = string.uppercase(\"hi\")\n" +
        "def n = string.length(\"hi\")\n" +
        "def parts = string.split(\"a,b\", on = \",\")\n" +
        "def joined = string.join(parts, \"-\")\n" +
        "def yes = string.startsWith(\"nanyx\", \"nan\")\n" +
        "def found = string.indexOf(\"nanyx\", \"yx\")\n" +
        "def lastFound = string.lastIndexOf(\"nanyx\", \"n\")\n" +
        "def times = string.count(\"nanyx\", \"n\")\n" +
        "def replaced = string.replace(\"nanyx\", pattern = \"n\", replacement = \"N\")\n" +
        "def padded = string.padStart(\"7\", width = 3, fill = \"0\")\n" +
        "def blank = string.isBlank(\" \")\n" +
        "def allLines = string.lines(\"a\")\n" +
        "def allWords = string.words(\"a b\")"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let types = result.Typed.Value.Types
    let indexResult = TyUnion [ TyTag("some", Some (TyPrimitive "int")); TyTag("notFound", None) ]
    Assert.Equal(TyPrimitive "string", types.["shout"])
    Assert.Equal(TyPrimitive "int", types.["n"])
    Assert.Equal(TyApply("list", [ TyPrimitive "string" ]), types.["parts"])
    Assert.Equal(TyPrimitive "string", types.["joined"])
    Assert.Equal(TyPrimitive "bool", types.["yes"])
    Assert.Equal(indexResult, types.["found"])
    Assert.Equal(indexResult, types.["lastFound"])
    Assert.Equal(TyPrimitive "int", types.["times"])
    Assert.Equal(TyPrimitive "string", types.["replaced"])
    Assert.Equal(TyPrimitive "string", types.["padded"])
    Assert.Equal(TyPrimitive "bool", types.["blank"])
    Assert.Equal(TyApply("list", [ TyPrimitive "string" ]), types.["allLines"])
    Assert.Equal(TyApply("list", [ TyPrimitive "string" ]), types.["allWords"])

[<Fact>]
let ``nanyx/text rejects a wrongly typed argument`` () =
    let result = Compiler.compile "import nanyx/text\n\ndef bad = string.repeat(\"ab\", \"twice\")"
    Assert.False(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Standard library private helpers are not imported`` () =
    let result = Compiler.compile "import nanyx/text\n\ndef bad = indexFrom(\"abc\", \"b\", 0)"
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Unknown function 'indexFrom'", message)

[<Fact>]
let ``Compiler intrinsics are not in scope in user code`` () =
    let result = Compiler.compile "import nanyx/text\n\ndef bad = __nanyx_string_length(\"abc\")"
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Unknown function '__nanyx_string_length'", message)

[<Fact>]
let ``Standard library import with alias is qualified`` () =
    let source =
        "import nanyx/text as t\n" +
        "def shout = t.string.uppercase(\"hi\")"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "string", result.Typed.Value.Types.["shout"])
    let unqualified = Compiler.compile "import nanyx/text as t\n\ndef shout = string.uppercase(\"hi\")"
    Assert.False(unqualified.Diagnostics.IsEmpty)

[<Fact>]
let ``nanyx/text functions are attached to String`` () =
    let result = Compiler.compile "import nanyx/text\ndef s = \" a \"\ndef piped = s \\ trim\ndef called = string.trim(s)"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "string", result.Typed.Value.Types.["piped"])
    Assert.Equal(TyPrimitive "string", result.Typed.Value.Types.["called"])
    let bare = Compiler.compile "import nanyx/text\ndef x = trim(\" a \")"
    Assert.Contains("Unknown function 'trim'", (List.head bare.Diagnostics).Message)

[<Fact>]
let ``Unknown standard library module is an error`` () =
    withTempDir (fun dir ->
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(mainPath, "import nanyx/sockets\n")
        let result = Compiler.compileFile mainPath
        let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
        Assert.Contains("Unknown standard library module 'nanyx/sockets'", message)
        Assert.Contains("nanyx/console", message))

[<Fact>]
let ``Entry point module sees nanyx/console without importing it`` () =
    let source =
        "module main\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(\"Hello, World!\")\n" +
        "}\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let withoutEntry = Compiler.compile "module main\n\ndef main = {\n  println(\"Hello, World!\")\n}\n"
    Assert.False(withoutEntry.Diagnostics.IsEmpty)

[<Fact>]
let ``nanyx/math exports int functions with their types`` () =
    let result =
        Compiler.compile (
            "import nanyx/math as math\n" +
            "def a = math.abs(-3)\n" +
            "def b = math.min(1, 2)\n" +
            "def c = math.max(1, 2)\n" +
            "def d = math.clamp(5, min = 0, max = 3)\n" +
            "def e = math.pow(2, 3)")
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    for name in [ "a"; "b"; "c"; "d"; "e" ] do
        Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.[name])

[<Fact>]
let ``nanyx/math abs, min, max and clamp give the type they are called with`` () =
    // #610: they took an `int` only.
    let result =
        Compiler.compile (
            "import nanyx/math as math\n" +
            "def a = math.abs(-2.5)\n" +
            "def b = math.min(1.5, 2.0)\n" +
            "def c = math.max(1.5, 2.0)\n" +
            "def d = math.clamp(1.5, min = 0.0, max = 1.0)\n" +
            "def e = math.min(\"pear\", \"apple\")\n" +
            "def f = math.clamp(\"m\", min = \"a\", max = \"f\")")
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    for name in [ "a"; "b"; "c"; "d" ] do
        Assert.Equal(TyPrimitive "float", result.Typed.Value.Types.[name])
    for name in [ "e"; "f" ] do
        Assert.Equal(TyPrimitive "string", result.Typed.Value.Types.[name])

[<Fact>]
let ``nanyx/math minMax gives both values at the type it is called with`` () =
    // #489
    let result =
        Compiler.compile (
            """
import nanyx/math as math
def a, b = math.minMax(3, 1)
def c, d = math.minMax(1.5, 2.0)
def e, f = math.minMax("pear", "apple")
"""
                .ReplaceLineEndings "\n")
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    for name, expected in [ "a", "int"; "b", "int"; "c", "float"; "d", "float"; "e", "string"; "f", "string" ] do
        Assert.Equal(TyPrimitive expected, result.Typed.Value.Types.[name])

[<Fact>]
let ``nanyx/math minMax of an int and a float is an error`` () =
    let mixed = Compiler.compile "import nanyx/math as math\n\ndef a = math.minMax(1, 2.5)"
    Assert.Equal<string list>([ DiagnosticCodes.TypeMismatch.Code ], mixed.Diagnostics |> List.map (fun d -> d.Code))

[<Fact>]
let ``nanyx/math abs of a string is an error, and so is min of an int and a float`` () =
    let notANumber = Compiler.compile "import nanyx/math as math\n\ndef a = math.abs(\"x\")"
    Assert.Equal<string list>([ DiagnosticCodes.MissingContextMembers.Code ], notANumber.Diagnostics |> List.map (fun d -> d.Code))
    let mixed = Compiler.compile "import nanyx/math as math\n\ndef a = math.min(1, 2.5)"
    Assert.Equal<string list>([ DiagnosticCodes.TypeMismatch.Code ], mixed.Diagnostics |> List.map (fun d -> d.Code))

[<Fact>]
let ``nanyx/math exports float functions with their types`` () =
    let result =
        Compiler.compile (
            "import nanyx/math as math\n" +
            "def s = math.sqrt(81.0)\n" +
            "def f = math.floor(3.9)\n" +
            "def c = math.ceil(3.1)\n" +
            "def r = math.round(3.5)\n" +
            "def t = math.truncate(-3.9)")
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal(TyPrimitive "float", result.Typed.Value.Types.["s"])
    for name in [ "f"; "c"; "r"; "t" ] do
        Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.[name])

[<Fact>]
let ``nanyx/text rejects a labelled argument passed by position`` () =
    let result = Compiler.compile "import nanyx/text\n\ndef parts = string.split(\"a,b\", \",\")"
    let diagnostic = List.exactlyOne result.Diagnostics
    Assert.Equal(DiagnosticCodes.PositionalForNamedArgument.Code, diagnostic.Code)
    Assert.Equal("'string.split' takes 'on' by name, not by position: write 'on = ...'", diagnostic.Message)
    let piped = List.exactlyOne (Compiler.compile "import nanyx/text\n\ndef parts = \"a,b\" \\split(\",\")").Diagnostics
    Assert.Equal(diagnostic.Message, piped.Message)
    let named = Compiler.compile "import nanyx/text\n\ndef parts = string.split(\"a,b\", on = \",\")"
    Assert.True(named.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{named.Diagnostics}")

[<Fact>]
let ``nanyx/text reports a labelled argument passed by position once, not in the defs after it`` () =
    // As `shop.nyx` wrote it before `padStart`'s `width` and `fill` had to be named: the matches after it were
    // reported as errors too.
    let source =
        "import nanyx/text\n\n" +
        "def pad = { n -> string.padStart(\"{n}\", 2, \"0\") }\n" +
        "def quantity = { s -> match int.fromString(s)\n" +
        "  | #some(n) -> n\n" +
        "  | #invalidInt -> 0\n" +
        "}\n"
    let diagnostic = List.exactlyOne (Compiler.compile source).Diagnostics
    Assert.Equal(DiagnosticCodes.PositionalForNamedArgument.Code, diagnostic.Code)
    Assert.Equal("'string.padStart' takes 'width' and 'fill' by name, not by position: write 'width = ...' and 'fill = ...'", diagnostic.Message)
    Assert.Equal(2, diagnostic.Span.Value.StartLine)
