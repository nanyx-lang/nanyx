/// The examples of the official style on the Formatter page: each `-- before` formats to its `-- after`.
module Nanyx.Formatter.Tests.StyleExampleTests

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open Nanyx.Formatter.Tests.TestHelpers

let private formatterPage =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "..", "docs", "src", "content", "standard-tools", "formatter.md"))

/// The page's code blocks that show a `-- before` and an `-- after`, as their before and after.
let private examples =
    let codeBlock = Regex(@"```nanyx[^\n]*\n(.*?)```", RegexOptions.Singleline)
    codeBlock.Matches((File.ReadAllText formatterPage).ReplaceLineEndings "\n")
    |> Seq.map (fun m -> m.Groups[1].Value)
    |> Seq.filter (fun block -> block.StartsWith "-- before\n" && block.Contains "\n-- after\n")
    |> Seq.map (fun block ->
        let parts = block.Substring("-- before\n".Length).Split("\n-- after\n")
        parts[0].Trim('\n'), parts[1].Trim('\n'))
    |> List.ofSeq

[<Fact>]
let ``Each example on the Formatter page formats from its before to its after`` () =
    Assert.True(examples.Length >= 4, $"Only {examples.Length} examples found in {formatterPage}")
    for before, after in examples do
        formatsTo after before
