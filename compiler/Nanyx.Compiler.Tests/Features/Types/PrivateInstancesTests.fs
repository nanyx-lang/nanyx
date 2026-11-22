module Nanyx.Compiler.Tests.Features.Types.PrivateInstances

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A private type has `==`, an order and a hash only if it attaches them: none is derived from its hidden
// representation, in its own module or out of it, nor for a structure that holds one (#2325).

let private ids =
    """module ids =
  export type UserId = private int
  export def UserId.new: int -> UserId = { n -> UserId(n) }

def a = ids.UserId.new(1)
def b = ids.UserId.new(2)
"""

/// The code and message of the one diagnostic `use` gives after `ids`, and the text it is reported at.
let private errorOf (use': string) =
    let source = normalized (ids + use' + "\n")
    onlyDiagnostic source

let private noEquality = "private types without their own == have no equality"
let private noOrder = "private types without their own compare have no order"
let private noHash = "private types without their own hash have no hash"

[<Fact>]
let ``== and != on a private type that attaches no == are errors`` () =
    for op in [ "=="; "!=" ] do
        Assert.Equal(
            (DiagnosticCodes.MissingContextMembers.Code, $"'ids.UserId' cannot be compared with {op}: {noEquality}", $"a {op} b"),
            errorOf $"println(a {op} b)")

[<Fact>]
let ``Ordering a private type that attaches no compare is an error`` () =
    for op in [ "<"; "<="; ">"; ">=" ] do
        let code, message, at = errorOf $"println(a {op} b)"
        Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, code)
        Assert.Equal($"'ids.UserId' cannot be ordered with {op}: {noOrder}", message)
        Assert.Equal($"a {op} b", at)

[<Fact>]
let ``A function that requires a context of a private type that attaches none is an error`` () =
    let cases =
        [ "println([b, a] \\list.sort \\list.length)", noOrder
          "println([(id = b), (id = a)] \\list.sortBy({ r -> r.id }) \\list.length)", noOrder
          "println([a, b] \\list.contains(a))", noEquality
          "println(set[a, b] \\set.length)", noEquality
          "println([a => \"ada\"] \\map.get(a))", noEquality
          "def same: [Eq(t)] t, t -> bool = { x, y -> x == y }\nprintln(same(a, a))", noEquality
          "def bucket: [Hash(t)] t -> int = { x -> 0 }\nprintln(bucket(a))", noHash ]
    for use', reason in cases do
        // A map literal and the lookup in it each need the key's `==`, and each says so.
        let diagnostics = (Compiler.compile (normalized (ids + use' + "\n"))).Diagnostics
        Assert.NotEmpty(diagnostics)
        for diagnostic in diagnostics do
            Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
            Assert.EndsWith(reason, diagnostic.Message)

[<Fact>]
let ``A structure holding a private type that attaches no == has no derived ==`` () =
    Assert.Equal(
        (DiagnosticCodes.MissingContextMembers.Code,
         $"'(id: ids.UserId)' cannot be compared with ==: its field 'id' is the private type 'ids.UserId', and {noEquality}",
         "(id = a) == (id = b)"),
        errorOf "println((id = a) == (id = b))")

[<Fact>]
let ``A private type has no derived == in its own module either`` () =
    let source =
        normalized
            """type UserId = private int

def same: UserId, UserId -> bool = { a, b -> a == b }
"""
    let code, message, _ = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, code)
    Assert.EndsWith(noEquality, message)

[<Fact>]
let ``A private type that attaches ==, compare and hash is compared, ordered and hashed through them`` () =
    let source =
        normalized
            """module ids =
  export type UserId = private (n: int)
  export def UserId.new: int -> UserId = { n -> UserId(n = n) }
  def value: UserId -> int = { u -> u.n }
  export def UserId.`==`: UserId, UserId -> bool = { x, y -> value(x) == value(y) }
  export def UserId.compare: UserId, UserId -> #less | #equal | #greater = { x, y -> int.compare(value(y), value(x)) }
  export def UserId.hash: UserId -> int = { x -> value(x) }

def a = ids.UserId.new(1)
def b = ids.UserId.new(2)
println(a == b)
println(a < b)
println(set[a, b] \set.length)
println((id = a) == (id = a))
"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
