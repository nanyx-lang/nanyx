namespace NyxCompiler

open System

/// What a compile tells its caller as it goes, so that the caller can show where a slow compile is and how long each
/// part of it took. `CompileListener.none` listens to nothing; a `CompileTimings` records each module's time.
type CompileListener =
    /// A package build has started on the source file at `file`, its full path.
    abstract FileStarted: file: string -> unit
    /// A module's check has started: its parsing, desugaring and type checking, with the checks of the modules it
    /// imports inside it. `name` is the module's full path, or `nanyx/<module>` for a standard-library module. A
    /// standard-library module is checked once per process, so only the first compile that needs it hears of its check,
    /// and a later one hears that it was reused.
    abstract ModuleStarted: name: string -> unit
    /// The module's check has finished, `elapsed` after it started.
    abstract ModuleFinished: name: string * elapsed: TimeSpan -> unit
    /// A module was needed that was already checked, so it was not checked again: one imported a second time in the
    /// compile, one an earlier file of a package build checked, or a standard-library module an earlier compile in the
    /// process checked. It is told where its check would have started and ended.
    abstract ModuleReused: name: string -> unit

module CompileListener =
    /// The listener that does nothing.
    let none =
        { new CompileListener with
            member _.FileStarted _ = ()
            member _.ModuleStarted _ = ()
            member _.ModuleFinished(_, _) = ()
            member _.ModuleReused _ = () }

/// How long one module's check took in a compile.
type ModuleTiming =
    { /// The module: its full path, or `nanyx/<module>` for a standard-library module.
      Name: string
      /// From the start of the check to its end, the checks of the modules it imported included.
      Total: TimeSpan
      /// `Total` less the checks of the modules it imported: the module's own parsing, desugaring and type checking.
      Self: TimeSpan }

/// A module a compile needed, with the modules it needed in turn.
type ModuleCheck =
    /// A module checked in the compile, with the modules its imports needed, in the order they were needed.
    | Checked of ModuleTiming * imports: ModuleCheck list
    /// A module that was already checked (see `CompileListener.ModuleReused`), by its name.
    | Reused of name: string

/// A module's check that has started and not yet finished, in a compile under way.
type RunningCheck =
    { /// The module: its full path, or `nanyx/<module>` for a standard-library module.
      Name: string
      /// When the check started, on the compile's clock.
      Started: TimeSpan
      /// The modules its imports have needed so far, each finished, in the order they were needed.
      Imports: ModuleCheck list }

/// A compile under way, as it stood at `Now` on the compile's clock.
type CompileSoFar =
    { /// The modules needed outside any check whose checks have finished, in the order they were needed.
      Finished: ModuleCheck list
      /// The checks under way, outermost first: each is checking the next as one of its imports.
      Running: RunningCheck list
      Now: TimeSpan }

