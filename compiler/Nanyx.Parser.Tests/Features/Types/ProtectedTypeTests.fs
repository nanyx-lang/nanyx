module ParserTests.Features.ProtectedTypes

open Xunit
open Parser.Program
open ParserTestHelpers

let private parsed (source: string) =
    match parseModule source with
    | Result.Ok module' -> module' |> List.map (fun item -> item.Kind)
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``protected after the = declares a protected type`` () =
    match parsed "type Email = protected string" with
    | [ Def (TypeDef ("Email", [ Protected ], [], Kind (TypeName "string"))) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A protected record type keeps its fields`` () =
    match parsed "export type User = protected (\n  name: string\n  age: int\n)" with
    | [ Def (TypeDef ("User", [ Protected; Export ], [], Kind (TypeRecord fields))) ] -> Assert.Equal(2, fields.Length)
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A scoped context is declared protected after its =`` () =
    match parsed "scoped export context Memory = protected ()" with
    | [ Def (TypeDef ("Memory", modifiers, [], _)) ] ->
        Assert.Contains(Protected, modifiers)
        Assert.Contains(Context, modifiers)
        Assert.True(TypeDefModifier.isNominal modifiers)
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A $ before a declared name declares a protected type, and is not part of the name`` () =
    match parsed "type $Email = string\ncontext $Memory = ()" with
    | [ Def (TypeDef ("Email", [ Protected ], [], _)); Def (TypeDef ("Memory", [ Protected; Context ], [], _)) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A $ before a private type's name declares only a private type`` () =
    match parsed "type $UserId = private int" with
    | [ Def (TypeDef ("UserId", [ Private ], [], _)) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A $ before a type's name where it is used is not part of the name`` () =
    match parsed "def wrap: string -> $Email = { s -> $Email(s) }\ndef $Email.parse = { s -> s }\ndef e: m.$Email = x" with
    | [ Def (ValueDef (_, "wrap", Some (Kind (TypeFunc (_, Kind (TypeName "Email")))), Kind (Lambda (_, Kind (FunctionCall ("Email", _, _)), _))))
        Def (ValueDef (_, "Email.parse", None, _))
        Def (ValueDef (_, "e", Some (Kind (TypeName "m.Email")), _)) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A type with no protected or private is not nominal`` () =
    match parsed "type Point = (x: int, y: int)" with
    | [ Def (TypeDef ("Point", modifiers, [], _)) ] -> Assert.False(TypeDefModifier.isNominal modifiers)
    | other -> failwith $"Unexpected parse: %A{other}"

[<Theory>]
[<InlineData("type Id = private protected int", "protected int")>]
[<InlineData("type Id = protected private int", "protected private int")>]
let ``A type is not both private and protected`` (source: string) (at: string) =
    let reported, text = parseErrorOf source
    Assert.Contains("A private type is nominal already, so it is not also `protected`: keep `private`", reported)
    Assert.Equal(at, text)

[<Fact>]
let ``protected is a word of its own`` () =
    match parsed "type Id = protectedId" with
    | [ Def (TypeDef ("Id", [], [], Kind (TypeName "protectedId"))) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"
