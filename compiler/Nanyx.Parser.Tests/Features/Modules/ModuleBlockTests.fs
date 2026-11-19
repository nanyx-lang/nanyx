/// `module name =` blocks: what they parse to, and how their names are qualified.
module Nanyx.Parser.Tests.ModuleBlockTests

open Xunit
open Parser.Program

[<Fact>]
let ``A module block nested in another parses, and its names are qualified with both`` () =
    let source = "module tally =\n  module steps =\n    def step = 1\n\n  export def total = 2\n"
    match parseModuleFileWithNames "t.nyx" source with
    | Result.Ok(items, _, blocks) ->
        let names =
            items
            |> List.map (function
                | Kind(ModuleDecl(_, name)) -> $"module {name}"
                | Kind(Def(ValueDef(_, name, _, _))) -> name
                | item -> $"%A{item.Kind}")
        Assert.Equal<string list>([ "module tally"; "module tally.steps"; "tally.steps.step"; "module tally"; "tally.total" ], names)
        Assert.Equal<string list>([ "steps"; "tally" ], blocks |> List.map fst)
    | Result.Error error -> Assert.Fail error.Message
