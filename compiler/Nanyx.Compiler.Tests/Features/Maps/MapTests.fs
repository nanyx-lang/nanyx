module Nanyx.Compiler.Tests.Features.Maps

open Xunit
open NyxCompiler

/// The type of each of `names`, as `source` compiled with no diagnostics gives it.
let private typesOf (source: string) (names: string list) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    names |> List.map (fun name -> Unifier.tyToString result.Typed.Value.Types.[name])

/// The one diagnostic `source` has: its code and its message.
let private onlyDiagnostic (source: string) =
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] -> diagnostic.Code, diagnostic.Message
    | other -> failwith $"Expected one diagnostic, got %A{other}"

[<Fact>]
let ``A map literal is a map from its keys' type to its values' type`` () =
    let source = "def m = [\"a\" => 1, \"b\" => 2]\ndef byPoint = [(x = 1, y = 2) => [1], (x = 3, y = 4) => []]\n"
    Assert.Equal<string list>([ "map(string, int)"; "map((x: int, y: int), list(int))" ], typesOf source [ "m"; "byPoint" ])

[<Fact>]
let ``map(k, v) and Map(k, v) are the same type`` () =
    let source =
        "def m: map(string, int) = [\"a\" => 1]\n" +
        "def n: Map(string, int) = m\n" +
        "def count: Map(string, int) -> int = { x -> Map.count(x) }\n" +
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
    let source = "def groups = [\"a\", \"bb\", \"cc\"] \\List.groupBy { s -> s.length }\n"
    Assert.Equal<string list>([ "map(int, list(string))" ], typesOf source [ "groups" ])

[<Fact>]
let ``A lookup with a key of another type is a mismatch`` () =
    let code, message = onlyDiagnostic "def m = [\"a\" => 1]\ndef found = m(2)\n"
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Contains("string", message)

[<Fact>]
let ``A key type with no equality is an error at the literal`` () =
    let code, message = onlyDiagnostic "def m = [{ (x: int) -> x } => 1]\n"
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, code)
    Assert.Equal("'__map' compares values of type 'int -> int', which cannot be compared: functions have no equality", message)

[<Fact>]
let ``Maps cannot be compared with == yet (#618)`` () =
    let code, message = onlyDiagnostic "def same = [\"a\" => 1] == [\"a\" => 1]\n"
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, code)
    Assert.Equal("'map(string, int)' cannot be compared with ==: maps have no equality", message)

[<Fact>]
let ``A key whose type attaches == and no hash is an error at the literal`` () =
    let source =
        "type $Email = string\n" +
        "def $Email.`==`: $Email, $Email -> bool = { a, b -> true }\n" +
        "def m = [$Email(\"a\") => 1]\n"
    let code, message = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, code)
    Assert.Equal("'__map' hashes values of type '$Email', which cannot be hashed: '$Email' defines its own ==, so its hash must agree with it: attach '$Email.hash'", message)

[<Fact>]
let ``set(a) and Set(a) are the same type, made by the Set functions`` () =
    let source =
        "def s = Set.fromList([1, 2, 2])\n" +
        "def t: Set(int) = s \\insert(3)\n" +
        "def u: set(string) = Set.empty\n" +
        "def n = Set.count(t)\n"
    Assert.Equal<string list>([ "set(int)"; "set(int)"; "set(string)"; "int" ], typesOf source [ "s"; "t"; "u"; "n" ])

[<Fact>]
let ``An element type with no equality is an error, and sets cannot be compared with == yet`` () =
    let code, message = onlyDiagnostic "def s = Set.fromList([{ (x: int) -> x }])\n"
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, code)
    Assert.Equal("'Set.fromList' compares values of type 'int -> int', which cannot be compared: functions have no equality", message)
    let code, message = onlyDiagnostic "def same = Set.fromList([1]) == Set.fromList([1])\n"
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, code)
    Assert.Equal("'set(int)' cannot be compared with ==: sets have no equality", message)