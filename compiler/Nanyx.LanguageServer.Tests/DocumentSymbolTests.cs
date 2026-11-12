using System.IO.Pipelines;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using OmniSharp.Extensions.LanguageServer.Client;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using Xunit;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using Linter = NyxCompiler.Linter;
using LinterOptions = NyxCompiler.LinterOptions;
using Fix = NyxCompiler.Fix;
using Report = NyxCompiler.Report;
using Rule = NyxCompiler.Rule;
using SemanticModel = NyxCompiler.SemanticModel;
using Severity = NyxCompiler.Severity;

namespace Nanyx.LanguageServer.Tests;

/// <summary>Document symbols: the outline of a document.</summary>
[Collection(LanguageServerTestBase.Collection)]
public sealed class DocumentSymbolTests : LanguageServerTestBase
{
    [Fact]
    public void Server_advertises_document_symbols()
    {
        Assert.NotNull(_client.ServerSettings.Capabilities.DocumentSymbolProvider);
    }

    [Fact]
    public async Task Document_symbols_are_the_declarations_with_those_inside_them()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-6");

        Open(uri, "type Point = (x: int, y: int)\ndef area = { s ->\n  def side = s\n  side * side\n}\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var symbols = await DocumentSymbols(uri);
        Assert.Equal(["Point", "area"], symbols.Select(symbol => symbol.Name));

        var point = symbols[0];
        Assert.Equal(SymbolKind.Struct, point.Kind);
        Assert.Equal(new Range(0, 0, 0, 29), point.Range);
        Assert.Equal(new Range(0, 5, 0, 10), point.SelectionRange);
        Assert.Equal(["x", "y"], point.Children!.Select(child => child.Name));
        Assert.All(point.Children!, child => Assert.Equal(SymbolKind.Field, child.Kind));

        var area = symbols[1];
        Assert.Equal(SymbolKind.Function, area.Kind);
        Assert.Equal(new Range(1, 0, 4, 1), area.Range);
        var side = Assert.Single(area.Children!);
        Assert.Equal(("side", SymbolKind.Variable, new Range(2, 6, 2, 10)), (side.Name, side.Kind, side.SelectionRange));
    }

    [Fact]
    public async Task Document_symbols_of_text_that_does_not_parse_are_the_last_good_ones_not_edited_since()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-7");

        Open(uri, "def one = 1\ndef two = 2\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // The second def no longer parses, and is left out; the first, not edited, keeps its symbol.
        Change(uri, 2, "def one = 1\ndef two = (\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        var symbol = Assert.Single(await DocumentSymbols(uri));
        Assert.Equal(("one", new Range(0, 4, 0, 7)), (symbol.Name, symbol.SelectionRange));
    }
}
