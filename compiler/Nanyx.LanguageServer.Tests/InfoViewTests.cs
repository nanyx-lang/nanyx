using Nanyx.LanguageServer.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer.Tests;

/// <summary><c>nanyx/infoView</c>, the request behind the extension's info view.</summary>
public sealed partial class LanguageServerTests
{
    [Fact]
    public async Task Info_view_shows_the_expression_and_the_names_in_scope_nearest_first()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-1");

        Open(uri, """
            def greet: string -> string = { name ->
              def shout = name
              shout
            }

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var view = await InfoViewAt(uri, 2, 3);

        var expression = Assert.Single(Section(view, "expression").Items);
        Assert.Equal("shout: string", expression.Text);
        Assert.Equal(new Range(2, 2, 2, 7), expression.Location?.Range);
        Assert.Equal(
            [("shout: string", "local", new Range(1, 6, 1, 11)), ("name: string", "parameter", new Range(0, 32, 0, 36))],
            Section(view, "inScope").Items.Select(item => (item.Text, item.Detail, item.Location?.Range)));
        var topLevel = Section(view, "topLevel");
        Assert.True(topLevel.Collapsed);
        Assert.Equal(("greet: string -> string", "def"), (topLevel.Items[0].Text, topLevel.Items[0].Detail));
        Assert.DoesNotContain(topLevel.Items, item => item.Text.StartsWith("__"));
        Assert.Empty(Section(view, "messages").Items);
    }

    [Fact]
    public async Task Info_view_shows_the_contexts_of_the_signature_and_of_a_use()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-2");

        Open(uri, """
            context Logger = (log: string -> ())
            context Clock = (now: () -> int)
            def quiet: Logger = (log = { s -> () })
            def stamp: [Clock] string -> () = { message ->
              use quiet
              log(message)
            }

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var contexts = Section(await InfoViewAt(uri, 5, 4), "contexts").Items;

        Assert.Equal(
            [("Logger", "use", new Range(4, 2, 4, 11)), ("Clock", "signature", new Range(3, 11, 3, 31))],
            contexts.Select(item => (item.Text, item.Detail, item.Location?.Range)));
    }

    [Fact]
    public async Task Info_view_shows_contexts_inferred_for_a_def_and_the_runtime_at_the_top_level()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-3");

        Open(uri, """
            import nanyx/console

            def hello = { println("hi") }
            println("top")

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var inDef = Assert.Single(Section(await InfoViewAt(uri, 2, 16), "contexts").Items);
        Assert.Equal(("Console", "inferred", new Range(2, 4, 2, 9)), (inDef.Text, inDef.Detail, inDef.Location?.Range));
        var atTopLevel = Assert.Single(Section(await InfoViewAt(uri, 3, 2), "contexts").Items);
        Assert.Equal(("Console", "runtime", null), (atTopLevel.Text, atTopLevel.Detail, atTopLevel.Location));
    }

    [Fact]
    public async Task Info_view_shows_the_diagnostics_on_the_line()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-4");

        Open(uri, """
            import nanyx/console

            println(missing)

            """.ReplaceLineEndings("\n"));
        Assert.Single((await NextPublished(uri)).Diagnostics!);

        var message = Assert.Single(Section(await InfoViewAt(uri, 2, 0), "messages").Items);
        Assert.Equal("error NYX0101", message.Detail);
        Assert.Contains("missing", message.Text);
        Assert.Equal(new Range(2, 8, 2, 15), message.Location?.Range);
        Assert.Empty(Section(await InfoViewAt(uri, 0, 0), "messages").Items);
    }

    [Fact]
    public async Task Info_view_reads_the_last_typed_text_while_the_text_does_not_parse()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-5");

        Open(uri, "def double: int -> int = { n -> n + n }\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);
        Change(uri, 2, "def double: int -> int = { n -> n + n }\ndef broken = (\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        var view = await InfoViewAt(uri, 0, 32);

        Assert.Equal("n: int", Assert.Single(Section(view, "expression").Items).Text);
        Assert.Equal("n: int", Assert.Single(Section(view, "inScope").Items).Text);
    }

    private async Task<InfoViewResult> InfoViewAt(DocumentUri uri, int line, int character)
    {
        var result = await _client
            .SendRequest(InfoViewHandler.Method, new InfoViewParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Position = new Position(line, character)
            })
            .Returning<InfoViewResult?>(_cancellation.Token);
        Assert.NotNull(result);
        Assert.Equal(["expression", "inScope", "topLevel", "contexts", "messages"], result.Sections.Select(section => section.Id));
        return result;
    }

    private static InfoViewSection Section(InfoViewResult view, string id) => view.Sections.Single(section => section.Id == id);
}
