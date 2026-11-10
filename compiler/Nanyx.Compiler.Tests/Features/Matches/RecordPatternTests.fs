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

[<Fact>]
let ``A name that is not one of the record's fields is matched by position, and the record has none there`` () =
    let result = Compiler.compile "def f: (name: string, age: int) -> string = { r -> match r\n  | (name, years) -> name }\n"
    let diagnostic = Assert.Single result.Diagnostics
    Assert.StartsWith("Record field mismatch: {age, name} vs {1, name}.", diagnostic.Message)
