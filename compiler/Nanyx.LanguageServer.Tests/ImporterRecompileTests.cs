using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Xunit;

namespace Nanyx.LanguageServer.Tests;

/// <summary>
/// The open documents that import a file are compiled again when it changes, in the editor or on disk, and read an open
/// file's text from the editor.
/// </summary>
public sealed class ImporterRecompileTests : LanguageServerTestBase
{
    private const string Main = """
        module main
        import mod
        def copy = value

        """;

    private const string Broken = """
        module mod
        export def other = 1

        """;

    private const string Fixed = """
        module mod
        export def value = 1

        """;

    private DocumentUri _main = null!;
    private DocumentUri _mod = null!;
    private string _modPath = null!;

    /// <summary>Opens <c>main.nyx</c>, which uses a name <c>mod.nyx</c> on disk does not export, and checks its error.</summary>
    private async Task OpenMainWithAnError()
    {
        _modPath = TempPath("mod.nyx");
        File.WriteAllText(_modPath, Broken.ReplaceLineEndings("\n"));
        _mod = DocumentUri.FromFileSystemPath(_modPath);
        _main = DocumentUri.FromFileSystemPath(TempPath("main.nyx"));

        Open(_main, Main.ReplaceLineEndings("\n"));
        Assert.Contains("value", Assert.Single((await NextPublished(_main)).Diagnostics!).Message.String!);
    }

    [Fact]
    public async Task A_change_to_an_open_imported_file_compiles_its_importer_again()
    {
        await OpenMainWithAnError();
        Open(_mod, Broken.ReplaceLineEndings("\n"));
        await NextPublished(_mod);

        // The fix is not saved: the importer reads it from the editor.
        Change(_mod, version: 2, Fixed.ReplaceLineEndings("\n"));

        Assert.Empty((await NextPublished(_main)).Diagnostics!);
    }

    [Fact]
    public async Task Closing_an_imported_file_without_saving_compiles_its_importer_against_the_file_on_disk()
    {
        await OpenMainWithAnError();
        Open(_mod, Broken.ReplaceLineEndings("\n"));
        await NextPublished(_mod);
        Change(_mod, version: 2, Fixed.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(_main)).Diagnostics!);

        _client.DidCloseTextDocument(new DidCloseTextDocumentParams { TextDocument = new TextDocumentIdentifier(_mod) });

        Assert.Single((await NextPublished(_main)).Diagnostics!);
    }

    [Fact]
    public async Task Saving_an_imported_file_compiles_its_importer_again()
    {
        await OpenMainWithAnError();
        Open(_mod, Broken.ReplaceLineEndings("\n"));
        await NextPublished(_mod);

        // Written to disk while open, as an editor saves: the save is what compiles the importer.
        File.WriteAllText(_modPath, Fixed.ReplaceLineEndings("\n"));
        _client.DidSaveTextDocument(new DidSaveTextDocumentParams { TextDocument = new TextDocumentIdentifier(_mod) });

        // The importer reads the open file's text, which still has the error.
        Assert.Single((await NextPublished(_main)).Diagnostics!);
    }

    [Fact]
    public async Task A_file_changed_on_disk_compiles_its_importer_again()
    {
        await OpenMainWithAnError();

        File.WriteAllText(_modPath, Fixed.ReplaceLineEndings("\n"));
        _client.DidChangeWatchedFiles(new DidChangeWatchedFilesParams
        {
            Changes = new Container<FileEvent>(new FileEvent { Uri = _mod, Type = FileChangeType.Changed })
        });

        Assert.Empty((await NextPublished(_main)).Diagnostics!);
    }

    [Fact]
    public async Task A_file_imported_through_another_compiles_its_importer_again()
    {
        var subPath = TempPath("sub.nyx");
        File.WriteAllText(subPath, "module sub\nexport def other = missing\n");
        File.WriteAllText(TempPath("mod.nyx"), "module mod\nimport sub\nexport def value = 1\n");
        var main = DocumentUri.FromFileSystemPath(TempPath("main.nyx"));
        Open(main, Main.ReplaceLineEndings("\n"));
        Assert.Equal("'sub.nyx' has errors", Assert.Single((await NextPublished(main)).Diagnostics!).Message.String);

        File.WriteAllText(subPath, "module sub\nexport def other = 1\n");
        _client.DidChangeWatchedFiles(new DidChangeWatchedFilesParams
        {
            Changes = new Container<FileEvent>(new FileEvent { Uri = DocumentUri.FromFileSystemPath(subPath), Type = FileChangeType.Changed })
        });

        Assert.Empty((await NextPublished(main)).Diagnostics!);
    }

    [Fact]
    public async Task A_document_that_does_not_import_the_changed_file_is_not_compiled_again()
    {
        var compiled = new List<DocumentUri>();
        await RestartWithAsync(TestSettings with
        {
            CompileStarted = uri =>
            {
                lock (compiled)
                {
                    compiled.Add(uri);
                }
            }
        });
        await OpenMainWithAnError();
        var other = DocumentUri.FromFileSystemPath(TempPath("other.nyx"));
        Open(other, "def one = 1\n");
        await NextPublished(other);

        File.WriteAllText(_modPath, Fixed.ReplaceLineEndings("\n"));
        _client.DidChangeWatchedFiles(new DidChangeWatchedFilesParams
        {
            Changes = new Container<FileEvent>(new FileEvent { Uri = _mod, Type = FileChangeType.Changed })
        });
        Assert.Empty((await NextPublished(_main)).Diagnostics!);

        // Had the other document been scheduled with the importer, it would have been compiled by now.
        await Task.Delay(500);
        lock (compiled)
        {
            Assert.Equal([_main, other, _main], compiled);
        }
    }

    [Fact]
    public async Task A_file_of_a_package_with_a_type_error_is_not_compiled_again_for_a_change_to_a_file_it_does_not_import()
    {
        // A compile read the package's other files, to offer imports for unknown names, whatever its errors were, and so
        // became an importer of each of them (#2222).
        var compiled = new List<DocumentUri>();
        await RestartWithAsync(TestSettings with
        {
            CompileStarted = uri =>
            {
                lock (compiled)
                {
                    compiled.Add(uri);
                }
            }
        });
        File.WriteAllText(TempPath("app.nyxp"), """
            (package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"]))
            """.ReplaceLineEndings("\n"));
        var firstPath = TempPath("src", "first.nyx");
        File.WriteAllText(firstPath, "module first\nexport def one = 1\n");
        File.WriteAllText(TempPath("src", "second.nyx"), "module second\nexport def wrong: string = 1\n");
        var first = DocumentUri.FromFileSystemPath(firstPath);
        var second = DocumentUri.FromFileSystemPath(TempPath("src", "second.nyx"));
        Open(first, "module first\nexport def one = 1\n");
        Assert.Empty((await NextPublished(first)).Diagnostics!);
        Open(second, "module second\nexport def wrong: string = 1\n");
        Assert.Equal("NYX0201", Assert.Single((await NextPublished(second)).Diagnostics!).Code?.String);

        Change(first, version: 2, "module first\nexport def one = 2\n");
        Assert.Empty((await NextPublished(first)).Diagnostics!);

        // Had the second document been scheduled with the first, it would have been compiled by now.
        await Task.Delay(500);
        lock (compiled)
        {
            Assert.Equal([first, second, first], compiled);
        }
    }
}
