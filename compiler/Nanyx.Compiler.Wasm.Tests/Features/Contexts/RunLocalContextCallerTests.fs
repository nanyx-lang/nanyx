/// A generic local function, or a lambda in one, that calls a local function requiring a parameterised context at one of
/// its own type variables passes the members it has for that variable, or requires the context itself if it is inferred
/// (#2813). An inferred local function's own operator on one of its variables, `a == b`, uses the type's own `==` too
/// (#2809, #2815).
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

/// A generic nominal type whose own `==` ignores the order of its members, and a union that holds it.
let private objects =
    "type Obj(a) = protected (members: list((string, a)))\n\ndef Obj.`==`: [Eq(a)] Obj(a), Obj(a) -> bool = { x, y ->\n  list.all(x.members, { m -> list.contains(y.members, m) }) and x.members.length == y.members.length\n}\n\ntype J = #s(string) | #o(Obj(J))\n\n"

let private objectsIn (body: string) =
    runSource (
        objects
        + "@entry\ndef main = {\n  def p = Obj(members = [(\"a\", #s(\"1\")), (\"b\", #s(\"2\"))])\n  def q = Obj(members = [(\"b\", #s(\"2\")), (\"a\", #s(\"1\"))])\n  def jp: J = #o(p)\n  def jq: J = #o(q)\n"
        + body
        + "}\n"
    )

[<Fact>]
let ``An inferred local's == on its own variable compares with a type's own == (#2809)`` () =
    objectsIn "  def same = { a, b -> a == b }\n  println(same(jp, jq))\n  println(same(p, q))\n  println(same(1, 1))\n  println(same(1, 2))\n"
    |> assertRan "true\ntrue\ntrue\nfalse\n"

[<Fact>]
let ``A local [Eq(t)] function, declared or inferred, compares an Obj(J) with Obj's own == (#2815)`` () =
    objectsIn "  def declared: [Eq(t)] t, t -> bool = { a, b -> a == b }\n  def inferred = { a, b -> a == b }\n  println(declared(p, q))\n  println(declared(jp, jq))\n  println(inferred(p, q))\n  println(inferred(jp, jq))\n"
    |> assertRan "true\ntrue\ntrue\ntrue\n"

[<Fact>]
let ``An inferred local's != and < on its own variable use a type's own == and compare`` () =
    runSource
        "type Rev = protected (n: int)\n\ndef Rev.compare: Rev, Rev -> #less | #equal | #greater = { a, b -> int.compare(b.n, a.n) }\n\ndef Rev.`==`: Rev, Rev -> bool = { a, b -> true }\n\n@entry\ndef main = {\n  def less = { a, b -> a < b }\n  def differ = { a, b -> a != b }\n  println(less(Rev(n = 1), Rev(n = 2)))\n  println(less(1, 2))\n  println(differ(Rev(n = 1), Rev(n = 2)))\n  println(differ(1, 2))\n}\n"
    |> assertRan "false\ntrue\nfalse\ntrue\n"
