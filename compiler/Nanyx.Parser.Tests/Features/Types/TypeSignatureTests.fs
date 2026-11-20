module ParserTests.Features.TypeSignatures

open System.Runtime.Loader
open Xunit
open Parser.Program

// A type read on its own, as a def's annotation writes it: what `nanyx search` reads a query's type with.

[<Fact>]
let ``A type signature is read on its own`` () =
    match parseTypeSignature "bool -> string" with
    | Result.Ok _ -> ()
    | Result.Error message -> failwith message

[<Fact>]
let ``A type signature is read before anything else has been parsed`` () =
    // The parser's own copy, whose grammar nothing has used yet, as in a process that runs `nanyx search` (#1524): a
    // Release build once read the type with the grammar's forwarded parsers not yet filled.
    let context = AssemblyLoadContext("fresh parser", isCollectible = true)
    try
        let parser = context.LoadFromAssemblyPath(typeof<TypeExpr>.Assembly.Location)
        let parse = parser.GetType("Parser.Program.Api").GetMethod("parseTypeSignature")
        let result = parse.Invoke(null, [| box "bool -> string" |])
        Assert.True(result.GetType().GetProperty("IsOk").GetValue(result) :?> bool, $"%A{result}")
    finally
        context.Unload()

[<Theory>]
[<InlineData("bool -> string")>]
[<InlineData("list(a), (a -> bool) -> list(a)")>]
[<InlineData("[Eq(a)] a, a -> bool")>]
[<InlineData("#some(a) | #none")>]
[<InlineData("(x: int, y: int)")>]
[<InlineData("map(k, v).key")>]
[<InlineData("mut int")>]
let ``A type signature is read whole`` (written: string) =
    match parseTypeSignature written with
    | Result.Ok _ -> ()
    | Result.Error message -> Assert.Fail message

[<Fact>]
let ``A mistake in a type signature is reported`` () =
    match parseTypeSignature "int ->" with
    | Result.Error message -> Assert.EndsWith("found the end of the file", message)
    | Result.Ok typeExpr -> Assert.Fail $"%A{typeExpr}"

[<Fact>]
let ``A type signature with something after it is a mistake`` () =
    match parseTypeSignature "int int" with
    | Result.Error message -> Assert.StartsWith("Expected", message)
    | Result.Ok typeExpr -> Assert.Fail $"%A{typeExpr}"
