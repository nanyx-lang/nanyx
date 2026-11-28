namespace Nanyx.Runtime.Debugging

open Nanyx.Runtime

/// How a debugged program goes on from where it stopped.
type Resume =
    /// To the next breakpoint.
    | Continue
    /// To the next statement, in a function it calls too.
    | StepIn
    /// To the next statement of the function it stopped in, or of one that called it, once that function returns.
    | StepOver
    /// To the next statement of the function that called the one it stopped in.
    | StepOut

/// Why a debugged program stopped.
type StopReason =
    | AtBreakpoint
    | AfterStep
    | Paused

/// Where a debugged program is: the file and line of a statement probe's place, innermost, as the table of places names
/// the file, and how many frames the shadow stack has.
type Location = { File: string; Line: int; Depth: int }

/// What a debug session knows of how to go on (see `DebugSession.onStatement`): the breakpoints, by file, each the line
/// of a statement; how it goes on from where it last stopped, with the depth it stopped at; whether it was asked to
/// pause; and the place it last stopped at, which it doesn't stop at again until the program has passed another.
type DebugSession =
    { Breakpoints: Map<string, Set<int>>
      Resume: Resume * int
      Pausing: bool
      StoppedAt: Location option }

/// The rules by which a debugged program stops, apart from the program and the protocol: breakpoints by line, which a
/// line with no statement moves to the next with one, stepping in, over and out by the shadow stack's depth, and pausing.
module DebugSession =
    /// A session that runs to the first breakpoint.
    let start: DebugSession = { Breakpoints = Map.empty; Resume = (Continue, 0); Pausing = false; StoppedAt = None }

    /// The line each of `requested`, lines of `file`, breaks at: the first line at or after it with a statement of
    /// `statements` (the lines with a statement, by file), or none when there is no such line.
    let resolve (statements: Map<string, Set<int>>) (file: string) (requested: int list) : (int * int option) list =
        let lines = statements |> Map.tryFind file |> Option.defaultValue Set.empty
        requested |> List.map (fun line -> line, lines |> Seq.tryFind (fun candidate -> candidate >= line))

    /// `session` with the breakpoints of `file` set to `lines`, as `resolve` gives them.
    let withBreakpoints (file: string) (lines: int list) (session: DebugSession) =
        { session with Breakpoints = session.Breakpoints |> Map.add file (Set.ofList lines) }

    /// `session` going on from `at`, where it stopped, as `resume` says.
    let resume (resume: Resume) (at: Location) (session: DebugSession) =
        { session with Resume = (resume, at.Depth); Pausing = false; StoppedAt = Some at }

    /// `session` asked to stop at the next statement.
    let pause (session: DebugSession) = { session with Pausing = true }

    /// Whether the program stops at a statement at `at`, and why, and the session after it. A statement at the place it
    /// last stopped at doesn't stop it, until another probe has passed (see `passed`).
    let onStatement (at: Location) (session: DebugSession) : StopReason option * DebugSession =
        if session.StoppedAt = Some at then None, session
        else
            let session = { session with StoppedAt = None }
            let resume, depth = session.Resume
            let reason =
                if session.Pausing then Some Paused
                else
                    match resume with
                    | StepIn -> Some AfterStep
                    | StepOver when at.Depth <= depth -> Some AfterStep
                    | StepOut when at.Depth < depth -> Some AfterStep
                    | _ when session.Breakpoints |> Map.tryFind at.File |> Option.exists (Set.contains at.Line) -> Some AtBreakpoint
                    | _ -> None
            match reason with
            | Some _ -> reason, { session with Resume = (Continue, 0); Pausing = false }
            | None -> None, session

    /// The session once the program has passed a probe that isn't a statement (an entry or a return) at `at`: the place
    /// it last stopped at may be stopped at again.
    let passed (at: Location) (session: DebugSession) =
        if session.StoppedAt = Some at then session else { session with StoppedAt = None }

    /// Whether the program stops at a function's entry at `at`, and the session after it: only when it was asked to
    /// pause, since a function whose body is one expression, as a loop that calls itself may be, has no statement to
    /// pause at.
    let onEntry (at: Location) (session: DebugSession) : StopReason option * DebugSession =
        if session.Pausing then Some Paused, { session with Resume = (Continue, 0); Pausing = false; StoppedAt = None }
        else None, passed at session
