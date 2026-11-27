module Nanyx.Compiler.Tests.Features.RecordPatterns

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// Record patterns: a name alone in a pattern takes the record's field of that name, as `name = name`.

/// The type of each local the compiled `source` declares by `name`.
let private localHintsOf (source: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    localInlayHints Unicode "" result |> List.map (fun hint -> hint.Name, hint.Type)

[<Fact>]
let ``A name alone in a pattern binds the field of that name`` () =
    let source =
        normalized """def describe: (name: string, age: int) -> string = { r -> match r
  | (age, name) -> "{name} is {age}" }
"""
    Assert.Equal<(string * string) list>(
        [ "r", "(name: string, age: int)"; "age", "int"; "name", "string" ], localHintsOf source)

[<Fact>]
let ``A tag's named payload is taken apart by name, in a match and in a lambda's parameter`` () =
    let source =
        normalized """def a: #some(x: string, y: int) = #some(x = "", y = 1)
def f: typeof a -> string = { #some(x, y) -> "{x}{y}" }
def g: typeof a -> string = { t -> match t
  | #some(y, x) -> "{x}{y}" }
"""
    Assert.Equal<(string * string) list>(
        [ "x", "string"; "y", "int"; "t", "#some(x: string, y: int)"; "y", "int"; "x", "string" ], localHintsOf source)

[<Fact>]
let ``A tuple pattern still matches a tuple by position`` () =
    let source = "def f: (string, int) -> string = { p -> match p\n  | (s, n) -> s }\n"
    Assert.Equal<(string * string) list>([ "p", "(string, int)"; "s", "string"; "n", "int" ], localHintsOf source)

// A record type is closed, so a pattern by member name names every field, or ends in `...` to leave the others out.
[<Fact>]
let ``A record pattern that leaves a field out without ... is an error that names it`` () =
    let result = Compiler.compile "def f: (name: string, age: int) -> string = { r -> match r\n  | (name = n) -> n }\n"
    let diagnostic = Assert.Single result.Diagnostics
    Assert.StartsWith("Record field mismatch:", diagnostic.Message)
    Assert.EndsWith("where only the first has 'age'", diagnostic.Message)

[<Fact>]
let ``A record pattern that ends in ... leaves the other fields out`` () =
    let source =
        normalized """def f: (name: string, age: int) -> string = { r -> match r
  | (name = "Alice", ...) -> "A"
  | (name = n, age = _) -> n }
"""
    Assert.Equal<(string * string) list>([ "r", "(name: string, age: int)"; "n", "string" ], localHintsOf source)

[<Fact>]
let ``A record pattern that ends in ... and names a field the record lacks says which record has which fields`` () =
    let result = Compiler.compile "def f: (width: float, height: float) -> float = {\n  | (foo = w, ...) -> w\n}\n"
    let diagnostic = Assert.Single result.Diagnostics
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    Assert.Equal(
        "Record field mismatch: '(height: float, width: float)' vs '(foo: float, ...r)', where only the first has 'height', 'width' and only the second has 'foo'",
        diagnostic.Message)

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

// A pattern of fields without `=` may end in `...`: each reads as it would without it, a name alone by name where the
// value is known to have that field and anything else by position, and the other fields are left out.
[<Fact>]
let ``A name alone before ... binds the field of that name and leaves the others out`` () =
    let source =
        normalized """def describe: (name: string, age: int, email: string) -> string = { r -> match r
  | (age, ...) -> "{age}" }
def area: #rectangle(width: float, height: float, label: string) -> float = { #rectangle(height, width, ...) -> width * height }
"""
    Assert.Equal<(string * string) list>(
        [ "r", "(name: string, age: int, email: string)"; "age", "int"; "height", "float"; "width", "float" ],
        localHintsOf source)

[<Fact>]
let ``A pattern by position that ends in ... matches a tuple's first elements`` () =
    let source = "def f: (string, int, bool) -> int = { p -> match p\n  | (_, n, ...) -> n }\n"
    Assert.Equal<(string * string) list>([ "p", "(string, int, bool)"; "n", "int" ], localHintsOf source)

[<Fact>]
let ``A pattern by position that ends in ... on a value of no known type yet takes a tuple of at least that many`` () =
    let source = "def second = { (_, b, ...) -> b }\ndef n: int = second((1, 2, 3))\ndef m: string = second((1, \"two\"))\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)

[<Fact>]
let ``A name before ... that the record has no field of matches by position, which a record of named fields has none of`` () =
    let result = Compiler.compile "def f: (width: float, height: float) -> float = { r -> match r\n  | (w, ...) -> w }\n"
    let diagnostic = Assert.Single result.Diagnostics
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    Assert.StartsWith("Record field mismatch:", diagnostic.Message)

// A list pattern's elements match the list's element type, so a member pattern in one finds the record's fields (#461).
[<Fact>]
let ``Record patterns in two elements of a list pattern match different tags of a field's union`` () =
    let source =
        """
type Result =
  | #some(int)
  | #error(string)

type Request = (id: int, status: Result)

def f: list(Request) -> string = { requests ->
  match requests
    | [(status = #error(msg), ...), (status = #some(v), ...), ...tail] -> "{msg} {v} {tail.length}"
    | _ -> "other"
}
"""
    Assert.Equal<(string * string) list>(
        [ "requests", "list(Request)"; "msg", "string"; "v", "int"; "tail", "list(Request)" ], localHintsOf source)

[<Fact>]
let ``A record pattern in a list pattern's element binds the field's type`` () =
    let source =
        """
def f: list((name: string, age: int)) -> int = { people ->
  match people
    | [(age = a, ...), (name = n, age = b)] -> a + b + n.length
    | _ -> 0
}
"""
    Assert.Equal<(string * string) list>(
        [ "people", "list(name: string, age: int)"; "a", "int"; "n", "string"; "b", "int" ], localHintsOf source)

[<Fact>]
let ``A name that is not one of the record's fields is matched by position, and the record has none there`` () =
    let result = Compiler.compile "def f: (name: string, age: int) -> string = { r -> match r\n  | (name, years) -> name }\n"
    let diagnostic = Assert.Single result.Diagnostics
    Assert.StartsWith("Record field mismatch:", diagnostic.Message)
    Assert.EndsWith("where only the first has 'age' and only the second has '1'", diagnostic.Message)
