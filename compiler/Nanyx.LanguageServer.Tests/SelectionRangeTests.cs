using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer.Tests;

/// <summary>Selection ranges: expand and shrink selection along the syntax tree.</summary>
public sealed class SelectionRangeTests : LanguageServerTestBase
{
    [Fact]
    public void Server_advertises_selection_ranges()
    {
        Assert.NotNull(_client.ServerSettings.Capabilities.SelectionRangeProvider);
    }

    [Fact]
    public async Task Selection_ranges_grow_from_the_name_at_the_position_to_the_whole_document()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-selection-1");
        var text = "def area = { s ->\n  def side = max(s, 2)\n  side * side\n}\n";

        Open(uri, text);
        await NextPublished(uri);

        var result = await _client.TextDocument.RequestSelectionRange(
            new SelectionRangeParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Positions = new Container<Position>(new Position(1, 17))
            },
            _cancellation.Token);

        var ranges = new List<Range>();
        for (var range = Assert.Single(result!); range is not null; range = range.Parent)
        {
            ranges.Add(range.Range);
        }

        Assert.Equal(
            [
                new Range(1, 17, 1, 18), // s
                new Range(1, 17, 1, 21), // s, 2
                new Range(1, 13, 1, 22), // max(s, 2)
                new Range(1, 2, 1, 22), // def side = max(s, 2)
                new Range(1, 2, 2, 13), // the lambda's body
                new Range(0, 11, 3, 1), // the lambda
                new Range(0, 0, 3, 1), // def area
                new Range(0, 0, 4, 0) // the document
            ],
            ranges);
    }
}
