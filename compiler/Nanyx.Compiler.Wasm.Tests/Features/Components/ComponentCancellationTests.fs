/// Cancellation in a component (#2240): a task cancelled while it waits for a subtask, as a `race`'s loser is, cancels
/// the subtask with `[subtask-cancel]`, so what the host does for it stops then. The host here is a fake, which keeps a
/// clock of its own and traps where the canonical ABI does.
module TranspilerWasmTests.Features.Components.ComponentCancellation

open System.Collections.Generic
open System.IO
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

/// The canonical ABI's codes: a subtask's states, and the event a subtask's comes with.
let private started = 1
let private returned = 2
let private cancelledBeforeReturned = 4
let private subtaskEvent = 1

/// A host of `wasi:clocks/monotonic-clock` and the waitable built-ins, on a clock that moves only when the module waits.
/// A wait for a set takes the subtask in it whose time comes first, and moves the clock to that time.
type private FakeClockHost() =
    let mutable now = 0L
    let mutable nextHandle = 1
    let deadlines = Dictionary<int, int64>()
    let joined = Dictionary<int, int>()
    let delivered = HashSet<int>()

    let handle () =
        let made = nextHandle
        nextHandle <- nextHandle + 1
        made

    /// The durations, in nanoseconds, of each `wait-for`, by the subtask it started.
    member val Waits = Dictionary<int, int64>()
    /// The subtasks cancelled, and those dropped.
    member val Cancelled = List<int>()
    member val Dropped = List<int>()
    member _.Now = now

    member host.Implement(import: Wasmtime.FunctionImport) : Wasmtime.Function.UntypedCallbackDelegate option =
        match import.ModuleName, import.Name with
        | "wasi:clocks/monotonic-clock@0.3.0", "now" ->
            Some (Wasmtime.Function.UntypedCallbackDelegate(fun _ _ results -> results.[0] <- Wasmtime.ValueBox.op_Implicit now))
        | "wasi:clocks/monotonic-clock@0.3.0", "[async-lower]wait-for" ->
            Some (Wasmtime.Function.UntypedCallbackDelegate(fun _ arguments results ->
                let subtask = handle ()
                let duration = arguments.[0].AsInt64()
                host.Waits.[subtask] <- duration
                deadlines.[subtask] <- now + duration
                results.[0] <- Wasmtime.ValueBox.op_Implicit ((subtask <<< 4) ||| started)))
        | "$root", "[waitable-set-new]" ->
            Some (Wasmtime.Function.UntypedCallbackDelegate(fun _ _ results -> results.[0] <- Wasmtime.ValueBox.op_Implicit (handle ())))
        | "$root", "[waitable-join]" ->
            Some (Wasmtime.Function.UntypedCallbackDelegate(fun _ arguments _ ->
                let waitable = arguments.[0].AsInt32()
                match arguments.[1].AsInt32() with
                | 0 -> joined.Remove waitable |> ignore
                | set -> joined.[waitable] <- set))
        | "$root", "[waitable-set-wait]" ->
            Some (Wasmtime.Function.UntypedCallbackDelegate(fun caller arguments results ->
                let set = arguments.[0].AsInt32()
                let ready =
                    deadlines
                    |> Seq.filter (fun pair -> joined.GetValueOrDefault(pair.Key) = set && not (delivered.Contains pair.Key))
                    |> Seq.sortBy (fun pair -> pair.Value)
                    |> Seq.tryHead
                match ready with
                | None -> failwith "waitable-set.wait on a set with nothing to wait for"
                | Some pair ->
                    now <- max now pair.Value
                    delivered.Add pair.Key |> ignore
                    let memory = caller.GetMemory("memory")
                    let address = int64 (arguments.[1].AsInt32())
                    memory.WriteInt32(address, pair.Key)
                    memory.WriteInt32(address + 4L, returned)
                    results.[0] <- Wasmtime.ValueBox.op_Implicit subtaskEvent))
        | "$root", "[subtask-cancel]" ->
            Some (Wasmtime.Function.UntypedCallbackDelegate(fun _ arguments results ->
                let subtask = arguments.[0].AsInt32()
                // The canonical ABI traps on a subtask in a waitable set, or one whose return was delivered.
                if joined.ContainsKey subtask then failwith $"subtask {subtask} cancelled while in a waitable set"
                if delivered.Contains subtask then failwith $"subtask {subtask} cancelled after its return was delivered"
                host.Cancelled.Add subtask
                delivered.Add subtask |> ignore
                results.[0] <- Wasmtime.ValueBox.op_Implicit cancelledBeforeReturned))
        | "$root", "[subtask-drop]" ->
            Some (Wasmtime.Function.UntypedCallbackDelegate(fun _ arguments _ -> host.Dropped.Add(arguments.[0].AsInt32())))
        | _ -> None

/// The core module of the component `nanyx build` writes for a console program whose `main.nyx` is `main`.
let private coreModuleOf (main: string) =
    use directory = new TempDirectory()
    Directory.CreateDirectory(Path.Combine(directory.Path, "src")) |> ignore
    let manifest = $"""(package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"]), {consoleHostEntry})"""
    File.WriteAllText(Path.Combine(directory.Path, "app.nyxp"), manifest + "\n")
    File.WriteAllText(Path.Combine(directory.Path, "src", "main.nyx"), main)
    let built = nanyxIn directory.Path [ "build"; "--release"; "--wat" ]
    Assert.Equal("", built.StandardError)
    Assert.Equal(0, built.ExitCode)
    File.ReadAllText(Path.Combine(directory.Path, "out", "app.wat"))

[<Fact>]
let ``A race's loser that sleeps cancels its wait, and the race ends when the winner wakes`` () =
    let wat =
        coreModuleOf
            "module main\n\n@entry\ndef main = {\n  ignore(race([{ sleep(50ms) }, { sleep(60s) }]))\n}\n"
    let host = FakeClockHost()
    let engine, store, instance = instantiateCoreWith host.Implement wat
    use _ = engine
    use _ = store
    Assert.Equal(0, instance.GetFunction<int>("wasi:cli/run@0.3.0#run").Invoke())
    let byDuration = host.Waits |> Seq.map (fun pair -> pair.Value, pair.Key) |> dict
    Assert.Equal<int64 list>([ 50_000_000L; 60_000_000_000L ], host.Waits.Values |> Seq.sort |> List.ofSeq)
    Assert.Equal<int list>([ byDuration.[60_000_000_000L] ], List.ofSeq host.Cancelled)
    Assert.Equal<int list>(host.Waits.Keys |> Seq.sort |> List.ofSeq, host.Dropped |> Seq.sort |> List.ofSeq)
    Assert.Equal(50_000_000L, host.Now)

[<Fact>]
let ``Sleeps that are not cancelled wake in turn, and none is cancelled`` () =
    let wat =
        coreModuleOf
            "module main\n\n@entry\ndef main = {\n  def slow = start! sleep(300ms)\n  def quick = start! sleep(50ms)\n  await(slow)\n  await(quick)\n}\n"
    let host = FakeClockHost()
    let engine, store, instance = instantiateCoreWith host.Implement wat
    use _ = engine
    use _ = store
    Assert.Equal(0, instance.GetFunction<int>("wasi:cli/run@0.3.0#run").Invoke())
    Assert.Empty(host.Cancelled)
    Assert.Equal(2, host.Dropped.Count)
    Assert.Equal(300_000_000L, host.Now)
