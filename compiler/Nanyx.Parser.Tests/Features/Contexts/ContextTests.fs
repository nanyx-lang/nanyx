module ParserTests.Features.Contexts

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers

[<Fact>]
let ``Parse context feature files`` () =
    parseFeatureNyxFiles "Contexts"
    |> Array.iter (fun (filePath, result) ->
        result |> isOk |> should equal true
        match result with
        | Result.Error err -> failwith $"Parse failed for {filePath}: {err}"
        | _ -> ()
    )

[<Fact>]
let ``use C(T) = expr does not parse, and the error says to write use <expr> where the context is named`` () =
    for line, head in [ "  use Show(int) = (show = { n -> \"n\" })", "Show(int)"; "  use Console = (println = log)", "Console" ] do
        let source = $"def main = {{\n{line}\n  1\n}}"
        match parseModuleFileWithNames "t.nyx" source with
        | Result.Error err ->
            // FParsec wraps a long message onto several lines.
            let message = err.Message.Replace("\r\n", " ").Replace("\n", " ")
            Assert.Contains($"`use {head} = ...` is not a way to load an instance: write `use <expr>`.", message)
            Assert.Contains($"Define the instance with `def name: {head} = (...)`, then load it with `use name`.", message)
            Assert.Equal(source.IndexOf head, err.Span.Start)
        | Result.Ok _ -> failwith $"Expected '{line.Trim()}' not to parse"
