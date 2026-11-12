using OmniSharp.Extensions.LanguageServer.Protocol;
using Xunit;

namespace Nanyx.LanguageServer.Tests;

/// <summary>When a document is compiled: at once when it is opened, and after a change once the user pauses.</summary>
[Collection(LanguageServerTestBase.Collection)]
public sealed class CompileSchedulingTests : LanguageServerTestBase
{
    [Fact]
    public async Task A_burst_of_changes_is_compiled_at_most_twice_and_its_last_text_is_published()
    {
        var compiles = 0;
        await RestartWithAsync(ServerSettings.Default with
        {
            CompileDelay = TimeSpan.FromMilliseconds(150),
            CompileStarted = _ => Interlocked.Increment(ref compiles)
        });
        var uri = DocumentUri.From("untitled:burst.nyx");

        Open(uri, "def x = 1\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);
        Assert.Equal(1, Volatile.Read(ref compiles));

        // Ten changes 20 ms apart, as from typing. Only the last refers to a name that does not exist.
        for (var version = 2; version <= 11; version++)
        {
            Change(uri, version, version == 11 ? "def x = missing\n" : $"def x = {version}\n");
            await Task.Delay(20);
        }

        var published = await NextPublished(uri);
        Assert.Equal(11, published.Version);
        Assert.Contains(published.Diagnostics!, diagnostic => diagnostic.Message.String!.Contains("missing"));

        // Nothing else is published for the burst, and it was compiled once, or twice if a pause in it was long enough.
        await Task.Delay(500);
        lock (_published)
        {
            Assert.DoesNotContain(_published, p => p.Uri == uri);
        }

        Assert.InRange(Volatile.Read(ref compiles) - 1, 1, 2);
    }

    [Fact]
    public async Task Requests_during_the_pause_read_the_last_text_that_was_typed()
    {
        await RestartWithAsync(ServerSettings.Default with { CompileDelay = TimeSpan.FromSeconds(10) });
        var uri = DocumentUri.From("untitled:pause.nyx");

        Open(uri, "def answer = 42\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // The change is not compiled yet: hover maps its position back to the opened text, which was typed.
        Change(uri, 2, "def other = 1\ndef answer = 42\n");
        var hover = await HoverAt(uri, 1, 5);
        Assert.NotNull(hover);
        Assert.Contains("int", hover.Contents.MarkupContent!.Value);
    }
}
