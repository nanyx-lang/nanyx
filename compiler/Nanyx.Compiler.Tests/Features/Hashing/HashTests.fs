module Nanyx.Compiler.Tests.Features.Hashing

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// A function generic in what it hashes, which requires `Hash` of it.
let private hashing = "def h: [Hash(a)] a -> int = { x -> hash(x) }\n"

/// A nominal type that attaches its own `==`, and `hash` when `withHash` is set.
let private email (withHash: bool) =
    "type Email = protected string\n" +
    "def Email.`==`: Email, Email -> bool = { a, b -> true }\n" +
    (if withHash then "def Email.hash: Email -> int = { e -> 7 }\n" else "")

[<Fact>]
let ``A call that requires Hash of a type with no hash of its own is passed the compiler's own`` () =
    let typed = compileClean (hashing + (withFinalNewline """def a = h(1)
def b = h((name = "x", tags = [#red]))"""))
    Assert.Equal<ContextInstance list>([ InstanceSynthesized [ "hash", "__hash" ]; InstanceSynthesized [ "hash", "__hash" ] ], instancesOf typed)

[<Fact>]
let ``A call that requires Hash of a nominal type that attaches hash is passed it`` () =
    let typed = compileClean (hashing + email true + (withFinalNewline """def a = h(Email("x"))"""))
    Assert.Equal<ContextInstance list>([ InstanceSynthesized [ "hash", "Email.hash" ] ], instancesOf typed)

[<Fact>]
let ``A type that attaches == and no hash has no Hash, and nor has anything that holds it`` () =
    let message, at = missingContext (hashing + email false + (withFinalNewline """def a = h(Email("x"))"""))
    Assert.Equal("'h' hashes values of type 'Email', which cannot be hashed: 'Email' defines its own ==, so its hash must agree with it: attach 'Email.hash'", message)
    Assert.Equal("h", at)
    let message, _ = missingContext (hashing + email false + (withFinalNewline """def a = h([Email("x")])"""))
    Assert.Equal("'h' hashes values of type 'list(Email)', which cannot be hashed: 'Email' defines its own ==, so its hash must agree with it: attach 'Email.hash'", message)

[<Fact>]
let ``A structure that holds a part with its own hash is passed a hash derived from its parts`` () =
    let typed = compileClean (hashing + email true + (withFinalNewline """def a = h([Email("x")])
def b = h((who = Email("x"), n = 1))"""))
    // The derived definitions' own calls, which hash each part, come after the module's.
    match instancesOf typed |> List.truncate 2 with
    | [ InstanceSynthesized [ "hash", list ]; InstanceSynthesized [ "hash", record ] ] ->
        Assert.StartsWith("__hash_", list)
        Assert.StartsWith("__hash_", record)
    | other -> failwith $"Expected two derived hashes, got %A{other}"

[<Fact>]
let ``Functions have no hash`` () =
    let message, _ = missingContext (hashing + "def a = h({ (x: int) -> x })\n")
    Assert.Equal("'h' hashes values of type 'int -> int', which cannot be hashed: functions have no hash", message)
    let message, _ = missingContext (hashing + (withFinalNewline """def a = h((name = "x", onClick = { (x: int) -> x }))"""))
    Assert.Equal("'h' hashes values of type '(name: string, onClick: (int -> int))', which cannot be hashed: its field 'onClick' is a function, and functions have no hash", message)

[<Fact>]
let ``A def that hashes values of its type variable requires Hash of it`` () =
    let message, _ = missingContext (hashing + "def g: a -> int = { x -> h(x) }\n")
    Assert.Equal("'h' requires the context 'Hash(a)', which is not available here: add [Hash(a)] to the type of 'g'", message)
    let typed = compileClean (hashing + "def g = { x -> h([x]) }\ndef n = g(1)\n")
    match typed.InferredContextTypes |> Map.values |> List.ofSeq with
    | [ [ TyApply("Hash", [ TyVar _ ]) ] ] -> ()
    | other -> failwith $"Unexpected inferred contexts: %A{other}"

[<Fact>]
let ``Only a nominal type may attach hash`` () =
    match (Compiler.compile "type Point = (x: int, y: int)\ndef Point.hash: Point -> int = { p -> p.x }\n").Diagnostics with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.UnattachableDefinition.Code, diagnostic.Code)
        Assert.Equal("'Point.hash' cannot be defined: 'Point' is a structural type, so its hash is always that of its structure. Declare it `protected` to define its own hash", diagnostic.Message)
    | other -> failwith $"Expected one diagnostic, got %A{other}"
