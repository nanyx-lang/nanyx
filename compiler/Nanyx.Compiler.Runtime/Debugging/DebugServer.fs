namespace Nanyx.Runtime.Debugging

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading
open Nanyx.Runtime

/// The program a debug session runs: the module, with debug probes and places; its command-line arguments, its name
/// first; the directories it may reach, its current directory first; how a file the table of places names is found on
/// disk (`fileOf`) and the other way round (`shownOf`); the words that say how a run that went wrong ended, with
/// the exit code it ends with (see `RunError`); and whether the program has this process's standard streams, a
/// terminal's, rather than an empty input and its output sent to the client (`InTerminal`).
type DebugTarget =
    { Wasm: byte array
      Arguments: string list
      Directories: string list
      FileOf: string -> string option
      ShownOf: string -> string
      Report: RunError -> string * int
      InTerminal: bool }

/// A frame where the program stopped, with the locals the debugger has for it and the line it had them at: the last
/// time it asked for them, which for a frame it didn't stop in may be never (see `DebugServer`).
type private StoppedFrame =
    { Frame: Frame
      Locals: ((string * string) list * int) option }

/// Raised in a probe to end a program the client asked to end.
exception private Terminated

/// `nanyx run --debug`: a Debug Adapter Protocol server over a pair of streams, which runs a program with debug probes
/// and stops it at breakpoints, after a step, when the client pauses it, and when it crashes (see `DebugSession`). The
/// stack is the shadow stack's, and a frame's variables are the locals its probes wrote, read into trees (see
/// `ValueTree`). It asks for the locals only where it stops, so a frame further out has those of the last time the
/// program stopped in it, and none if it never did. What the program writes is sent as `output` events, and its input is
/// empty, but for one that runs in a terminal (`DebugTarget.InTerminal`), whose streams are the process's.
module DebugServer =
    /// The thread the program runs on, the only one the protocol knows.
    let private threadId = 1

    /// Serves a session for `target` on `input` and `output`, and gives the exit code it ends with.
    let serve (input: Stream) (output: Stream) (target: DebugTarget) : int =
        let table = SpanTable.read target.Wasm
        let statements = table |> Option.map SpanTable.statementLines |> Option.defaultValue Map.empty
        let mutable sequence = 0
        let send (message: JsonObject) =
            message.["seq"] <- Dap.number (Interlocked.Increment(&sequence))
            Dap.write output message
        let event (name: string) (body: (string * JsonNode) list) =
            send (Dap.object [ "type", Dap.string "event"; "event", Dap.string name; "body", Dap.object body ])
        let respond (request: JsonObject) (body: (string * JsonNode) list) =
            send (
                Dap.object
                    [ "type", Dap.string "response"
                      "request_seq", Dap.number (request.["seq"].GetValue<int>())
                      "success", Dap.boolean true
                      "command", Dap.string (request.["command"].GetValue<string>())
                      "body", Dap.object body ])
        let refuse (request: JsonObject) (message: string) =
            send (
                Dap.object
                    [ "type", Dap.string "response"
                      "request_seq", Dap.number (request.["seq"].GetValue<int>())
                      "success", Dap.boolean false
                      "command", Dap.string (request.["command"].GetValue<string>())
                      "message", Dap.string message ])

        // What the program writes goes to files, sent on as `output` events as they grow.
        let folder = Directory.CreateTempSubdirectory("nanyx-debug-").FullName
        let emptyInput = Path.Combine(folder, "input")
        File.WriteAllText(emptyInput, "")
        let streams = [ "stdout", Path.Combine(folder, "stdout"), ref 0L; "stderr", Path.Combine(folder, "stderr"), ref 0L ]
        for _, file, _ in streams do
            File.WriteAllText(file, "")
        let flush () =
            lock streams (fun () ->
                for category, file, offset in streams do
                    use stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                    if stream.Length > offset.Value then
                        stream.Seek(offset.Value, SeekOrigin.Begin) |> ignore
                        let bytes = Array.zeroCreate<byte> (int (stream.Length - offset.Value))
                        let read = stream.Read(bytes, 0, bytes.Length)
                        offset.Value <- offset.Value + int64 read
                        event "output" [ "category", Dap.string category; "output", Dap.string (Text.Encoding.UTF8.GetString(bytes, 0, read)) ])

        // The session's state, which the program's thread and the client's share, under `gate`.
        let gate = obj ()
        let mutable session = DebugSession.start
        let mutable stopOnEntry = false
        let mutable terminating = false
        let mutable resumed: (Resume option) = None
        // A pause the client asked for, which the program's thread takes into the session at its next probe. It is set
        // without `gate`, which a program running a tight loop takes and releases at every probe: a lock isn't fair, so
        // the client's thread could wait on it for as long as the loop runs.
        let pauseAsked = ref false
        // Under `gate`: the session with the pause the client asked for since the last probe, if it asked.
        let takePause () =
            if Volatile.Read(&pauseAsked.contents) then
                Volatile.Write(&pauseAsked.contents, false)
                session <- DebugSession.pause session
        let stack = ShadowStack()
        // Each frame's locals as of its last statement, by its depth, with the line it was at: written within a budget
        // where the program went on, and in full where it stopped (see `LocalsWanted`).
        let frameLocals = Collections.Generic.Dictionary<int, (string * string) list * int>()
        let written = ResizeArray<string * string>()
        let mutable pending: (StopReason * Location) option = None
        // Where a probe the program goes on from asked for the locals, which are kept as its frame's without stopping: a
        // statement's, within a budget, and one before a call that never returns, in full, so that the crash shows them.
        let mutable recordingAt: Location option = None
        let mutable stopped: StoppedFrame list = []
        // What each variables reference stands for, while the program is stopped: a frame's locals, or a value's parts.
        let references = Collections.Generic.Dictionary<int, (string * ValueTree) list>()

        let referenceOf (parts: (string * ValueTree) list) =
            if parts.IsEmpty then 0
            else
                let reference = references.Count + 1
                references.[reference] <- parts
                reference

        /// Stops the program's thread until the client resumes it, having told the client why it stopped.
        let stopHere (reason: string) (description: string option) (at: Location option) =
            flush ()
            lock gate (fun () ->
                // A function the compiler made, such as the host's start, is left out, as a crash's stack leaves it out.
                let frames = stack.Frames
                stopped <-
                    frames
                    |> List.mapi (fun index frame ->
                        let depth = frames.Length - index
                        { Frame = frame; Locals = (match frameLocals.TryGetValue depth with | true, found -> Some found | _ -> None) })
                    |> List.filter (fun stoppedFrame -> not (stoppedFrame.Frame.Name.StartsWith "__"))
                references.Clear()
                resumed <- None)
            event
                "stopped"
                [ yield "reason", Dap.string reason
                  yield "threadId", Dap.number threadId
                  yield "allThreadsStopped", Dap.boolean true
                  match description with
                  | Some text -> yield "text", Dap.string text
                  | None -> () ]
            let resume =
                lock gate (fun () ->
                    while resumed.IsNone && not terminating do
                        Monitor.Wait gate |> ignore
                    resumed)
            if terminating then raise Terminated
            match resume, at with
            | Some resume, Some at ->
                lock gate (fun () ->
                    // A pause asked for while the program was stopped is undone by resuming it, as the session's is.
                    Volatile.Write(&pauseAsked.contents, false)
                    session <- DebugSession.resume resume at session)
            | _ -> ()

        let locationOf (probe: Probe) =
            match probe.Place with
            | (_, file, line, _) :: _ -> Some { File = file; Line = line; Depth = stack.Depth }
            | [] -> None

        let listener =
            { new IProbeListener with
                member _.Probe probe =
                    if terminating then raise Terminated
                    let before = stack.Depth
                    stack.Pass probe
                    match probe.Kind with
                    | Entry _ ->
                        // A new frame: what the debugger had at its depth was another's.
                        frameLocals.Remove(stack.Depth) |> ignore
                        match locationOf probe with
                        | None -> NoLocals
                        | Some at ->
                            let reason =
                                lock gate (fun () ->
                                    takePause ()
                                    let reason, next = DebugSession.onEntry at session
                                    session <- next
                                    reason)
                            match reason with
                            | Some reason ->
                                written.Clear()
                                pending <- Some(reason, at)
                                InFull
                            | None -> NoLocals
                    | Return ->
                        frameLocals.Remove before |> ignore
                        locationOf probe |> Option.iter (fun at -> lock gate (fun () -> session <- DebugSession.passed at session))
                        NoLocals
                    // Just before a call that never returns: the locals are always asked for, and kept as the frame's.
                    // It runs once, on the way into a crash, so it costs nothing a run that doesn't crash pays.
                    | Crashing ->
                        match locationOf probe with
                        | None -> NoLocals
                        | Some at ->
                            written.Clear()
                            recordingAt <- Some at
                            InFull
                    | Statement ->
                        match locationOf probe with
                        | None -> NoLocals
                        | Some at ->
                            // The pause asked for taken, and the session read and written, at once, so that none is lost.
                            let reason =
                                lock gate (fun () ->
                                    takePause ()
                                    let reason, next =
                                        if stopOnEntry then
                                            stopOnEntry <- false
                                            Some Paused, { session with StoppedAt = None }
                                        else DebugSession.onStatement at session
                                    session <- next
                                    reason)
                            written.Clear()
                            match reason with
                            | Some reason ->
                                // It stops once the locals are written, before the statement runs (see `Written`), and
                                // shows them whole.
                                pending <- Some(reason, at)
                                InFull
                            | None ->
                                // Each statement's locals are kept as its frame's, so that a frame further out shows its
                                // locals as of its call. Within a budget each, so that a large value, written at every
                                // statement, costs no more than a small one (#3253).
                                recordingAt <- Some at
                                WithinBudget
                member _.Local(name, text) = written.Add((name, text))
                member _.Written() =
                    match pending with
                    | Some(reason, at) ->
                        pending <- None
                        frameLocals.[at.Depth] <- (List.ofSeq written, at.Line)
                        let reason =
                            match reason with
                            | AtBreakpoint -> "breakpoint"
                            | AfterStep -> "step"
                            | Paused -> "pause"
                        stopHere reason None (Some at)
                    | None ->
                        match recordingAt with
                        | Some at ->
                            recordingAt <- None
                            frameLocals.[at.Depth] <- (List.ofSeq written, at.Line)
                        | None -> ()
                member _.Crashed message = stopHere "exception" (Some message) None }

        // The program, once the client has set its breakpoints, on a thread of its own, which ends the session.
        let mutable exitCode = 0
        let mutable program: Thread = null
        let run () =
            let outcome =
                try
                    WasmRunner.runListening
                        (if target.InTerminal then Inherited
                         else ToFiles(emptyInput, (let _, file, _ = streams.[0] in file), (let _, file, _ = streams.[1] in file), []))
                        target.Arguments
                        target.Directories
                        target.Wasm
                        listener
                with Terminated -> Ok()
            flush ()
            exitCode <-
                match outcome with
                | Ok () -> 0
                | Error _ when terminating -> 0
                | Error error ->
                    let text, code = target.Report error
                    // Where the program wrote, after what it wrote.
                    if text <> "" && target.InTerminal then eprintf "%s" text
                    elif text <> "" then event "output" [ "category", Dap.string "stderr"; "output", Dap.string text ]
                    code
            event "exited" [ "exitCode", Dap.number exitCode ]
            event "terminated" []

        // Sends what the program writes on as it writes it, until the session ends.
        use timer = new Timer((fun _ -> try flush () with _ -> ()), null, 100, 100)

        let source (file: string) =
            match target.FileOf file with
            | Some path -> Dap.object [ "name", Dap.string (Path.GetFileName path); "path", Dap.string path ]
            | None -> Dap.object [ "name", Dap.string file; "presentationHint", Dap.string "deemphasize" ]

        let resumeWith (request: JsonObject) (resume: Resume) =
            respond request [ "allThreadsContinued", Dap.boolean true ]
            lock gate (fun () ->
                resumed <- Some resume
                Monitor.PulseAll gate)

        let mutable serving = true
        while serving do
            match Dap.read input with
            | None ->
                // The client has gone: the program ends with it.
                lock gate (fun () ->
                    terminating <- true
                    Monitor.PulseAll gate)
                serving <- false
            | Some request ->
                let arguments = request.["arguments"]
                match request.["command"].GetValue<string>() with
                | "initialize" ->
                    respond request [ "supportsConfigurationDoneRequest", Dap.boolean true; "supportsTerminateRequest", Dap.boolean true ]
                    event "initialized" []
                | "launch" ->
                    stopOnEntry <- (match arguments with null -> false | arguments -> match arguments.["stopOnEntry"] with null -> false | value -> value.GetValue<bool>())
                    respond request []
                | "setBreakpoints" ->
                    let path = arguments.["source"].["path"].GetValue<string>()
                    let file = target.ShownOf path
                    let requested =
                        match arguments.["breakpoints"] with
                        | null -> []
                        | breakpoints -> breakpoints.AsArray() |> Seq.map (fun breakpoint -> breakpoint.["line"].GetValue<int>()) |> List.ofSeq
                    let resolved = DebugSession.resolve statements file requested
                    lock gate (fun () -> session <- DebugSession.withBreakpoints file (resolved |> List.choose snd) session)
                    respond
                        request
                        [ "breakpoints",
                          Dap.array (
                              resolved
                              |> List.map (fun (line, actual) ->
                                  match actual with
                                  | Some actual -> Dap.object [ "verified", Dap.boolean true; "line", Dap.number actual ] :> JsonNode
                                  | None ->
                                      Dap.object [ "verified", Dap.boolean false; "line", Dap.number line; "message", Dap.string "No statement is on this line or after it" ]
                                      :> JsonNode)
                          ) ]
                | "configurationDone" ->
                    respond request []
                    if isNull program then
                        program <- Thread(run, IsBackground = true)
                        program.Start()
                | "threads" -> respond request [ "threads", Dap.array [ Dap.object [ "id", Dap.number threadId; "name", Dap.string "main" ] ] ]
                | "stackTrace" ->
                    let frames = lock gate (fun () -> stopped)
                    respond
                        request
                        [ "stackFrames",
                          Dap.array (
                              frames
                              |> List.mapi (fun index stoppedFrame ->
                                  let frame = stoppedFrame.Frame
                                  Dap.object
                                      [ yield "id", Dap.number (index + 1)
                                        yield "name", Dap.string frame.Name
                                        match frame.Place with
                                        | Some(file, line) ->
                                            yield "source", source file :> JsonNode
                                            yield "line", Dap.number line
                                        | None -> yield "line", Dap.number 0
                                        yield "column", Dap.number (max 1 frame.Column) ]
                                  :> JsonNode)
                          )
                          "totalFrames", Dap.number frames.Length ]
                | "scopes" ->
                    let frames = lock gate (fun () -> stopped)
                    let frameId = Dap.integer "frameId" arguments |> Option.defaultValue 1
                    match frames |> List.tryItem (frameId - 1) with
                    | Some { Locals = Some(locals, line) } ->
                        let parts = locals |> List.map (fun (name, text) -> name, ValueTree.parse text)
                        let reference = lock gate (fun () -> referenceOf parts)
                        let name =
                            if frameId = 1 then "Locals"
                            // Written within a budget as the program went on: a value cut short ends in `…`.
                            elif locals |> List.exists (fun (_, text) -> text.Contains "…") then
                                $"Locals at line {line}, values cut short: stop in this frame to see them whole"
                            else $"Locals at line {line}"
                        respond request [ "scopes", Dap.array [ Dap.object [ "name", Dap.string name; "variablesReference", Dap.number reference; "expensive", Dap.boolean false ] ] ]
                    | Some _ ->
                        respond
                            request
                            [ "scopes",
                              Dap.array
                                  [ Dap.object
                                        [ "name", Dap.string "No locals yet: this frame hasn't reached a statement"
                                          "variablesReference", Dap.number 0
                                          "expensive", Dap.boolean false ] ] ]
                    | None -> refuse request "There is no such frame"
                | "variables" ->
                    let reference = Dap.integer "variablesReference" arguments |> Option.defaultValue 0
                    let variables =
                        lock gate (fun () ->
                            match references.TryGetValue reference with
                            | true, parts ->
                                parts
                                |> List.map (fun (name, value) ->
                                    Dap.object
                                        [ "name", Dap.string name
                                          "value", Dap.string value.Text
                                          "variablesReference", Dap.number (referenceOf value.Parts) ]
                                    :> JsonNode)
                            | _ -> [])
                    respond request [ "variables", Dap.array variables ]
                | "continue" -> resumeWith request Continue
                | "next" -> resumeWith request StepOver
                | "stepIn" -> resumeWith request StepIn
                | "stepOut" -> resumeWith request StepOut
                | "pause" ->
                    Volatile.Write(&pauseAsked.contents, true)
                    respond request []
                | "disconnect"
                | "terminate" ->
                    lock gate (fun () ->
                        terminating <- true
                        Monitor.PulseAll gate)
                    if not (isNull program) then program.Join(TimeSpan.FromSeconds 10.0) |> ignore
                    respond request []
                    if request.["command"].GetValue<string>() = "disconnect" then serving <- false
                | command -> refuse request $"'{command}' isn't supported"
        if not (isNull program) then program.Join(TimeSpan.FromSeconds 10.0) |> ignore
        try Directory.Delete(folder, true) with _ -> ()
        exitCode
