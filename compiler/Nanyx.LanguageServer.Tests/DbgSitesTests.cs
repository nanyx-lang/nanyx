using Nanyx.LanguageServer.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace Nanyx.LanguageServer.Tests;

/// <summary>
/// <c>nanyx/dbgSites</c>: where the <c>dbg</c>s a run reached are in the document now, so that the extension keeps each
/// value on its line (#2054). The matching itself is <c>DbgSites</c>'s, tested in the compiler's tests.
/// </summary>
public sealed class DbgSitesTests : LanguageServerTestBase
{
    private const string Ran = "def twice: int -> int = { n ->\n  dbg(n)\n  n * 2\n}\n\ndbg(twice(3))\n";

    private async Task<IReadOnlyList<DbgPlace?>?> Sites(DocumentUri uri, params (int Line, int Column)[] places) =>
        await _client
            .SendRequest(
                DbgSitesHandler.Method,
                new DbgSitesParams
                {
                    TextDocument = new TextDocumentIdentifier(uri),
                    Before = Ran,
                    Places = places.Select(place => new DbgPlace { Line = place.Line, Column = place.Column }).ToList()
                })
            .Returning<IReadOnlyList<DbgPlace?>?>(_cancellation.Token);

    [Fact]
    public async Task A_dbg_moved_down_by_lines_added_above_it_is_where_it_is_now()
    {
        var uri = DocumentUri.From("untitled:moved.nyx");
        Open(uri, "-- doubles\n\n" + Ran);
        await NextPublished(uri);

        var sites = await Sites(uri, (2, 3), (6, 1));

        Assert.Equal([new DbgPlace { Line = 4, Column = 3 }, new DbgPlace { Line = 8, Column = 1 }], sites);
    }

    [Fact]
    public async Task A_deleted_dbg_has_no_place_and_a_document_that_does_not_parse_has_none_at_all()
    {
        var uri = DocumentUri.From("untitled:deleted.nyx");
        Open(uri, Ran.Replace("  dbg(n)\n", ""));
        await NextPublished(uri);

        Assert.Equal([null, new DbgPlace { Line = 5, Column = 1 }], await Sites(uri, (2, 3), (6, 1)));

        var broken = DocumentUri.From("untitled:broken.nyx");
        Open(broken, "def twice = (\n");
        await NextPublished(broken);
        Assert.Null(await Sites(broken, (2, 3)));
    }
}
