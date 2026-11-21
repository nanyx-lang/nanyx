/// A program that asks for more memory than there is stops in the allocator, and the runner reports that as out of
/// memory rather than as a trap (#1349, see docs/src/content/deep-dives/memory-management.md).
module TranspilerWasmTests.Features.Memory.OutOfMemory

open Xunit
open Transpiler.Wasm.CodeGen
open TranspilerWasmTestHelpers

/// A host whose start calls `main`, defined in `definitions`, with `nanyx/host.memory` imported.
let private host (definitions: string) =
    $"""module test.host

import (
  nanyx/host.memory
  nanyx/host.runtime
)

{definitions}
main()
"""

[<Fact>]
let ``A block that would end past 4 GB is out of memory`` () =
    let source =
        host
            """def main: [HostRuntime] () -> () = { ->
  -- Read as unsigned, a size of -16 is 16 bytes short of 4 GB.
  free(allocate(-16))
}
"""
    let errors =
        [ runHost [ "test" ] (hostWatWith WasmOptions.Default source) |> Result.map ignore
          runHost [ "test" ] (hostWatWith { WasmOptions.Default with Inline = false } source) |> Result.map ignore
          runCheckedHost [ "test" ] (hostWatWith checkedOptions source) |> Result.map ignore ]
    Assert.Equal<Result<unit, Nanyx.Runtime.RunError> list>(List.replicate 3 (Error Nanyx.Runtime.OutOfMemory), errors)

[<Fact>]
let ``A trap outside the allocator is not out of memory`` () =
    let source =
        host
            """def main: [HostRuntime] () -> () = { ->
  ignore(readI32(-4))
}
"""
    match runHost [ "test" ] (hostWatWith WasmOptions.Default source) with
    | Error(Nanyx.Runtime.Trapped(message, _)) -> Assert.Contains("out of bounds", message)
    | other -> Assert.Fail $"Expected a trap, got {other}"
