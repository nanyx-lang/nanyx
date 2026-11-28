/// The rules by which a debugged program stops, apart from any program: breakpoints by line, stepping in, over and out by
/// the shadow stack's depth, and pausing (see `DebugSession`).
module TranspilerWasmTests.Features.Debugging.DebugSessions

open Xunit
open Nanyx.Runtime.Debugging

let private at (line: int) (depth: int) = { File = "main.nyx"; Line = line; Depth = depth }

/// Where the program stops, of the statements at `path`, from `session`, each with why.
let private stops (session: DebugSession) (path: Location list) =
    path
    |> List.fold (fun (session, stopped) location ->
        match DebugSession.onStatement location session with
        | Some reason, next -> next, stopped @ [ location.Line, reason ]
        | None, next -> next, stopped) (session, [])
    |> snd

[<Fact>]
let ``A breakpoint is moved to the next line with a statement, and one past the last has none`` () =
    let statements = Map [ "main.nyx", Set [ 2; 3; 7 ] ]
    Assert.Equal<(int * int option) list>([ 1, Some 2; 3, Some 3; 5, Some 7; 9, None ], DebugSession.resolve statements "main.nyx" [ 1; 3; 5; 9 ])
    Assert.Equal<(int * int option) list>([ 1, None ], DebugSession.resolve statements "other.nyx" [ 1 ])

[<Fact>]
let ``The program stops at each statement on a breakpoint's line, and runs past the others`` () =
    let session = DebugSession.start |> DebugSession.withBreakpoints "main.nyx" [ 3 ]
    Assert.Equal<(int * StopReason) list>([ 3, AtBreakpoint ], stops session [ at 2 1; at 3 1; at 4 1 ])

[<Fact>]
let ``Resumed at a breakpoint, it doesn't stop there again until another place has passed`` () =
    let session = DebugSession.start |> DebugSession.withBreakpoints "main.nyx" [ 3 ] |> DebugSession.resume Continue (at 3 1)
    Assert.Equal(None, fst (DebugSession.onStatement (at 3 1) session))
    // A loop comes back to it through another statement, or a call's entry.
    let afterLoop = session |> DebugSession.passed (at 1 2)
    Assert.Equal(Some AtBreakpoint, fst (DebugSession.onStatement (at 3 1) afterLoop))

[<Fact>]
let ``Stepping in stops at the next statement, in a function it calls too`` () =
    let session = DebugSession.start |> DebugSession.resume StepIn (at 3 1)
    Assert.Equal<(int * StopReason) list>([ 10, AfterStep ], stops session [ at 10 2 ] |> List.truncate 1)

[<Fact>]
let ``Stepping over runs through the functions it calls, and stops at the next statement as deep or shallower`` () =
    let session = DebugSession.start |> DebugSession.resume StepOver (at 3 2)
    Assert.Equal<(int * StopReason) list>([ 4, AfterStep ], stops session [ at 10 3; at 11 3; at 4 2 ] |> List.truncate 1)
    // A function that returns stops in its caller.
    let returning = DebugSession.start |> DebugSession.resume StepOver (at 5 2)
    Assert.Equal<(int * StopReason) list>([ 8, AfterStep ], stops returning [ at 8 1 ] |> List.truncate 1)

[<Fact>]
let ``Stepping out runs to the next statement of the caller`` () =
    let session = DebugSession.start |> DebugSession.resume StepOut (at 3 2)
    Assert.Equal<(int * StopReason) list>([ 9, AfterStep ], stops session [ at 4 2; at 10 3; at 9 1 ] |> List.truncate 1)

[<Fact>]
let ``A pause stops at the next statement, wherever it is`` () =
    let session = DebugSession.start |> DebugSession.pause
    Assert.Equal<(int * StopReason) list>([ 20, Paused ], stops session [ at 20 5 ] |> List.truncate 1)

[<Fact>]
let ``A step stops once, and then the program runs to a breakpoint`` () =
    let session = DebugSession.start |> DebugSession.withBreakpoints "main.nyx" [ 6 ] |> DebugSession.resume StepIn (at 3 1)
    Assert.Equal<(int * StopReason) list>([ 4, AfterStep ], stops session [ at 4 1 ])
    let _, next = DebugSession.onStatement (at 4 1) session
    Assert.Equal<(int * StopReason) list>([ 6, AtBreakpoint ], stops next [ at 5 1; at 6 1 ])

[<Fact>]
let ``A pause stops at a function's entry too, and nothing else does`` () =
    let session = DebugSession.start |> DebugSession.withBreakpoints "main.nyx" [ 1 ]
    Assert.Equal(None, fst (DebugSession.onEntry (at 1 2) session))
    Assert.Equal(Some Paused, fst (DebugSession.onEntry (at 1 2) (DebugSession.pause session)))
