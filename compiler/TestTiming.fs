/// Timing in tests: a test that compares how long things take is in the `Timing` category, so that a run on a busy
/// machine can leave it out (`dotnet test --filter "Category!=Timing"`), while CI runs it. Where a test can compare an
/// amount of work instead, as a count of solves or a program's fuel, it does, and is in no category. Every test project
/// that times anything compiles this one file, so each times the same way.
module Nanyx.TestTiming

open System.Diagnostics
open System.Reflection
open Xunit

/// The trait a timing test carries: `[<Fact; Trait(TestTiming.category, TestTiming.timing)>]`.
[<Literal>]
let category = "Category"

/// The category of a timing test (see `category`).
[<Literal>]
let timing = "Timing"

/// Fails unless the test running is in the `Timing` category: each way of timing checks it, so that a new test that
/// times something can't leave it out.
let private requireTimingCategory () =
    let isTiming (data: CustomAttributeData) =
        data.AttributeType = typeof<TraitAttribute>
        && data.ConstructorArguments.Count = 2
        && string data.ConstructorArguments[0].Value = category
        && string data.ConstructorArguments[1].Value = timing
    let test =
        StackTrace().GetFrames()
        |> Array.choose (fun frame -> Option.ofObj (frame.GetMethod()))
        |> Array.tryFind (fun method -> method.IsDefined(typeof<FactAttribute>, true))
    match test with
    | Some method when method.GetCustomAttributesData() |> Seq.exists isTiming -> ()
    | Some method -> failwith $"'{method.Name}' times what it runs, so it needs [<Trait(\"{category}\", \"{timing}\")>]"
    | None -> failwith "Timing is for a test, which calls it on its own thread"

/// How many milliseconds `action` takes, in a test in the `Timing` category.
let millisecondsOf (action: unit -> unit) : float =
    requireTimingCategory ()
    let watch = Stopwatch.StartNew()
    action ()
    watch.Elapsed.TotalMilliseconds
