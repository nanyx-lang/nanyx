/// A generic local function, or a lambda in one, that calls a local function requiring a parameterised context at one of
/// its own type variables passes the members it has for that variable, or requires the context itself if it is inferred
/// (#2813).
module TranspilerWasmTests.Features.Contexts.LocalContextCallers

open Xunit
open TranspilerWasmTestHelpers

/// A type whose own `==` holds any two equal, which a comparison by structure gets wrong.
let private email = "type Email = protected string\n\ndef Email.`==`: Email, Email -> bool = { a, b -> true }\n\n"

let private runs (body: string) = runSource ("@entry\ndef main = {\n" + body + "}\n")

[<Fact>]
let ``An inferred local calls a local [Eq(a)] function at its own variable`` () =
    runs "  def eq: [Eq(a)] a, a -> bool = { x, y -> x == y }\n  def twice = { v -> eq(v, v) }\n  println(twice(\"a\"))\n"
    |> assertRan "true\n"

[<Fact>]
let ``An inferred local of two parameters calls a local [Eq(a)] function at its own variable`` () =
    runs "  def eq: [Eq(a)] a, a -> bool = { x, y -> x == y }\n  def same = { x, y -> eq(x, y) }\n  println(same(\"a\", \"a\"))\n  println(same(1, 2))\n"
    |> assertRan "true\nfalse\n"

[<Fact>]
let ``A lambda in an inferred local calls a local [Eq(a)] function at the local's variable`` () =
    runs "  def eq: [Eq(a)] a, a -> bool = { x, y -> x == y }\n  def outer = { s -> { t -> eq(s, t) } }\n  println(outer(\"a\")(\"a\"))\n"
    |> assertRan "true\n"

[<Fact>]
let ``A lambda in a local that declares [Eq(b)] calls a local [Eq(a)] function`` () =
    runs "  def eq: [Eq(a)] a, a -> bool = { x, y -> x == y }\n  def outer: [Eq(b)] b -> (b -> bool) = { s -> { t -> eq(s, t) } }\n  println(outer(\"a\")(\"a\"))\n"
    |> assertRan "true\n"

[<Fact>]
let ``An inferred local that calls a local [Eq(a)] function compares with a type's own ==`` () =
    runSource (
        email
        + "@entry\ndef main = {\n  def eq: [Eq(a)] a, a -> bool = { x, y -> x == y }\n  def same = { x, y -> eq(x, y) }\n  def twice = { v -> eq(v, v) }\n  def outer = { s -> { t -> eq(s, t) } }\n  println(same(Email(\"a\"), Email(\"b\")))\n  println(twice(Email(\"a\")))\n  println(outer(Email(\"a\"))(Email(\"b\")))\n}\n"
    )
    |> assertRan "true\ntrue\ntrue\n"

[<Fact>]
let ``A lambda in an inferred local calls a top-level [Eq(a)] function at the local's variable`` () =
    runSource "def eq: [Eq(a)] a, a -> bool = { x, y -> x == y }\n\n@entry\ndef main = {\n  def outer = { s -> { t -> eq(s, t) } }\n  println(outer(\"a\")(\"a\"))\n  println(outer(1)(2))\n}\n"
    |> assertRan "true\nfalse\n"

[<Fact>]
let ``A lambda two locals deep calls a local [Eq(a)] function at the outer local's variable`` () =
    runs "  def eq: [Eq(a)] a, a -> bool = { x, y -> x == y }\n  def outer = { s ->\n    def inner = { t -> { u -> eq(s, u) and eq(t, u) } }\n    inner(s)\n  }\n  println(outer(\"a\")(\"a\"))\n  println(outer(1)(2))\n"
    |> assertRan "true\nfalse\n"

[<Fact>]
let ``A lambda in a local that declares [Eq(c)], in one that declares [Eq(b)], calls a local [Eq(a)] function`` () =
    // `eq` is used in `inner` at `inner`'s own variable, so `outer` captures it at each type `inner` is used at.
    runs "  def eq: [Eq(a)] a, a -> bool = { x, y -> x == y }\n  def outer: [Eq(b)] b -> (b -> bool) = { s ->\n    def inner: [Eq(c)] c -> (c -> bool) = { t -> { u -> eq(t, u) } }\n    inner(s)\n  }\n  println(outer(\"a\")(\"a\"))\n  println(outer(1)(2))\n"
    |> assertRan "true\nfalse\n"
