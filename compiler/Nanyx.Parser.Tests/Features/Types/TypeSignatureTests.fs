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

[<Fact>]
let ``A type signature reads the same with the recursive-descent parser as with the FParsec one`` () =
    let written = [ "bool -> string"; "list(a), (a -> bool) -> list(a)"; "[Eq(a)] a, a -> bool"; "#some(a) | #none"; "(x: int, y: int)"; "map(k, v).key"; "mut int" ]
    for text in written do
        let descent = DescentTypes.readTypeSignature text
        Assert.True(descent.IsSome, $"The recursive-descent parser gave up on {text}")
        match FParsec.CharParsers.runParserOnString typeSignatureParser (ParseState.empty ()) "" text with
        | FParsec.CharParsers.ParserResult.Success(expected, _, _) ->
            Assert.Equal(Nanyx.Parser.SyntaxDump.dump expected, Nanyx.Parser.SyntaxDump.dump descent.Value)
        | FParsec.CharParsers.ParserResult.Failure(message, _, _) -> Assert.Fail message

[<Fact>]
let ``A mistake in a type signature is still reported by the FParsec parser`` () =
    Assert.Equal(None, DescentTypes.readTypeSignature "int ->")
    match parseTypeSignature "int ->" with
    | Result.Error message -> Assert.Contains("Error in Ln: 1", message)
    | Result.Ok typeExpr -> Assert.Fail $"%A{typeExpr}"
