module ParserTests.Features.Keywords

open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Xunit
open Parser.Program

/// The VS Code extension's TextMate grammar, which highlights `.nyx` files.
let private grammarPath =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "..", "extension", "syntaxes", "nanyx.tmLanguage.json"))

/// The words the grammar's `keywords` rules highlight: each rule's `match` is an alternation, `\b(a|b|c)\b`.
let private highlightedKeywords () =
    use grammar = JsonDocument.Parse(File.ReadAllText grammarPath)
    let patterns = grammar.RootElement.GetProperty("repository").GetProperty("keywords").GetProperty("patterns")
    [ for pattern in patterns.EnumerateArray() do
          let alternation = Regex.Match(pattern.GetProperty("match").GetString(), @"\(([a-z|]+)\)")
          if alternation.Success then
              yield! alternation.Groups[1].Value.Split('|') ]
    |> Set.ofList

[<Fact>]
let ``The extension's grammar highlights every reserved word`` () =
    let missing = Keywords.reserved - highlightedKeywords () |> Set.toList
    Assert.Equal<string list>([], missing)

[<Fact>]
let ``The extension's grammar highlights scoped, which is a keyword only before context`` () =
    Assert.Contains("scoped", highlightedKeywords ())
