module TranspilerWasmTests.Features.StdLib.RunStringTakeSkip

open Xunit
open TranspilerWasmTestHelpers

// `string.take` and `string.skip` cut a string at a byte count, as `list.take` and `list.skip` cut a list (#824).

[<Fact>]
let ``Run - string take and skip cut at a byte count, clamped to the string`` () =
    let source =
        """import nanyx/console
import nanyx/text

@entry
def main = {
  def s = "hello"
  println("[{s \take(2)}] [{s \skip(2)}]")
  println("[{s \take(10)}] [{s \skip(10)}]")
  println("[{s \take(-1)}] [{s \skip(-1)}]")
  println("[{string.take(s, 0)}] [{string.skip(s, 5)}]")
}
"""
    runSource source |> assertRan "[he] [llo]\n[hello] []\n[] [hello]\n[] []\n"
