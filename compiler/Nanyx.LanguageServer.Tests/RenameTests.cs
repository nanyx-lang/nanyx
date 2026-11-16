using OmniSharp.Extensions.JsonRpc.Server;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer.Tests;

/// <summary>
/// Rename: a name and every use of it in the document. A refusal is a <c>RequestFailed</c> error whose message is the
/// reason; the protocol library's client keeps only the code, so the reasons are tested in the compiler's tests.
/// </summary>
public sealed class RenameTests : LanguageServerTestBase
{
    [Fact]
    public void Server_advertises_rename_with_prepare()
    {
        Assert.True(_client.ServerSettings.Capabilities.RenameProvider?.Value?.PrepareProvider);
    }

    [Fact]
    public async Task Rename_renames_a_local_and_its_uses()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-rename-1");
        const string text = "def area = { (s: int) ->\n  def side = s * 2\n  side * side\n}\n";

        Open(uri, text);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var prepared = await _client.TextDocument.PrepareRename(
            new PrepareRenameParams { TextDocument = new TextDocumentIdentifier(uri), Position = new Position(1, 7) },
            _cancellation.Token);
        Assert.Equal(("side", new Range(1, 6, 1, 10)), (prepared!.PlaceholderRange!.Placeholder, prepared.PlaceholderRange.Range));

        var edit = await _client.TextDocument.RequestRename(
            new RenameParams { TextDocument = new TextDocumentIdentifier(uri), Position = new Position(2, 3), NewName = "width" },
            _cancellation.Token);
        Assert.Equal(
            "def area = { (s: int) ->\n  def width = s * 2\n  width * width\n}\n",
            Applied(text, edit!.Changes![uri]));
    }

    [Fact]
    public async Task Rename_of_an_exported_name_edits_every_file_of_the_package_that_uses_it()
    {
        File.WriteAllText(TempPath("app.nyxp"), """
            (package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"]))
            """.ReplaceLineEndings("\n"));
        var shapesText = """
            module shapes

            export def origin: int = 0
            """.ReplaceLineEndings("\n");
        var shapes = TempPath("src", "shapes.nyx");
        File.WriteAllText(shapes, shapesText);
        var mainText = """
            module main

            import app/shapes

            def here = origin + 1
            """.ReplaceLineEndings("\n");
        var main = DocumentUri.FromFileSystemPath(TempPath("src", "main.nyx"));
        File.WriteAllText(TempPath("src", "main.nyx"), mainText);

        Open(main, mainText);
        Assert.Empty((await NextPublished(main)).Diagnostics!);

        var prepared = await _client.TextDocument.PrepareRename(
            new PrepareRenameParams { TextDocument = new TextDocumentIdentifier(main), Position = new Position(4, 12) },
            _cancellation.Token);
        Assert.Equal("origin", prepared!.PlaceholderRange!.Placeholder);

        var edit = await _client.TextDocument.RequestRename(
            new RenameParams { TextDocument = new TextDocumentIdentifier(main), Position = new Position(4, 12), NewName = "start" },
            _cancellation.Token);
        Assert.Equal(mainText.Replace("origin", "start"), Applied(mainText, edit!.Changes![main]));
        Assert.Equal(shapesText.Replace("origin", "start"), Applied(shapesText, edit.Changes[DocumentUri.FromFileSystemPath(shapes)]));
        Assert.Equal(2, edit.Changes.Count);
    }

    [Fact]
    public async Task Renaming_a_module_edits_its_module_line_and_imports_and_renames_its_file()
    {
        File.WriteAllText(TempPath("app.nyxp"), """
            (package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"]))
            """.ReplaceLineEndings("\n"));
        var shapesText = """
            module shapes

            export def origin: int = 0
            """.ReplaceLineEndings("\n");
        var shapes = TempPath("src", "shapes.nyx");
        File.WriteAllText(shapes, shapesText);
        var mainText = """
            module main

            import app/shapes

            def here = origin + 1
            """.ReplaceLineEndings("\n");
        var main = DocumentUri.FromFileSystemPath(TempPath("src", "main.nyx"));
        File.WriteAllText(TempPath("src", "main.nyx"), mainText);

        Open(main, mainText);
        Assert.Empty((await NextPublished(main)).Diagnostics!);

        var prepared = await _client.TextDocument.PrepareRename(
            new PrepareRenameParams { TextDocument = new TextDocumentIdentifier(main), Position = new Position(2, 13) },
            _cancellation.Token);
        Assert.Equal(("shapes", new Range(2, 11, 2, 17)), (prepared!.PlaceholderRange!.Placeholder, prepared.PlaceholderRange.Range));

        var edit = await _client.TextDocument.RequestRename(
            new RenameParams { TextDocument = new TextDocumentIdentifier(main), Position = new Position(2, 13), NewName = "geometry" },
            _cancellation.Token);
        var changes = edit!.DocumentChanges!.ToList();
        var texts = changes
            .Where(change => change.IsTextDocumentEdit)
            .ToDictionary(change => change.TextDocumentEdit!.TextDocument.Uri, change => change.TextDocumentEdit!.Edits);
        Assert.Equal(mainText.Replace("app/shapes", "app/geometry"), Applied(mainText, texts[main]));
        Assert.Equal(shapesText.Replace("module shapes", "module geometry"), Applied(shapesText, texts[DocumentUri.FromFileSystemPath(shapes)]));
        var renamed = Assert.Single(changes, change => change.IsRenameFile).RenameFile!;
        Assert.Equal(
            (DocumentUri.FromFileSystemPath(shapes), DocumentUri.FromFileSystemPath(TempPath("src", "geometry.nyx"))),
            (renamed.OldUri, renamed.NewUri));
        Assert.Equal(changes.Count - 1, changes.FindIndex(change => change.IsRenameFile));
    }

    [Fact]
    public async Task A_rename_that_would_change_what_a_name_refers_to_fails()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-rename-2");
        const string text = "def limit = 10\ndef check = { (n: int) ->\n  def cap = n * 2\n  cap < limit\n}\n";

        Open(uri, text);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var failure = await Assert.ThrowsAsync<RequestFailedException>(() => _client.TextDocument.RequestRename(
            new RenameParams { TextDocument = new TextDocumentIdentifier(uri), Position = new Position(2, 7), NewName = "limit" },
            _cancellation.Token));
        Assert.Equal(-32803, failure.ErrorCode);
    }

    [Fact]
    public async Task Prepare_rename_on_a_standard_library_name_fails()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-rename-3");
        const string text = "def n = [1, 2] \\sum\n";

        Open(uri, text);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var failure = await Assert.ThrowsAsync<RequestFailedException>(() => _client.TextDocument.PrepareRename(
            new PrepareRenameParams { TextDocument = new TextDocumentIdentifier(uri), Position = new Position(0, 17) },
            _cancellation.Token));
        Assert.Equal(-32803, failure.ErrorCode);
    }
}
