/// Cancellation in a component (#2240): a task cancelled while it waits for a subtask, as a `race`'s loser is, cancels
/// the subtask with `[subtask-cancel]`, so what the host does for it stops then. The host is a fake (`FakeWasiHost`),
/// which keeps a clock of its own and traps where the canonical ABI does.
module TranspilerWasmTests.Features.Components.ComponentCancellation

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``A race's loser that sleeps cancels its wait, and the race ends when the winner wakes`` () =
    let wat = consoleComponentCore "module main\n\n@entry\ndef main = {\n  ignore(race([{ sleep(50ms) }, { sleep(60s) }]))\n}\n"
    let host = FakeWasiHost()
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
        consoleComponentCore
            "module main\n\n@entry\ndef main = {\n  def slow = start! sleep(300ms)\n  def quick = start! sleep(50ms)\n  await(slow)\n  await(quick)\n}\n"
    let host = FakeWasiHost()
    let engine, store, instance = instantiateCoreWith host.Implement wat
    use _ = engine
    use _ = store
    Assert.Equal(0, instance.GetFunction<int>("wasi:cli/run@0.3.0#run").Invoke())
    Assert.Empty(host.Cancelled)
    Assert.Equal(2, host.Dropped.Count)
    Assert.Equal(300_000_000L, host.Now)
