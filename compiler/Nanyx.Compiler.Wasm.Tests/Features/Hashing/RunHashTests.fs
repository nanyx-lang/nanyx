module TranspilerWasmTests.Features.Hashing.Run

open Xunit
open TranspilerWasmTestHelpers

/// `same(x, y)` says whether `x` and `y` have the same hash, through the `Hash` a generic function is passed.
let private sameHash = "def same: [Hash(a)] a, a -> bool = { x, y -> hash(x) == hash(y) }"

[<Fact>]
let ``Run - values equal by their structure have the same hash`` () =
    comparing
        [ sameHash ]
        [ "same(s, \"aa\")"
          "same(42, 42)"
          "same(true, true)"
          "same((x = 1, y = [2.0]), (x = 1, y = [2.0]))"
          "same((1, s), (1, \"aa\"))"
          "same([[s], [\"b\"]], [[\"aa\"], [\"b\"]])"
          "same(#some(s), #some(\"aa\"))"
          "same(#rgb(1, 2, 3), #rgb(1, 2, 3))"
          "same(0.0, -0.0)" ]
    |> runSource
    |> assertRan "T\nT\nT\nT\nT\nT\nT\nT\nT\n"

[<Fact>]
let ``Run - the hash tells apart values that differ in a part`` () =
    comparing
        [ sameHash ]
        [ "same(\"ab\", \"ba\")"
          "same(1, 2)"
          "same((x = 1, y = 2), (x = 2, y = 1))"
          "same([1, 2], [2, 1])"
          "same([1, 2], [1, 2, 3])"
          "same(#some(1), #some(2))"
          "same(#rgb(1, 2, 3), #rgb(3, 2, 1))"
          "same(1.5, 2.5)" ]
    |> runSource
    |> assertRan "F\nF\nF\nF\nF\nF\nF\nF\n"

[<Fact>]
let ``Run - a nominal type's own hash is used, and the built-in types' hashes can be called`` () =
    comparing
        [ sameHash
          "type $Name = string"
          "def $Name.`==`: $Name, $Name -> bool = { a, b -> true }"
          "def $Name.hash: $Name -> int = { n -> 7 }" ]
        [ "same($Name(\"Ada\"), $Name(\"Grace\"))"
          "string.hash(s) == string.hash(\"aa\")"
          "int.hash(3) == int.hash(1 + 2)"
          "bool.hash(true) != bool.hash(false)"
          "float.hash(0.0) == float.hash(-0.0)" ]
    |> runSource
    |> assertRan "T\nT\nT\nT\nT\n"