module ModuleCheck =
    let private seconds (time: TimeSpan) = sprintf "%.2fs" time.TotalSeconds

    /// `check`'s own line, without the modules it needed: `name (self of total)`, `name (total)` when the two are the
    /// same as written, or `name (cached)`.
    let private line (display: string -> string) (check: ModuleCheck) =
        match check with
        | Reused name -> $"{display name} (cached)"
        | Checked(timing, _) ->
            let self, total = seconds timing.Self, seconds timing.Total
            if self = total then $"{display timing.Name} ({total})"
            else $"{display timing.Name} ({self} of {total})"

    /// `checks` as a tree for a terminal or a log, each module's name written by `display`: a line per module, as
    /// `name (self of total)` in seconds, `name (total)` when the two are the same as written, or `name (cached)`, with
    /// the modules it needed under it, drawn with box-drawing characters.
    let tree (display: string -> string) (checks: ModuleCheck list) : string =
        let rec lines (prefix: string) (childPrefix: string) (check: ModuleCheck) =
            match check with
            | Reused _ -> [ prefix + line display check ]
            | Checked(_, imports) ->
                let last = imports.Length - 1
                (prefix + line display check)
                :: (imports
                    |> List.mapi (fun index import ->
                        if index = last then lines $"{childPrefix}└── " $"{childPrefix}    " import
                        else lines $"{childPrefix}├── " $"{childPrefix}│   " import)
                    |> List.concat)
        checks |> List.collect (lines "" "") |> String.concat "\n"

    /// The live view of a compile under way, a line at a time, drawn as `tree` draws: a finished module's check on one
    /// line, without what it needed, and each check under way as `name (1.2s…)`, how long it has taken so far, with its
    /// finished imports under it and then the import it is checking now. When that is more than `height` lines, the
    /// lines nearest the check under way are kept, and the rest are one line at the top, `… 12 more`.
    let live (display: string -> string) (soFar: CompileSoFar) (height: int) : string list =
        let running (check: RunningCheck) =
            sprintf "%s (%.1fs…)" (display check.Name) (soFar.Now - check.Started).TotalSeconds
        let rec under (childPrefix: string) (check: RunningCheck) (inner: RunningCheck list) =
            let last = check.Imports.Length - 1
            let imports =
                check.Imports
                |> List.mapi (fun index import ->
                    let branch = if index = last && List.isEmpty inner then "└── " else "├── "
                    childPrefix + branch + line display import)
            match inner with
            | [] -> imports
            | next :: rest -> imports @ (childPrefix + "└── " + running next) :: under (childPrefix + "    ") next rest
        let lines =
            (soFar.Finished |> List.map (line display))
            @ (match soFar.Running with
               | [] -> []
               | outer :: rest -> running outer :: under "" outer rest)
        if lines.Length <= height then
            lines
        else
            let hidden = lines.Length - max 1 (height - 1)
            $"… {hidden} more" :: List.skip hidden lines

/// A listener that records each module a compile needs, and how long each module's check took, telling `inner`
/// everything it is told. Its checks nest: a module's imports are checked inside its own check, and their time is taken
/// off its `Self`. It may be read from another thread while the compile runs, as a live view does.
type CompileTimings(inner: CompileListener) =
    let clock = Diagnostics.Stopwatch.StartNew()
    let gate = obj ()
    let roots = ResizeArray<ModuleCheck>()
    // The checks under way, innermost on top, each with its module, when it started, the time of the imports it has
    // checked so far and what its imports needed.
    let underWay = Collections.Generic.Stack<string * TimeSpan * TimeSpan ref * ResizeArray<ModuleCheck>>()

    let record (check: ModuleCheck) =
        if underWay.Count > 0 then
            let _, _, _, imports = underWay.Peek()
            imports.Add check
        else
            roots.Add check

    new() = CompileTimings(CompileListener.none)

    /// The modules needed so far outside any check, in the order they were needed, each with what it needed in turn.
    member _.Checks: ModuleCheck list = lock gate (fun () -> List.ofSeq roots)

    /// The compile as it stands now: the checks finished outside any check, and the checks under way.
    member _.SoFar: CompileSoFar =
        lock gate (fun () ->
            { Finished = List.ofSeq roots
              // A stack lists its top first, so the innermost check.
              Running =
                underWay
                |> Seq.rev
                |> Seq.map (fun (name, started, _, imports) -> { Name = name; Started = started; Imports = List.ofSeq imports })
                |> List.ofSeq
              Now = clock.Elapsed })

    interface CompileListener with
        member _.FileStarted file = inner.FileStarted file

        member _.ModuleStarted name =
            lock gate (fun () -> underWay.Push((name, clock.Elapsed, ref TimeSpan.Zero, ResizeArray())))
            inner.ModuleStarted name

        member _.ModuleFinished(name, elapsed) =
            lock gate (fun () ->
                let _, _, imported, imports = underWay.Pop()
                record (Checked({ Name = name; Total = elapsed; Self = elapsed - imported.Value }, List.ofSeq imports))
                if underWay.Count > 0 then
                    let _, _, importer, _ = underWay.Peek()
                    importer.Value <- importer.Value + elapsed)
            inner.ModuleFinished(name, elapsed)

        member _.ModuleReused name =
            lock gate (fun () -> record (Reused name))
            inner.ModuleReused name
