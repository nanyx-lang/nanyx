namespace Nanyx.Runtime

open System
open Wasmtime

/// What answers a debug build's probes (see `Transpiler.Wasm.CoreIrProbes`): the debugger, or a test.
type IProbeListener =
    /// The program is at `probe`. True asks it for the locals of the frame it is in that are still alive there, which
    /// it passes to `Local`, one at a time, before it goes on.
    abstract Probe: probe: Probe -> bool
    /// A local of the frame of the last probe, by its name, written as `dbg` writes values.
    abstract Local: name: string * text: string -> unit
    /// The locals the last probe was asked for are all written, and the statement it is at hasn't run: where a debugger
    /// stops.
    abstract Written: unit -> unit
    /// The program crashed with `message` and is about to trap, its frames still on the stack (see `CrashImport`).
    abstract Crashed: message: string -> unit

/// A `dbg` a debug build reached: where it is written, its line and column counted from 1, the value's text, as
/// `dbg`'s printer wrote it, and whether it is a value `nanyx run --show-values` shows rather than a `dbg` the program
/// writes.
type DbgHit =
    { File: string
      Line: int
      Column: int
      Text: string
      Shown: bool }

    /// `[main.nyx:12] 84`, as a program writes it itself (see `Transpiler.Wasm.CoreIr.DbgSite`).
    override this.ToString() =
        if this.File = "" then $"[{this.Line}] {this.Text}" else $"[{this.File}:{this.Line}] {this.Text}"

/// The call stack of a program, kept from the entry and return probes of a debug build: a frame for each function it
/// is in, innermost first, each at the place of the last probe it passed. A function inlined into another still has a
/// frame, since its probes were inlined with it, and a tail call ends its caller's frame before the callee's starts.
type ShadowStack() =
    let frames = Collections.Generic.List<Frame>()

    /// The frames, innermost first.
    member _.Frames = frames |> Seq.rev |> List.ofSeq

    /// How many frames there are.
    member _.Depth = frames.Count

    /// Takes the program on past `probe`.
    member _.Pass(probe: Probe) =
        let place, column =
            match probe.Place with
            | (_, file, line, column) :: _ -> Some(file, line), column
            | [] -> None, 0
        match probe.Kind with
        | Entry functionName ->
            let name = FunctionNames.shown functionName |> Option.defaultValue functionName
            frames.Add { Name = name; Place = place; Column = column }
        | Statement
        | Crashing when frames.Count > 0 -> frames.[frames.Count - 1] <- { frames.[frames.Count - 1] with Place = place; Column = column }
        | Statement
        | Crashing -> ()
        | Return when frames.Count > 0 -> frames.RemoveAt(frames.Count - 1)
        | Return -> ()

module Probing =
    /// The module a debug build's probes import (see `Transpiler.Wasm.CoreIrEmitImports.probeModule`).
    let moduleName = "nanyx:debug"

    /// The string at `address` in the program's memory: its UTF-8 bytes, with their count in the word before them.
    let private stringAt (memory: Memory) (address: int) =
        let length = memory.ReadInt32(int64 (address - 4))
        Text.Encoding.UTF8.GetString(memory.GetSpan(int64 address, length))

    /// Defines what a debug build's probes import in `linker`: `enabled`, whether anything listens, which the program
    /// reads when it starts; `probe`, which passes the probe of a number in `table` to `listener`; `local`, which
    /// passes it a local the program writes; and `written`, which tells it they are all written. Without a listener,
    /// the program never calls the last three.
    let define (linker: Linker) (table: SpanTable option) (listener: IProbeListener option) =
        linker.DefineFunction(moduleName, "enabled", Func<int>(fun () -> if listener.IsSome then 1 else 0))
        linker.DefineFunction(
            moduleName,
            "probe",
            Func<int, int>(fun id ->
                match listener, table |> Option.bind (fun table -> SpanTable.probe table id) with
                | Some listener, Some probe -> if listener.Probe probe then 1 else 0
                | _ -> 0))
        linker.DefineFunction(
            moduleName,
            "local",
            CallerAction<int, int>(fun caller name text ->
                match listener with
                | Some listener ->
                    let memory = caller.GetMemory "memory"
                    listener.Local(stringAt memory name, stringAt memory text)
                | None -> ()))
        linker.DefineFunction(moduleName, "written", Action(fun () -> listener |> Option.iter _.Written()))

    /// What a runner that leaves each `dbg` to the program gives `defineDbg`: it deals with none, so the program writes
    /// each to its own stderr.
    let leftToProgram (_: DbgHit) = false

    /// Defines `dbg` in `linker`, which a debug build the runtime runs calls at each `dbg` it reaches, with the address of
    /// the file's name, the line, the column and the address of the value's text. `handle` is given the hit, and says
    /// whether it has dealt with it: when it hasn't, the program writes it to its own stderr, as a module that another
    /// runtime runs, without the import, does. The console runner leaves it to the program, so that it keeps its place
    /// among what else the program writes there; the HTTP server writes it to its log, with the request it came from.
    /// It defines `value` too, which a program built to show its values (see `Transpiler.Wasm.ShownValues`) calls as it
    /// does `dbg` at each value it shows.
    let defineDbg (linker: Linker) (handle: DbgHit -> bool) =
        let hit (shown: bool) =
            CallerFunc<int, int, int, int, int>(fun caller file line column text ->
                let memory = caller.GetMemory "memory"
                let hit = { File = stringAt memory file; Line = line; Column = column; Text = stringAt memory text; Shown = shown }
                if handle hit then 1 else 0)
        linker.DefineFunction(moduleName, "dbg", hit false)
        linker.DefineFunction(moduleName, "value", hit true)
