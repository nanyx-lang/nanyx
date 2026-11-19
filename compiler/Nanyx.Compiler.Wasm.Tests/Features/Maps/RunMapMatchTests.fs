module TranspilerWasmTests.Features.Maps.RunMapMatch

open Xunit
open TranspilerWasmTestHelpers

// A local bound to a match on a lookup, whose arms are the value found and `#null`, in an arm of an outer match, before
// a call with a lambda. The checker made the map's values `#null` there, so the value found was never dropped (#2070).

/// A program with the type `J`, whose `pick` reads `j("value")` into a local, `payload`, and then folds over a list of
/// names with a lambda whose found value is `found`. `main` prints what `pick` gives for an object holding `value`.
let private program (found: string) =
    [ "type J = #num(int) | #null | #obj(map(string, J))"
      ""
      """def describe: () -> #union((name: string, cases: list(string))) | #int = { -> #union((name = "R", cases = ["admin", "member"])) }"""
      ""
      "def depth: J -> int = { j ->"
      "  match j"
      "    | #obj(_) -> 1"
      "    | _ -> 0"
      "}"
      ""
      "def pick: J -> #some(int) | #error(string) = { j ->"
      "  match describe()"
      "    | #union(u) ->"
      "      match j"
      "        | #obj(o) ->"
      "          def payload ="
      """            match map.get(o, "value")"""
      "              | #some(v) -> v"
      "              | _ -> #null"
      """          def unknown: #some(int) | #error(string) = #error("unknown")"""
      "          u.cases"
      "            \\list.fold(unknown, { found, c ->"
      $"""              if c == "member" then #some({found}) else found"""
      "            })"
      """        | _ -> #error("not an object")"""
      """    | _ -> #error("no")"""
      "}"
      ""
      "@entry"
      "def main = {"
      """  match pick(#obj(map.fromList([("value", #obj(map.fromList([("inner", #num(2))])))])))"""
      """    | #some(p) -> println("{p}")"""
      "    | #error(e) -> println(e)"
      "}" ]
    |> String.concat "\n"

[<Fact>]
let ``Run - an unused local of a map.get match in a match arm is dropped`` () =
    program "1" |> runSource |> assertRan "1\n"

[<Fact>]
let ``Run - a local of a map.get match captured by a lambda and passed to a call is dropped`` () =
    program "depth(payload)" |> runSource |> assertRan "1\n"
