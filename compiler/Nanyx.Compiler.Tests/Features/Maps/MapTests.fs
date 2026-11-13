module Nanyx.Compiler.Tests.Features.Maps

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The type of each of `names`, as `source` compiled with no diagnostics gives it.
let private typesOf (source: string) (names: string list) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    names |> List.map (fun name -> Unifier.tyToString result.Typed.Value.Types.[name])

[<Fact>]
let ``A map literal is a map from its keys' type to its values' type`` () =
    let source = "def m = [\"a\" => 1, \"b\" => 2]\ndef byPoint = [(x = 1, y = 2) => [1], (x = 3, y = 4) => []]\n"
    Assert.Equal<string list>([ "map(string, int)"; "map((x: int, y: int), list(int))" ], typesOf source [ "m"; "byPoint" ])

[<Fact>]
let ``map(k, v) and Map(k, v) are the same type`` () =
    let source =
        "def m: map(string, int) = [\"a\" => 1]\n" +
        "def n: map(string, int) = m\n" +
        "def count: map(string, int) -> int = { x -> map.count(x) }\n" +
        "def c = count(m)\n"
    Assert.Equal<string list>([ "map(string, int)"; "map(string, int)"; "int" ], typesOf source [ "m"; "n"; "c" ])

[<Fact>]
let ``Calling a map is a lookup, which finds the value or #notFound`` () =
    let source =
        "def m = [\"a\" => 1]\n" +
        "def found = m(\"a\")\n" +
        "type Shop = (prices: map(string, float))\n" +
        "def price = { (shop: Shop) -> shop.prices(\"tea\") }\n"
    Assert.Equal<string list>([ "#some(int) | #notFound"; "Shop -> #some(float) | #notFound" ], typesOf source [ "found"; "price" ])

[<Fact>]
let ``A map's functions are attached to Map, so a pipe finds them`` () =
    let source = "def m = [\"a\" => 1] \\insert(\"b\", 2) \\remove(\"a\")\ndef n = m \\count\n"
    Assert.Equal<string list>([ "map(string, int)"; "int" ], typesOf source [ "m"; "n" ])

[<Fact>]
let ``List.groupBy gives a map from each key to its elements`` () =
    let source = "def groups = [\"a\", \"bb\", \"cc\"] \\list.groupBy { s -> s.length }\n"
    Assert.Equal<string list>([ "map(int, list(string))" ], typesOf source [ "groups" ])

[<Fact>]
let ``A lookup with a key of another type is a mismatch`` () =
    let code, message, _ = onlyDiagnostic "def m = [\"a\" => 1]\ndef found = m(2)\n"
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Contains("string", message)

[<Fact>]
let ``A key type with no equality is an error at the literal`` () =
    let code, message, _ = onlyDiagnostic "def m = [{ (x: int) -> x } => 1]\n"
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, code)
    Assert.Equal("'__map' compares values of type 'int -> int', which cannot be compared: functions have no equality", message)

[<Fact>]
let ``Maps and sets can be compared with ==, and so can what holds them`` () =
    let source =
        """type Shop = (prices: map(string, int))
def same = ["a" => 1] == ["a" => 1]
def differ = set.fromList([1]) != set.fromList([2])
def shops = Shop(prices = ["a" => 1]) == Shop(prices = ["a" => 1])
def generic: [Eq(k) & Eq(v)] map(k, v), map(k, v) -> bool = { m, n -> m == n }
"""
    let source = source.ReplaceLineEndings "\n"
    Assert.Equal<string list>([ "bool"; "bool"; "bool" ], typesOf source [ "same"; "differ"; "shops" ])

[<Fact>]
let ``A map whose values are functions cannot be compared with ==`` () =
    let code, message, _ = onlyDiagnostic "def same = [1 => { (x: int) -> x }] == [1 => { (x: int) -> x }]\n"
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, code)
    Assert.Equal("'map(int, (int -> int))' cannot be compared with ==: a value it holds is a function, and functions have no equality", message)

[<Fact>]
let ``A map compared by a local function is an error for now`` () =
    let local =
        """def same = {
  def local = { a, b -> a == b }
  local(["a" => 1], ["a" => 1])
}
"""
    let code, message, _ = onlyDiagnostic (local.ReplaceLineEndings "\n")
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, code)
    Assert.Equal("'local' compares values of type 'map(string, int)' with the compiler's own ==, which cannot compare a map or a set yet: a local function, or a function that compares a union's rest, is passed no other", message)

[<Fact>]
let ``A call generic in the types of the sets it compares is passed the == derived for them`` () =
    let source = "def count: [Eq(a)] list(set(a)) -> int = { xs -> list.distinct(xs) \\list.length }\n"
    Assert.Equal<string list>([ "list(set(a)) -> int" ], typesOf source [ "count" ])

[<Fact>]
let ``Maps and sets cannot be ordered or hashed`` () =
    let code, message, _ = onlyDiagnostic "def less = [\"a\" => 1] < [\"a\" => 1]\n"
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, code)
    Assert.Equal("'map(string, int)' cannot be ordered with <: maps have no order", message)
    let code, message, _ = onlyDiagnostic "def keyed = [set.fromList([1]) => 1]\n"
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, code)
    Assert.Equal("'__map' hashes values of type 'set(int)', which cannot be hashed: sets have no hash", message)

[<Fact>]
let ``A key whose type attaches == and no hash is an error at the literal`` () =
    let source =
        "type $Email = string\n" +
        "def $Email.`==`: $Email, $Email -> bool = { a, b -> true }\n" +
        "def m = [$Email(\"a\") => 1]\n"
    let code, message, _ = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, code)
    Assert.Equal("'__map' hashes values of type '$Email', which cannot be hashed: '$Email' defines its own ==, so its hash must agree with it: attach '$Email.hash'", message)

[<Fact>]
let ``set(a) and Set(a) are the same type, made by the Set functions`` () =
    let source =
        "def s = set.fromList([1, 2, 2])\n" +
        "def t: set(int) = s \\insert(3)\n" +
        "def u: set(string) = set.empty\n" +
        "def n = set.count(t)\n"
    Assert.Equal<string list>([ "set(int)"; "set(int)"; "set(string)"; "int" ], typesOf source [ "s"; "t"; "u"; "n" ])

[<Fact>]
let ``An element type with no equality is an error`` () =
    let code, message, _ = onlyDiagnostic "def s = set.fromList([{ (x: int) -> x }])\n"
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, code)
    Assert.Equal("'set.fromList' compares values of type 'int -> int', which cannot be compared: functions have no equality", message)