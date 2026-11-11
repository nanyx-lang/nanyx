module Nanyx.Compiler.Tests.Features.DeprecatedTypeNames

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The warnings `source` has, each as the text it is reported at and the source its fix makes.
let private warnings (source: string) =
    let result = Compiler.compile source
    Assert.True(result.TypeChecked, $"Unexpected diagnostics: %A{result.Diagnostics}")
    result.Diagnostics
    |> List.map (fun diagnostic ->
        Assert.Equal(DiagnosticCodes.DeprecatedTypeName.Code, diagnostic.Code)
        Assert.Equal(Severity.Warning, diagnostic.Severity)
        let span = diagnostic.Span.Value
        source.Substring(span.Start, span.End - span.Start), applyFix source (List.exactlyOne diagnostic.Fixes))

[<Fact>]
let ``A definition attached to a built-in type is called by the type's name`` () =
    let typed = compileClean "def n = int.fromString(\"42\")\ndef s = string.toString(\"ab\")\ndef xs = list.map([1], { x -> x + 1 })"
    Assert.Equal(TyApply("list", [ TyPrimitive "int" ]), typed.Types.["xs"])

[<Fact>]
let ``A definition attached under the type's name is reached by a pipe`` () =
    let typed = compileClean "def int.double = { n -> n * 2 }\ndef n = 3 \\double"
    Assert.Equal(TyPrimitive "int", typed.Types.["n"])
    Assert.Equal(TyFunc(TyPrimitive "int", TyPrimitive "int"), typed.Types.["int.double"])

[<Fact>]
let ``An attached definition is used as a value by the type's name`` () =
    let typed = compileClean "def show = int.toString"
    Assert.Equal(TyFunc(TyPrimitive "int", TyPrimitive "string"), typed.Types.["show"])

[<Fact>]
let ``A value named as a built-in type is the value`` () =
    let typed = compileClean "def count = { (list: list(int)) -> list.length }"
    Assert.Equal(TyFunc(TyApply("list", [ TyPrimitive "int" ]), TyPrimitive "int"), typed.Types.["count"])

[<Fact>]
let ``A capitalised type name before the dot warns, and its fix writes the type's name`` () =
    let source = "def Int.double = { n -> n * 2 }\ndef n = Int.fromString(\"4\")\ndef m = 3 \\Int.double"
    Assert.Equal<(string * string) list>(
        [ "Int", source.Replace("def Int.double", "def int.double")
          "Int", source.Replace("Int.fromString", "int.fromString")
          "Int", source.Replace("\\Int.double", "\\int.double") ],
        warnings source)

[<Fact>]
let ``A capitalised type name still means the type`` () =
    let result = Compiler.compile "def Int.double = { n -> n * 2 }\ndef n = 3 \\double\ndef s = String.toString(\"ab\")"
    Assert.True(result.TypeChecked, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["n"])
    Assert.Equal(TyFunc(TyPrimitive "int", TyPrimitive "int"), result.Typed.Value.Types.["int.double"])

[<Fact>]
let ``An attached definition used as a value by a capitalised type name warns`` () =
    let source = "def show = Int.toString"
    Assert.Equal<(string * string) list>([ "Int", "def show = int.toString" ], warnings source)

[<Fact>]
let ``A capitalised type in an annotation warns`` () =
    let source = "def xs: List(Int) = [1]\ndef m: Map(string, int) = [\"a\" => 1]"
    Assert.Equal<string list>([ "List"; "Int"; "Map" ], warnings source |> List.map fst)
    Assert.Equal(TyApply("list", [ TyPrimitive "int" ]), (Compiler.compile source).Typed.Value.Types.["xs"])

[<Fact>]
let ``A type the module defines with a capitalised name is that type`` () =
    compileClean "type Set(a) = (items: list(a))\ndef Set.size = { (s: Set(int)) -> s.items.length }" |> ignore

[<Fact>]
let ``An imported module's capitalised type names do not warn in the importer`` () =
    let result = compileImporting "export def Int.triple = { n -> n * 3 }" "import \"./mod\"\ndef n = int.triple(2)"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``A definition attached to a name that is not a type is an error`` () =
    let diagnostic = Assert.Single((Compiler.compile "def Option.x = { n -> n }").Diagnostics)
    Assert.Equal(DiagnosticCodes.UnknownAttachedType.Code, diagnostic.Code)
