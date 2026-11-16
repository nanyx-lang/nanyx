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
    | [ Def (TypeDef ("Email", [ Protected; KindAt span ], [], Kind (TypeName "string"))) ] -> Assert.Equal(13, span.Start)
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A protected record type keeps its fields`` () =
    match parsed "export type User = protected (\n  name: string\n  age: int\n)" with
    | [ Def (TypeDef ("User", [ Protected; Export; KindAt _ ], [], Kind (TypeRecord fields))) ] -> Assert.Equal(2, fields.Length)
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A scoped context is declared protected after its =`` () =
    match parsed "scoped export context Memory = protected ()" with
    | [ Def (TypeDef ("Memory", modifiers, [], _)) ] ->
        Assert.Contains(Protected, modifiers)
        Assert.Contains(Context, modifiers)
        Assert.True(TypeDefModifier.isNominal modifiers)
    | other -> failwith $"Unexpected parse: %A{other}"

[<Theory>]
[<InlineData("type $Email = string", "$Email = string")>]
[<InlineData("context $Email = ()", "$Email = ()")>]
[<InlineData("def wrap: string -> $Email = { s -> s }", "$Email = { s -> s }")>]
[<InlineData("def e = $Email(s)", "$Email(s)")>]
[<InlineData("def $Email.parse = { s -> s }", "$Email.parse = { s -> s }")>]
[<InlineData("def e: m.$Email = x", "$Email = x")>]
[<InlineData("def f = { x -> match x | $Email(s) -> s }", "$Email(s) -> s }")>]
let ``A $ before a type's name is an error at the $`` (source: string) (at: string) =
    let reported, text = parseErrorOf source
    Assert.Contains("A type's name is written without a `$`: write `Email`, and declare a nominal type with `protected` after its `=`, as in `type Email = protected string`", reported)
    Assert.Equal(at, text)

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

[<Fact>]
let ``A scoped context written before a type's kind puts its values in the context's scope`` () =
    match parsed "export type Cursor = [Tx] protected (row: int)" with
    | [ Def (TypeDef ("Cursor", modifiers, [], Kind (TypeRecord _))) ] ->
        Assert.Equal<string list>([ "Tx" ], TypeDefModifier.scopeContexts modifiers |> List.map fst)
        Assert.True(TypeDefModifier.isNominal modifiers)
    | other -> failwith $"Unexpected parse: %A{other}"
    match parsed "type MRef(a) = [Memory] protected (value: a)" with
    | [ Def (TypeDef ("MRef", modifiers, [ "a", None ], _)) ] ->
        Assert.Equal<string list>([ "Memory" ], TypeDefModifier.scopeContexts modifiers |> List.map fst)
    | other -> failwith $"Unexpected parse: %A{other}"