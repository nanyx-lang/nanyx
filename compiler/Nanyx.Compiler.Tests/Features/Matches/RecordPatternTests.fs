module Nanyx.Compiler.Tests.Features.RecordPatterns

open Xunit
open NyxCompiler

// Record patterns: a name alone in a pattern takes the record's field of that name, as `name = name`.

/// The type of each local the compiled `source` declares by `name`.
let private typesOf (source: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    TypeHints.inlayHints Unicode "" result |> List.map (fun hint -> hint.Name, hint.Type)

[<Fact>]
let ``A name alone in a pattern binds the field of that name`` () =
    let source =
        "def describe: (name: string, age: int) -> string = { r -> match r\n" +
        "  | (age, name) -> \"{name} is {age}\" }\n"
    Assert.Equal<(string * string) list>(
        [ "r", "(age: int, name: string)"; "age", "int"; "name", "string" ], typesOf source)

[<Fact>]
let ``A tag's named payload is taken apart by name, in a match and in a lambda's parameter`` () =
    let source =
        "def a: #some(x: string, y: int) = #some(x = \"\", y = 1)\n" +
        "def f: typeof a -> string = { #some(x, y) -> \"{x}{y}\" }\n" +
        "def g: typeof a -> string = { t -> match t\n" +
        "  | #some(y, x) -> \"{x}{y}\" }\n"
    Assert.Equal<(string * string) list>(
        [ "x", "string"; "y", "int"; "t", "#some(x: string, y: int)"; "y", "int"; "x", "string" ], typesOf source)

[<Fact>]
let ``A tuple pattern still matches a tuple by position`` () =
    let source = "def f: (string, int) -> string = { p -> match p\n  | (s, n) -> s }\n"
    Assert.Equal<(string * string) list>([ "p", "(string, int)"; "s", "string"; "n", "int" ], typesOf source)

// A record type is closed, so a pattern by member name names every field, or ends in `...` to leave the others out.
[<Fact>]
let ``A record pattern that leaves a field out without ... is an error that names it`` () =
    let result = Compiler.compile "def f: (name: string, age: int) -> string = { r -> match r\n  | (name = n) -> n }\n"
    let diagnostic = Assert.Single result.Diagnostics
    Assert.StartsWith("Record field mismatch: {age, name} vs {name}: only the first has age.", diagnostic.Message)

[<Fact>]
let ``A record pattern that ends in ... leaves the other fields out`` () =
    let source =
        "def f: (name: string, age: int) -> string = { r -> match r\n" +
        "  | (name = \"Alice\", ...) -> \"A\"\n" +
        "  | (name = n, age = _) -> n }\n"
    Assert.Equal<(string * string) list>([ "r", "(age: int, name: string)"; "n", "string" ], typesOf source)

[<Fact>]
let ``A record pattern on a value of no known type yet takes exactly its fields, or at least them with ...`` () =
    let exact = "def f = { r -> match r\n  | (x = 1) -> 1\n  | _ -> 0 }\n"
    let atLeast = "def f = { r -> match r\n  | (x = 1, ...) -> 1\n  | _ -> 0 }\n"
    let wider = "def n = f((x = 1, y = 2))\n"
    Assert.Empty((Compiler.compile (exact + "def n = f((x = 1))\n")).Diagnostics)
    let diagnostic = Assert.Single (Compiler.compile (exact + wider)).Diagnostics
    Assert.StartsWith("Record field mismatch:", diagnostic.Message)
    Assert.Contains("y", diagnostic.Message)
    let result = Compiler.compile (atLeast + wider)
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)

[<Fact>]
let ``A name that is not one of the record's fields is matched by position, and the record has none there`` () =
    let result = Compiler.compile "def f: (name: string, age: int) -> string = { r -> match r\n  | (name, years) -> name }\n"
    let diagnostic = Assert.Single result.Diagnostics
    Assert.StartsWith("Record field mismatch: {age, name} vs {1, name}: only the first has age; only the second has 1.", diagnostic.Message)
