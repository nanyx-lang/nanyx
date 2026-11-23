/// Types nested many levels deep, which the parser reads once at each level (#2218). When it read each level twice, a
/// depth of 20 took 218 s, and the time doubled with each level.
module ParserTests.Features.NestedTypes

open Xunit
open ParserTestHelpers

/// `wrap` applied `depth` times to `innermost`.
let private nested (depth: int) (wrap: string -> string) (innermost: string) =
    Seq.fold (fun inner _ -> wrap inner) innermost (seq { 1..depth })

/// Asserts that `source` parses whole within 10 s. It takes milliseconds when each level is read once, so load on the
/// machine can't make it fail, and a parse that doubles with each level doesn't finish at a depth of 30: the test fails
/// when the time is up rather than waiting for it.
let private parsesInTime (source: string) =
    let parse = System.Threading.Tasks.Task.Run(fun () -> readsWhole source)
    Assert.True(parse.Wait(System.TimeSpan.FromSeconds 10.0), "The parse didn't finish in 10 s")
    parse.GetAwaiter().GetResult()

[<Fact>]
let ``A type applied to a type applied to a type, 30 deep, parses in time`` () =
    parsesInTime $"""def x: {nested 30 (fun t -> $"list({t})") "int"} = []"""

[<Fact>]
let ``A function type in brackets, 30 deep, parses in time`` () =
    parsesInTime $"""def f: {nested 30 (fun t -> $"({t} -> int)") "int"} = g"""

[<Fact>]
let ``A tuple type in a tuple type, 30 deep, parses in time`` () =
    parsesInTime $"""def y: {nested 30 (fun t -> $"(int, {t})") "int"} = z"""

[<Fact>]
let ``A tag's payload of a tag, 30 deep, parses in time`` () =
    parsesInTime $"""def y: {nested 30 (fun t -> $"#a({t})") "int"} = z"""

[<Fact>]
let ``A capitalised call of a capitalised call, 30 deep, parses in time`` () =
    parsesInTime $"""def y = {nested 30 (fun t -> $"Wrap({t})") "x"}"""
