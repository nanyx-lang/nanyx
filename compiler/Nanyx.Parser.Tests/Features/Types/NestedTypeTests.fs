/// Types nested many levels deep, which the parser reads once at each level (#2218). When it read each level twice, a
/// depth of 20 took 218 s, and the time doubled with each level.
module ParserTests.Features.NestedTypes

open Xunit
open ParserTestHelpers

/// `wrap` applied `depth` times to `innermost`.
let private nested (depth: int) (wrap: string -> string) (innermost: string) =
    Seq.fold (fun inner _ -> wrap inner) innermost (seq { 1..depth })

/// Asserts that `source` parses whole within 10 s. It takes milliseconds when each level is read once, and a parse that
/// doubles with each level doesn't finish at a depth of 30: the test fails when the time is up rather than waiting for
/// it. The parse runs on a thread of its own, started at once, so the 10 s don't include waiting for a thread-pool thread
/// while other tests keep the pool busy (#2613), and a background thread, so a parse still running doesn't keep the test
/// run alive.
let private parsesInTime (source: string) =
    let failure: exn option ref = ref None
    let parse =
        System.Threading.Thread(
            (fun () ->
                try
                    readsWhole source
                with error ->
                    failure.Value <- Some error),
            IsBackground = true
        )
    parse.Start()
    Assert.True(parse.Join(System.TimeSpan.FromSeconds 10.0), "The parse didn't finish in 10 s")
    failure.Value |> Option.iter (fun error -> raise (System.AggregateException("The parse failed", error)))

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
