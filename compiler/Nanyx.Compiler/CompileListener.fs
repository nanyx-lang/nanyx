namespace NyxCompiler

open System

/// What a compile tells its caller as it goes, so that the caller can show where a slow compile is and how long each
/// part of it took. `CompileListener.none` listens to nothing; a `CompileTimings` records each module's time.
type CompileListener =
    /// A package build has started on the source file at `file`, its full path.
    abstract FileStarted: file: string -> unit
    /// A module's check has started: its parsing, desugaring and type checking, with the checks of the modules it
    /// imports inside it. `name` is the module's full path, or `nanyx/<module>` for a standard-library module. A
    /// standard-library module is checked once per process, so only the first compile that needs it hears of it.
    abstract ModuleStarted: name: string -> unit
    /// The module's check has finished, `elapsed` after it started.
    abstract ModuleFinished: name: string * elapsed: TimeSpan -> unit

module CompileListener =
    /// The listener that does nothing.
    let none =
        { new CompileListener with
            member _.FileStarted _ = ()
            member _.ModuleStarted _ = ()
            member _.ModuleFinished(_, _) = () }

/// How long one module's check took in a compile.
type ModuleTiming =
    { /// The module: its full path, or `nanyx/<module>` for a standard-library module.
      Name: string
      /// From the start of the check to its end, the checks of the modules it imported included.
      Total: TimeSpan
      /// `Total` less the checks of the modules it imported: the module's own parsing, desugaring and type checking.
      Self: TimeSpan }

module ModuleTiming =
    /// `timings` as a table for a terminal or a log, slowest first by `Total`: a header, then a row per module with
    /// its total and self times in seconds and its name.
    let table (timings: ModuleTiming list) : string =
        let seconds (time: TimeSpan) = sprintf "%8.3fs" time.TotalSeconds
        let rows =
            timings
            |> List.sortByDescending _.Total
            |> List.map (fun timing -> $"{seconds timing.Total} {seconds timing.Self}  {timing.Name}")
        String.concat "\n" (sprintf "%9s %9s  module" "total" "self" :: rows)

/// A listener that records how long each module's check took, telling `inner` everything it is told. Its checks nest:
/// a module's imports are checked inside its own check, and their time is taken off its `Self`.
type CompileTimings(inner: CompileListener) =
    let timings = ResizeArray<ModuleTiming>()
    // The checks under way, innermost last, each with the time of the imports it has checked so far.
    let underWay = Collections.Generic.Stack<TimeSpan ref>()

    new() = CompileTimings(CompileListener.none)

    /// The modules checked so far, in the order their checks finished: an import before its importer.
    member _.Timings: ModuleTiming list = List.ofSeq timings

    interface CompileListener with
        member _.FileStarted file = inner.FileStarted file

        member _.ModuleStarted name =
            underWay.Push(ref TimeSpan.Zero)
            inner.ModuleStarted name

        member _.ModuleFinished(name, elapsed) =
            let imported = underWay.Pop()
            timings.Add { Name = name; Total = elapsed; Self = elapsed - imported.Value }
            if underWay.Count > 0 then
                let importer = underWay.Peek()
                importer.Value <- importer.Value + elapsed
            inner.ModuleFinished(name, elapsed)
