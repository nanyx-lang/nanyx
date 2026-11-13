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

module ModuleCheck =
    /// `checks` as a tree for a terminal or a log, each module's name written by `display`: a line per module, as
    /// `name (self of total)` in seconds or `name (cached)`, with the modules it needed under it, drawn with
    /// box-drawing characters.
    let tree (display: string -> string) (checks: ModuleCheck list) : string =
        let seconds (time: TimeSpan) = sprintf "%.2fs" time.TotalSeconds
        let rec lines (prefix: string) (childPrefix: string) (check: ModuleCheck) =
            match check with
            | Reused name -> [ $"{prefix}{display name} (cached)" ]
            | Checked(timing, imports) ->
                let own = $"{prefix}{display timing.Name} ({seconds timing.Self} of {seconds timing.Total})"
                let last = imports.Length - 1
                own
                :: (imports
                    |> List.mapi (fun index import ->
                        if index = last then lines $"{childPrefix}└── " $"{childPrefix}    " import
                        else lines $"{childPrefix}├── " $"{childPrefix}│   " import)
                    |> List.concat)
        checks |> List.collect (lines "" "") |> String.concat "\n"

/// A listener that records each module a compile needs, and how long each module's check took, telling `inner`
/// everything it is told. Its checks nest: a module's imports are checked inside its own check, and their time is taken
/// off its `Self`.
type CompileTimings(inner: CompileListener) =
    let roots = ResizeArray<ModuleCheck>()
    // The checks under way, innermost last, each with the time of the imports it has checked so far and what its
    // imports needed.
    let underWay = Collections.Generic.Stack<TimeSpan ref * ResizeArray<ModuleCheck>>()

    let record (check: ModuleCheck) =
        if underWay.Count > 0 then (snd (underWay.Peek())).Add check else roots.Add check

    new() = CompileTimings(CompileListener.none)

    /// The modules needed so far outside any check, in the order they were needed, each with what it needed in turn.
    member _.Checks: ModuleCheck list = List.ofSeq roots

    interface CompileListener with
        member _.FileStarted file = inner.FileStarted file

        member _.ModuleStarted name =
            underWay.Push((ref TimeSpan.Zero, ResizeArray()))
            inner.ModuleStarted name

        member _.ModuleFinished(name, elapsed) =
            let imported, imports = underWay.Pop()
            record (Checked({ Name = name; Total = elapsed; Self = elapsed - imported.Value }, List.ofSeq imports))
            if underWay.Count > 0 then
                let importer = fst (underWay.Peek())
                importer.Value <- importer.Value + elapsed
            inner.ModuleFinished(name, elapsed)

        member _.ModuleReused name =
            record (Reused name)
            inner.ModuleReused name
