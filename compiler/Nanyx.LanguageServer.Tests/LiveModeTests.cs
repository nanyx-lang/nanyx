using Nanyx.LanguageServer.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace Nanyx.LanguageServer.Tests;

/// <summary>
/// <c>nanyx/liveMode</c>: a document's entry point and the contexts it requires, which say whether the extension's live
/// mode is offered on its own (#2055). Which entry points are offered is <c>LiveMode</c>'s, tested in the compiler's
/// tests.
/// </summary>
public sealed class LiveModeTests : LanguageServerTestBase
{
    private async Task<LiveModeResult> LiveMode(string name, string text)
    {
        var uri = DocumentUri.From($"untitled:{name}");
        Open(uri, text);
        await NextPublished(uri);
        return await _client
            .SendRequest(LiveModeHandler.Method, new LiveModeParams { TextDocument = new TextDocumentIdentifier(uri) })
            .Returning<LiveModeResult>(_cancellation.Token);
    }

    [Fact]
    public async Task A_script_that_prints_is_offered_live_mode()
    {
        var live = await LiveMode("prints.nyx", "println(\"hi\")\n");

        Assert.Equal("statements", live.Entry);
        Assert.Equal(["Console"], live.Contexts);
        Assert.True(live.Offered);
    }

    [Fact]
    public async Task A_script_that_reads_its_arguments_is_not_offered_it_on_its_own()
    {
        var live = await LiveMode("arguments.nyx", "println(\"{args()}\")\n");

        Assert.Contains("Env", live.Contexts);
        Assert.False(live.Offered);
    }

    [Fact]
    public async Task A_file_with_nothing_to_run_has_no_entry_point()
    {
        var live = await LiveMode("nothing.nyx", "def answer = 42\n");

        Assert.Null(live.Entry);
        Assert.Empty(live.Contexts);
        Assert.False(live.Offered);
    }
}
