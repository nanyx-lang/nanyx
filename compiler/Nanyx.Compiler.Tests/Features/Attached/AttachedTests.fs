module Nanyx.Compiler.Tests.Features.Attached

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``Typecheck dotted def name`` () =
    let source =
        "type Person = (firstName: string)\n" +
        "def Person.fullName: Person -> string = { p -> p.firstName }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    match typed.Types.["Person.fullName"] with
    | TyFunc(TyAlias("Person", [], TyRecord fields), TyPrimitive "string") when fields.ContainsKey("firstName") ->
        Assert.True(true)
    | other -> Assert.True(false, $"Unexpected dotted def type: %A{other}")

[<Fact>]
let ``Typecheck ws-sensitive module block`` () =
    let source =
        "module Root\n" +
        "module Inner =\n" +
        "  def x = 1\n" +
        "  def y = x"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["Root.Inner.x"])
    Assert.Equal(TyPrimitive "int", typed.Types.["Root.Inner.y"])

[<Fact>]
let ``Typecheck infers attached receiver type for unannotated lambda`` () =
    let source =
        "type Person = (firstName: string)\n" +
        "def Person.toString = { p -> \"person\" }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    match typed.Types.["Person.toString"] with
    | TyFunc(TyAlias("Person", [], TyRecord fields), TyPrimitive "string") when fields.ContainsKey("firstName") ->
        Assert.True(true)
    | other -> Assert.True(false, $"Unexpected inferred attached def type: %A{other}")

[<Fact>]
let ``Typecheck infers nominal attached receiver type for unannotated lambda`` () =
    let source =
        "type $Email = string\n" +
        "def $Email.toString = { e -> \"ok\" }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    match typed.Types.["$Email.toString"] with
    | TyFunc(TyNominal(name, _, _), TyPrimitive "string") when name = "$Email" ->
        Assert.True(true)
    | other -> Assert.True(false, $"Unexpected inferred nominal attached def type: %A{other}")

[<Fact>]
let ``Typecheck infers nominal attached receiver as argument in body`` () =
    let source =
        "type $Email = string\n" +
        "def $Email.toDisplay = { e -> e }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    match typed.Types.["$Email.toDisplay"] with
    | TyFunc(TyNominal(nameIn, _, _), TyNominal(nameOut, _, _)) when nameIn = "$Email" && nameOut = "$Email" ->
        Assert.True(true)
    | other -> Assert.True(false, $"Unexpected inferred nominal identity attached def type: %A{other}")

[<Fact>]
let ``Typecheck rejects attached def with missing type`` () =
    let source = "def A.a = { s -> s }"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Unknown attached type", message)

[<Fact>]
let ``Typecheck allows attached defs on built-in types`` () =
    let source =
        "def Int.parse: string -> int = { s -> 0 }\n" +
        "def String.shout: string -> string = { s -> s }\n" +
        "def List.isBlank: list(int) -> bool = { xs -> true }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyFunc(TyPrimitive "string", TyPrimitive "int"), typed.Types.["Int.parse"])

[<Fact>]
let ``Typecheck infers primitive attached receiver type for unannotated lambda`` () =
    let source = "def Int.double = { n -> n * 2 }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyFunc(TyPrimitive "int", TyPrimitive "int"), typed.Types.["Int.double"])

[<Fact>]
let ``Typecheck pipe resolves attached functions on built-in types`` () =
    let source =
        "def Int.double: int -> int = { n -> n * 2 }\n" +
        "def n = 3 \\ double"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["n"])

[<Fact>]
let ``Typecheck pipe resolves attached functions on a type known only from earlier constraints`` () =
    let source =
        "def Int.double: int -> int = { n -> n * 2 }\n" +
        "def String.double: string -> string = { s -> s ++ s }\n" +
        "def id = { x -> x }\n" +
        "def n = id(3) \\ double\n" +
        "def s = id(\"a\") \\ double"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["n"])
    Assert.Equal(TyPrimitive "string", typed.Types.["s"])

[<Fact>]
let ``Typecheck pipe resolves attached functions on a lambda parameter pinned earlier in the body`` () =
    let source =
        "def Int.double: int -> int = { n -> n * 2 }\n" +
        "def String.double: string -> string = { s -> s ++ s }\n" +
        "def f = { x ->\n" +
        "  def y: int = x\n" +
        "  x \\ double\n" +
        "}"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyFunc(TyPrimitive "int", TyPrimitive "int"), typed.Types.["f"])

[<Fact>]
let ``Typecheck pipe resolves attached functions`` () =
    let source =
        "type Person = (firstName: string surname: string)\n" +
        "def Person.fullName: Person -> string = { p -> p.firstName }\n" +
        "def p: Person = (firstName = \"\", surname = \"\")\n" +
        "def n = p \\ Person.fullName\n" +
        "def n2 = p \\ fullName"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "string", typed.Types.["n"])
    Assert.Equal(TyPrimitive "string", typed.Types.["n2"])

[<Fact>]
let ``Typecheck pipe resolves attached functions on nominals`` () =
    let source =
        "type $Email = string\n" +
        "def $Email.toString: $Email -> string = { e -> \"\" }\n" +
        "def $Email.format: $Email -> string = { e -> \"\" }\n" +
        "def email: $Email = \"a@b.com\"\n" +
        "def s = email \\ toString\n" +
        "def s2 = email \\ format"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "string", typed.Types.["s"])
    Assert.Equal(TyPrimitive "string", typed.Types.["s2"])

