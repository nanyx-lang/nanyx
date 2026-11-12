module Nanyx.Compiler.Tests.Features.BuiltinTypeNames

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The `NYX0114` errors `source` has, each as the text it is reported at and the source its fix makes.
let private notTypes (source: string) =
    let result = Compiler.compile source
    result.Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = DiagnosticCodes.NotABuiltInType.Code)
    |> List.map (fun diagnostic ->
        Assert.Equal(Severity.Error, diagnostic.Severity)
        let span = diagnostic.Span.Value
        source.Substring(span.Start, span.End - span.Start), applyFix source (List.exactlyOne diagnostic.Fixes))

[<Fact>]
let ``A definition attached to a built-in type is called by the type's alias`` () =
    let typed = compileClean "def n = int.fromString(\"42\")\ndef s = string.toString(\"ab\")\ndef xs = list.map([1], { x -> x + 1 })"
    Assert.Equal(TyApply("list", [ TyPrimitive "int" ]), typed.Types.["xs"])

[<Fact>]
let ``A definition attached under the type's alias is reached by a pipe`` () =
    let typed = compileClean "def int.double = { n -> n * 2 }\ndef n = 3 \\double"
    Assert.Equal(TyPrimitive "int", typed.Types.["n"])
    Assert.Equal(TyFunc(TyPrimitive "int", TyPrimitive "int", []), typed.Types.["int.double"])

[<Fact>]
let ``An attached definition is used as a value by the type's alias`` () =
    let typed = compileClean "def show = int.toString"
    Assert.Equal(TyFunc(TyPrimitive "int", TyPrimitive "string", []), typed.Types.["show"])

[<Fact>]
let ``A value named as a built-in type is the value`` () =
    let typed = compileClean "def count = { (list: list(int)) -> list.length }"
    Assert.Equal(TyFunc(TyApply("list", [ TyPrimitive "int" ]), TyPrimitive "int", []), typed.Types.["count"])

[<Fact>]
let ``A capitalised built-in type is the type its alias names`` () =
    let typed =
        compileClean
            """def String.shout = { (s: string) -> s ++ "!" }
def a = String.shout("hi")
def b = string.shout("hi")
def c = "hi" \shout
def List.second = { (xs: List(int)) -> xs }
def d = list.second([1])
def u: Unit = ()
def e = String.toString("ab")
def f = List.map
def m: Map(string, int) = ["a" => 1]
def s: Set(int) = set.fromList([1])
def q = { (items: Seq(int)) -> items }"""
    Assert.Equal(TyPrimitive "string", typed.Types.["a"])
    Assert.Equal(TyPrimitive "string", typed.Types.["c"])
    Assert.Equal(TyFunc(TyPrimitive "string", TyPrimitive "string", []), typed.Types.["string.shout"])
    Assert.Equal(TyApply("list", [ TyPrimitive "int" ]), typed.Types.["d"])
    Assert.Equal(TyPrimitive "unit", typed.Types.["u"])
    Assert.Equal(TyFunc(TyApply("seq", [ TyPrimitive "int" ]), TyApply("seq", [ TyPrimitive "int" ]), []), typed.Types.["q"])

[<Fact>]
let ``I64, F64 and Boolean are the types int, float and bool name`` () =
    let typed =
        compileClean
            """def I64.double = { (n: int) -> n * 2 }
def a = I64.double(3)
def b = int.double(3)
def c: I64 = 3 \double
def total: I64, int -> I64 = { x, y -> x + y }
def half: F64 = 0.5
def F64.twice = { (x: F64) -> x * 2.0 }
def d = float.twice(half)
def yes: Boolean = true
def flags: List(Boolean) = [yes, false]
def show = I64.toString"""
    Assert.Equal(TyPrimitive "int", typed.Types.["a"])
    Assert.Equal(TyPrimitive "int", typed.Types.["b"])
    Assert.Equal(TyPrimitive "int", typed.Types.["c"])
    Assert.Equal(TyFunc(TyRecord(Map.ofList [ "0", TyPrimitive "int"; "1", TyPrimitive "int" ]), TyPrimitive "int", []), typed.Types.["total"])
    Assert.Equal(TyFunc(TyPrimitive "int", TyPrimitive "int", []), typed.Types.["int.double"])
    Assert.Equal(TyPrimitive "float", typed.Types.["d"])
    Assert.Equal(TyApply("list", [ TyPrimitive "bool" ]), typed.Types.["flags"])
    Assert.Equal(TyFunc(TyPrimitive "int", TyPrimitive "string", []), typed.Types.["show"])

[<Fact>]
let ``Int, Float and Bool are not types, and the fix writes the alias`` () =
    let source =
        """def Int.double = { n -> n * 2 }
def n = Int.fromString("4")
def m = 3 \Int.double
def show = Float.toString
def b: Bool = true""".ReplaceLineEndings "\n"
    Assert.Equal<(string * string) list>(
        [ "Int", source.Replace("def Int.double", "def int.double")
          "Int", source.Replace("Int.fromString", "int.fromString")
          "Int", source.Replace("\\Int.double", "\\int.double")
          "Float", source.Replace("Float.toString", "float.toString")
          "Bool", source.Replace(": Bool", ": bool") ],
        notTypes source)

[<Fact>]
let ``Int, Float and Bool are errors in a type and named on their own`` () =
    let source = "def xs: List(Int) = [1]\ndef f = Float(1)\ndef g = Bool"
    Assert.Equal<string list>([ "Int"; "Float"; "Bool" ], notTypes source |> List.map fst)

[<Fact>]
let ``An error for Int is the only one when the alias would check`` () =
    let result = Compiler.compile "def n: Int = 3"
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.NotABuiltInType.Code, diagnostic.Code)
    Assert.Equal("'Int' is not a type: write 'int'", diagnostic.Message)

[<Fact>]
let ``A type the module defines with a capitalised name is that type`` () =
    compileClean "type Set(a) = (items: list(a))\ndef Set.size = { (s: Set(int)) -> s.items.length }" |> ignore
    compileClean "type Int = (value: int)\ndef Int.get = { (i: Int) -> i.value }" |> ignore

[<Fact>]
let ``An imported module's capitalised type names are the types in the importer`` () =
    let result = compileImporting "module mod\nexport def List.triple = { (xs: list(int)) -> xs ++ xs ++ xs }" "import mod\ndef n = list.triple([2])"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``A definition attached to a name that is not a type is an error`` () =
    let diagnostic = Assert.Single((Compiler.compile "def Option.x = { n -> n }").Diagnostics)
    Assert.Equal(DiagnosticCodes.UnknownAttachedType.Code, diagnostic.Code)
