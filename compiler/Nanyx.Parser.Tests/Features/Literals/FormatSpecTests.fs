/// A hole's format spec, `"{price:.2f}"` (#3173): a `:` outside any bracket the hole opens starts it, it runs raw to the
/// hole's `}`, and the hole is a call to `__format` with the spec as a string written where it is.
module ParserTests.Features.FormatSpecs

open Xunit
open Parser.Program
open ParserTestHelpers

let private parse (source: string) = parseModuleFile "t.nyx" source

/// Each hole of the string `s` in `source`: its value's text, and its spec and the spec's text, if it has one.
let private holes (source: string) =
    match parse source with
    | Result.Ok [ Kind (Def (ValueDef (_, "s", _, Kind (InterpolatedString parts)))) ] ->
        parts
        |> List.choose (function
            | StringExpr { Kind = FormatSyntax.Call(value, spec, span) } -> Some (textOf source value.Span, Some (spec, textOf source span))
            | StringExpr value -> Some (textOf source value.Span, None)
            | StringText _ -> None)
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``A colon outside the hole's brackets starts its spec, which runs to the hole's brace as written`` () =
    Assert.Equal<(string * (string * string) option) list>(
        [ "price", Some (">8.2f", ">8.2f"); "n", Some ("::^5", "::^5"); "n", Some ("=+6", "=+6"); "n", Some ("", ""); "x", None ],
        holes "def s = \"{price:>8.2f} {n:::^5} {n:=+6} {n:} {x}\"\n")

[<Fact>]
let ``A colon inside the brackets of a hole is not its spec`` () =
    Assert.Equal<(string * (string * string) option) list>(
        [ "[1] \\list.map({ x: int -> x })", Some ("d", "d"); "list.map([1], { x: int -> x })", None ],
        holes "def s = \"{[1] \\list.map({ x: int -> x }):d} {list.map([1], { x: int -> x })}\"\n")

[<Fact>]
let ``A spec runs to the hole's brace, and one that reaches the end of its line is never closed`` () =
    Assert.Equal<(string * (string * string) option) list>([ "s", Some ("é>4\"{", "é>4\"{") ], holes "def s = \"{s:é>4\"{}\"\n")
    Assert.Contains("format spec is never closed", fst (parseErrorOf "def s = \"{x:.2f\n"))
