module Nanyx.Compiler.Tests.Features.Except

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The members of the union `name` is in `source`, each written out, or the one type when it is not a union.
let private membersOf (source: string) (name: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    match result.Typed.Value.Types.[name] with
    | TyUnion items -> items |> List.map Unifier.tyToString |> Set.ofList
    | ty -> Set.singleton (Unifier.tyToString ty)

let private parsed = "def parsed: #some(int) | #invalidInt = #invalidInt\n"

[<Fact>]
let ``except takes the tag it names out of the union and adds its arm's value`` () =
    let source = parsed + "def v = parsed except #invalidInt -> #invalidVersionNumber\n"
    Assert.Equal<Set<string>>(set [ "#some(int)"; "#invalidVersionNumber" ], membersOf source "v")

[<Fact>]
let ``an except arm may give a tag the union already has`` () =
    let source = parsed + "def v = parsed except #invalidInt -> #some(0)\n"
    Assert.Equal<Set<string>>(set [ "#some(int)" ], membersOf source "v")

[<Fact>]
let ``except takes a tag out of a tag's payload`` () =
    let source =
        "def f: () -> #some(int) | #error(#notFound | #io(string)) = { -> #some(1) }\n" +
        "def v = f() except #error(#notFound) -> #some(0)\n"
    Assert.Equal<Set<string>>(set [ "#some(int)"; "#error(#io(string))" ], membersOf source "v")

[<Fact>]
let ``a tag whose payload's tags are all taken out leaves the union`` () =
    let source =
        "def f: () -> #some(int) | #error(#notFound | #io(string)) = { -> #some(1) }\n" +
        "def v = f() except\n" +
        "  | #error(#notFound) -> #some(0)\n" +
        "  | #error(#io(message)) -> #some(1)\n"
    Assert.Equal<Set<string>>(set [ "#some(int)" ], membersOf source "v")

[<Fact>]
let ``except leaves one case for a destructuring def to take apart`` () =
    let source =
        "def f: () -> #some(int) | #error(#notFound) = { -> #some(1) }\n" +
        "def g = { ->\n" +
        "  def #some(result) = f() except #error(#notFound) -> #some(0)\n" +
        "  result\n" +
        "}\n"
    Assert.Equal<Set<string>>(set [ "() -> int" ], membersOf source "g")

[<Fact>]
let ``except passes through what a function is given more of`` () =
    let source = "def recover = { x -> x except #notFound -> #none }\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    match result.Typed.Value.Types.["recover"] with
    | TyFunc(TyUnion [ TyTag("notFound", None); TyVar input ], TyUnion output, []) ->
        Assert.Contains(TyTag("none", None), output)
        Assert.Contains(TyVar input, output)
    | other -> failwith $"Unexpected type: {Unifier.tyToString other}"

[<Fact>]
let ``an except with a catch-all arm is a match`` () =
    let source = parsed + "def v = parsed except | #invalidInt -> 0 | _ -> 1\n"
    Assert.Equal<Set<string>>(set [ "int" ], membersOf source "v")

[<Fact>]
let ``an except pattern the value can never match is an error`` () =
    let source = parsed + "def v = parsed except #outOfRange -> #some(0)\n"
    let result = Compiler.compile source
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    Assert.Contains("#outOfRange", diagnostic.Message)

[<Fact>]
let ``an except pattern of the wrong kind is an error`` () =
    let source = parsed + "def v = parsed except 0 -> #some(0)\n"
    let result = Compiler.compile source
    Assert.NotEmpty(result.Diagnostics)
    Assert.All(result.Diagnostics, fun diagnostic -> Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code))

[<Fact>]
let ``a name an except arm binds hovers as the payload it takes`` () =
    let source =
        "def f: () -> #some(int) | #error(#io(string)) = { -> #some(1) }\n" +
        "def v = f() except #error(#io(message)) -> #some(message)\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let hovered = Hover.typeAt Unicode result (offsetOf source "#some(message)" 0 6)
    Assert.Equal(Some "message: string", hovered |> Option.map _.Type)
