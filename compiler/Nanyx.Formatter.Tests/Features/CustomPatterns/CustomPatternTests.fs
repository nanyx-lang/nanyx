module Nanyx.Formatter.Tests.Features.CustomPatterns.CustomPatterns

open Xunit
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``A pattern declaration keeps its type and its body`` () =
    unchanged (
        """
pattern int -> #Even | #Odd = { n ->
  if n % 2 == 0 then #Even else #Odd
}

export pattern int -> #Zero | _ = { n -> n == 0 }
"""
            .TrimStart('\n')
            .ReplaceLineEndings "\n"
    )

[<Fact>]
let ``A pattern with arguments keeps its curried body`` () =
    unchanged (
        """
pattern string -> string -> #SplitOn(string, string) | _ = { separator ->
  { s ->
    match s \split(on = separator)
      | [a, b] -> #SplitOn(a, b)
      | _ -> _
  }
}
"""
            .TrimStart('\n')
            .ReplaceLineEndings "\n"
    )

[<Fact>]
let ``A pattern declaration is spaced as a def is`` () =
    "pattern   int->#Zero|_ =   { n -> n == 0 }\n" |> formatsTo "pattern int -> #Zero | _ = { n -> n == 0 }"

[<Fact>]
let ``A pattern tag keeps its arguments and its payload in their groups`` () =
    unchanged (
        """
def describe = { s ->
  match s
    | #SplitOn(",")(first, second) -> first
    | #Between(1, 10) & #DivisibleBy(n + 1) -> "x"
    | #Hsl(h, s, l) -> "y"
    | _ -> s
}
"""
            .TrimStart('\n')
            .ReplaceLineEndings "\n"
    )

[<Fact>]
let ``A pattern tag's arguments are spaced as a call's are`` () =
    "def f = { | #SplitOn( \",\" )( a,b ) -> a | _ -> \"\" }\n"
    |> formatsTo "def f = {\n  | #SplitOn(\",\")(a, b) -> a\n  | _ -> \"\"\n}"