[<Fact>]
let ``Typecheck pipe picks the attached function by the name of the value's type`` () =
    let source =
        "type Option(a) = #some(a) | #none\n" +
        "def List.map: (list(a), (a -> b)) -> list(b) = { xs, f -> [] }\n" +
        "def Option.map: (Option(a), (a -> b)) -> Option(b) = { o, f -> #none }\n" +
        "def xs: list(int) = [1, 2]\n" +
        "def o: Option(int) = #some(1)\n" +
        "def ys = xs \\ map({ x -> x > 1 })\n" +
        "def p = o \\ map({ x -> \"a\" })"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyApply("list", [ TyPrimitive "bool" ]), typed.Types.["ys"])
    match Unifier.unalias typed.Types.["p"] with
    | TyUnion options -> Assert.Contains(TyTag("some", Some (TyPrimitive "string")), options)
    | other -> Assert.True(false, $"Unexpected type for p: %A{other}")

[<Fact>]
let ``Typecheck pipe calls a plain function when the value's type attaches none`` () =
    let source =
        "def Int.describe: int -> string = { n -> \"int\" }\n" +
        "def describe: string -> string = { s -> s }\n" +
        "def a = \"x\" \\ describe\n" +
        "def b = 1 \\ describe"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "string", typed.Types.["a"])
    Assert.Equal(TyPrimitive "string", typed.Types.["b"])

[<Fact>]
let ``Typecheck pipe does not find attached functions of a module imported with as`` () =
    let modules = [ "one.nyx", "module one\nexport def String.shout: string -> string = { s -> s ++ \"!\" }" ]
    let unqualified = compileImportingAll modules "module main\nimport \"one.nyx\" as t\ndef a = \"x\" \\ shout"
    Assert.False(unqualified.Diagnostics.IsEmpty)
    let qualified = compileImportingAll modules "module main\nimport \"one.nyx\" as t\ndef a = \"x\" \\ t.String.shout"
    Assert.True(qualified.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{qualified.Diagnostics}")
    Assert.Equal(TyPrimitive "string", qualified.Typed.Value.Types.["a"])

[<Fact>]
let ``A string's and a list's length are built-in properties, read with a dot`` () =
    // No import: the property is the string's own, not `nanyx/text`'s `String.length`.
    let result =
        Compiler.compile (
            "def s = \"hello\"\n" +
            "def n = s.length\n" +
            "def xs = [1, 2, 3]\n" +
            "def m = xs.length\n" +
            "def size: string -> int = { text -> text.length }\n" +
            "def r = (length = \"field\")\n" +
            "def f = r.length\n")
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let types = result.Typed.Value.Types
    Assert.Equal(TyPrimitive "int", types.["n"])
    Assert.Equal(TyPrimitive "int", types.["m"])
    // A record's own field of that name is still the field.
    Assert.Equal(TyPrimitive "string", types.["f"])

/// The one diagnostic compiling `source` gives, as its code and message.
let private onlyDiagnostic (source: string) =
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    diagnostic.Code, diagnostic.Message

[<Fact>]
let ``A dot on a value does not reach a definition attached to its type, and the error says to pipe`` () =
    let code, message = onlyDiagnostic "import nanyx/text\ndef s = \"nanyx\"\ndef b = s.isEmpty\n"
    Assert.Equal(DiagnosticCodes.UnknownMember.Code, code)
    Assert.Equal("'string' has no property 'isEmpty'. 'String.isEmpty' is attached to its type: call it with a pipe, as in `s \\isEmpty`", message)
    // Called, it reads as a function named `s.startsWith`.
    let code, message = onlyDiagnostic "import nanyx/text\ndef s = \"nanyx\"\ndef b = s.startsWith(\"nan\")\n"
    Assert.Equal(DiagnosticCodes.UnknownFunction.Code, code)
    Assert.Equal("'s' is a value, so 's.startsWith' is not a function's name. 'String.startsWith' is attached to its type: call it with a pipe, as in `s \\startsWith(...)`", message)
    // A record type with an attached definition and no field of that name.
    let _, message =
        onlyDiagnostic "type Point = (x: int, y: int)\ndef Point.length = { p -> p.x + p.y }\ndef p = Point(x = 3, y = 4)\ndef n = p.length\n"
    Assert.Equal("Record 'p' has no member 'length'. 'Point.length' is attached to its type: call it with a pipe, as in `p \\length`", message)
    // Nothing attached: the property is just missing.
    Assert.Equal((DiagnosticCodes.UnknownMember.Code, "'string' has no property 'size'"), onlyDiagnostic "def s = \"x\"\ndef n = s.size\n")

[<Fact>]
let ``nanyx/text imported with as is only reached qualified from a pipe`` () =
    let qualified = Compiler.compile "import nanyx/text as t\ndef x = \" a \" \\ t.String.trim"
    Assert.True(qualified.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{qualified.Diagnostics}")
    let short = Compiler.compile "import nanyx/text as t\ndef x = \" a \" \\ trim"
    Assert.False(short.Diagnostics.IsEmpty)
