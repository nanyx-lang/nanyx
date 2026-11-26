/// Types nested many levels deep, which the parser reads once at each level (#2218). When it read each level twice, a
/// depth of 20 took 218 s, and the time doubled with each level. The tests count the tokens it reads, which load on the
/// machine doesn't change, rather than time it.
module ParserTests.Features.NestedTypes

open Xunit
open Parser.Program
open ParserTestHelpers

/// `wrap` applied `depth` times to `innermost`.
let private nested (depth: int) (wrap: string -> string) (innermost: string) =
    Seq.fold (fun inner _ -> wrap inner) innermost (seq { 1..depth })

/// How many tokens the parser takes reading `source` whole, each one read again after a reading that didn't match
/// counted again (see `ParseState.TokensRead`).
let private tokensRead (source: string) =
    match DescentTopLevel.tryReadModuleWithState "t.nyx" source with
    | Ok (_, state) -> state.TokensRead
    | Error error -> failwith $"{error.Message} at {error.Span.StartLine + 1}:{error.Span.StartColumn + 1}"

/// Asserts that `source depth` is read in work that grows with its depth: 16 levels take at most three times the tokens
/// of 8, about twice when each level is read once. Read twice, each level doubles the work, so 16 levels take 256 times
/// what 8 do. The depths are small so that such a parse still ends, and the test fails rather than hangs.
let private readsOnceALevel (source: int -> string) =
    let atEight, atSixteen = tokensRead (source 8), tokensRead (source 16)
    Assert.True(atSixteen <= 3 * atEight, $"16 levels took {atSixteen} tokens, {float atSixteen / float atEight:F1} times the {atEight} of 8")

[<Fact>]
let ``A type applied to a type applied to a type is read once a level`` () =
    readsOnceALevel (fun depth -> $"""def x: {nested depth (fun t -> $"list({t})") "int"} = []""")

[<Fact>]
let ``A function type in brackets is read once a level`` () =
    readsOnceALevel (fun depth -> $"""def f: {nested depth (fun t -> $"({t} -> int)") "int"} = g""")

[<Fact>]
let ``A tuple type in a tuple type is read once a level`` () =
    readsOnceALevel (fun depth -> $"""def y: {nested depth (fun t -> $"(int, {t})") "int"} = z""")

[<Fact>]
let ``A tag's payload of a tag is read once a level`` () =
    readsOnceALevel (fun depth -> $"""def y: {nested depth (fun t -> $"#a({t})") "int"} = z""")

[<Fact>]
let ``A capitalised call of a capitalised call is read once a level`` () =
    readsOnceALevel (fun depth -> $"""def y = {nested depth (fun t -> $"Wrap({t})") "x"}""")
