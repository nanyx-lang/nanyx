module TranspilerWasmTests.Features.Core.RunLexical

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - numbers grouped with underscores have their value, and block comments are skipped`` () =
    """import nanyx/console

---
This is a multi-line comment.
println("not code")
---

----------------------------------
This is also a multi-line comment.
---
----------------------------------

def million = 1_000_000
def pi = 3.141_592

@entry
def main = {
  ---
  println("not code either")
  ---
  println("{million} {pi} {million + 1_000}")
}"""
    |> runSource
    |> assertRan "1000000 3.141592 1001000\n"
