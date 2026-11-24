module TranspilerWasmTests.Features.Arrays.MArrayCounting

open Xunit
open TranspilerWasmTestHelpers

// An `MArray` is a box holding an array, and reading or writing an element reads the array the box holds with no
// reference of its own: the box lives until the intrinsic is done, and nothing puts another array in it. Counting that
// read gave every `MArray.at` and `MArray.set` a dup and a drop of the array, which made a loop of them three times as
// slow (#2417).

let private churn (steps: int) =
    normalized $"""import nanyx/marray

def churn: [Memory] MArray(int), int, int -> int = {{ xs, i, acc ->
  if i == 0 then acc else
    MArray.set(xs, i %% 1000, i)
    churn(xs, i - 1, acc + someOr(MArray.at(xs, (i * 7) %% 1000), 0))
}}

println(memory {{
  def xs = MArray.create(1000)
  churn(xs, {steps}, 0)
}})
"""

[<Fact>]
let ``Reading and writing an MArray's elements counts nothing of its array`` () =
    let wat = transpileWat (churn 5)
    let start = wat.IndexOf "(func $churn"
    Assert.True(start >= 0, "no function churn")
    let text = wat.Substring(start, wat.IndexOf("\n  )", start) - start)
    Assert.DoesNotContain("call $__nanyx_dup_array_int", text)
    Assert.DoesNotContain("call $__nanyx_drop_array_int", text)

[<Fact>]
let ``Run - an MArray read and written in a loop frees its array once`` () =
    // Steps 7 down to 1 write `i` at `i`, and step 1 reads index 7, which step 7 wrote; the others read what is unset.
    runSource (churn 7) |> assertRan "7\n"
