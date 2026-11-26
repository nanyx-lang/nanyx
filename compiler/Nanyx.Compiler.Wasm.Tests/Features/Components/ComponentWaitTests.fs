/// Waiting in a component while other tasks run (#2240): what a function that requires `Async` waits on, a subtask or
/// a stream's or a future's end, joins the instance's shared waitable set, so the program's other tasks run meanwhile.
/// The host is a fake (`FakeWasiHost`), which keeps a clock of its own.
module TranspilerWasmTests.Features.Components.ComponentWaits

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Another task sleeps while a request's body is written, and wakes before the host has taken it`` () =
    let main =
        "module main\n\nimport nanyx.http/http as http\n\n@entry\ndef main = {\n  def sending = start! http.post(\"http://example.com/\", \"a body\")\n  def sleeping = start! sleep(10ms)\n  await(sleeping)\n  ignore(await(sending))\n}\n"
    let host = FakeWasiHost()
    let engine, store, instance = instantiateCoreWith host.Implement (consoleComponentCore main)
    use _ = engine
    use _ = store
    host.Run instance
    let sleep = Assert.Single(host.WaitStarts.Keys)
    let writer = Assert.Single(host.BodyWriters)
    // The sleep starts while the body's write is blocked, not once the host has taken it, and wakes first.
    Assert.Equal(0L, host.WaitStarts.[sleep])
    Assert.True(host.Delivered.IndexOf sleep < host.Delivered.IndexOf writer, $"delivered in the order %A{List.ofSeq host.Delivered}")
