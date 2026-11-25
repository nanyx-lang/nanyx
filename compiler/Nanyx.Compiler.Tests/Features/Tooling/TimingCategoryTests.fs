module Nanyx.Compiler.Tests.Features.TimingCategory

open Xunit

// A test that times something is in the `Timing` category, which a local run may leave out; timing checks it (see
// `Nanyx.TestTiming`), so a new one can't be left out of it by mistake (#2837).

[<Fact>]
let ``A test that times something without the Timing category fails`` () =
    let error = Assert.ThrowsAny<exn>(fun () -> Nanyx.TestTiming.millisecondsOf ignore |> ignore)
    Assert.Equal("'A test that times something without the Timing category fails' times what it runs, so it needs [<Trait(\"Category\", \"Timing\")>]", error.Message)

[<Fact; Trait("Category", "Timing")>]
let ``A test in the Timing category times what it runs`` () =
    Assert.True(Nanyx.TestTiming.millisecondsOf ignore >= 0.0)
