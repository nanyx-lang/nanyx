module TranspilerWasmTests.Features.Memory.Examples

open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Theory>]
[<InlineData("directives.nyx")>]
[<InlineData("money.nyx")>]
[<InlineData("shop.nyx")>]
let ``Counting - an example frees everything it allocates`` (example: string) =
    let path = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "..", "examples", example)
    use program = instantiateWat (transpileWatFileWith checkedOptions path) []
    Assert.True(program.Allocations > 0, "the example allocates nothing")
    Assert.Equal(0, program.LiveBlocks)
