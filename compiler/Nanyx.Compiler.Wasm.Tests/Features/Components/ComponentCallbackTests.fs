/// A component's entry, `run` or `handle`, lifted with the callback ABI (#2240): while every task waits on the host, the
/// entry and its callback return WAIT with the component root's shared waitable set, rather than block in
/// `[waitable-set-wait]`, and the host gives the callback each event. The host is a fake (`FakeWasiHost`).
module TranspilerWasmTests.Features.Components.ComponentCallback

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Two tasks that sleep wait on the host together, through the callback, and run gives ok`` () =
    let wat =
        consoleComponentCore
            "module main\n\n@entry\ndef main = {\n  def slow = start! sleep(300ms)\n  def quick = start! sleep(50ms)\n  await(slow)\n  await(quick)\n}\n"
    let host = FakeWasiHost()
    let engine, store, instance = instantiateCoreWith host.Implement wat
    use _ = engine
    use _ = store
    host.Run instance
    Assert.Equal(0, host.BlockingWaits)
    // One wait for each sleep: both were in flight at once.
    Assert.Equal(2, host.Waited)
    Assert.Equal(300_000_000L, host.Now)

[<Fact>]
let ``A program that never waits gives ok and returns EXIT at once`` () =
    let wat = consoleComponentCore "module main\n\n@entry\ndef main = {\n  ignore(1 + 2)\n}\n"
    let host = FakeWasiHost()
    let engine, store, instance = instantiateCoreWith host.Implement wat
    use _ = engine
    use _ = store
    host.Run instance
    Assert.Equal(0, host.Waited)

[<Fact>]
let ``A handler whose race waits on two sleeps answers once the winner wakes, through the callback`` () =
    let main =
        "module main\n\n@entry\ndef main: [Async, Clock] Request -> Response = { _ ->\n  def winner = race([{ sleep(20ms); \"the hare\" }, { sleep(60s); \"the tortoise\" }])\n  Response.text(200, \"{winner} wins\")\n}\n"
    let host = FakeWasiHost()
    let engine, store, instance = instantiateCoreWith host.Implement (httpComponentCore main)
    use _ = engine
    use _ = store
    host.Handle instance
    Assert.Equal(0, host.BlockingWaits)
    Assert.Equal(1, host.Waited)
    Assert.Equal(1, host.Cancelled.Count)
    Assert.Equal(20_000_000L, host.Now)
    Assert.Equal("the hare wins", host.ResponseBody.ToString())
