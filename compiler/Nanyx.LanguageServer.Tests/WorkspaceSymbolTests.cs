using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Xunit;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer.Tests;

/// <summary>Go to Symbol in Workspace: the top-level declarations of every <c>.nyx</c> file under the workspace folders.</summary>
public sealed class WorkspaceSymbolTests : LanguageServerTestBase
{
    [Fact]
    public void Server_advertises_workspace_symbols()
    {
        Assert.NotNull(_client.ServerSettings.Capabilities.WorkspaceSymbolProvider);
    }

    [Fact]
    public async Task A_def_type_or_context_is_found_in_a_file_that_is_not_open_but_not_in_bin_obj_or_node_modules()
    {
        var root = Path.GetDirectoryName(TempPath("app.nyxp"))!;
        var shapes = TempPath("src", "shapes.nyx");
        File.WriteAllText(shapes, """
            module shapes

            type Point = (x: int, y: int)

            context Logger = (log: string -> ())

            -- Not yet type-checked: the file is only parsed.
            def origin: Point = "nowhere"

            module geometry =
              def area = { w, h -> w * h }
            """.ReplaceLineEndings("\n"));
        File.WriteAllText(TempPath("bin", "copy.nyx"), "def originCopy = 1\n");
        File.WriteAllText(TempPath("node_modules", "dep.nyx"), "def originDep = 1\n");
        await RestartInFolderAsync(root);

        var origin = Assert.Single(await SymbolsMatching("origin"));
        Assert.Equal(("origin", SymbolKind.Variable, DocumentUri.FromFileSystemPath(shapes), new Range(7, 0, 7, 29)), (origin.Name, origin.Kind, origin.Location.Location!.Uri, origin.Location.Location.Range));
        Assert.Equal(SymbolKind.Struct, Assert.Single(await SymbolsMatching("Point")).Kind);
        Assert.Equal(SymbolKind.Interface, Assert.Single(await SymbolsMatching("Logger")).Kind);
        // A query matches a name with its characters in order, ignoring case.
        Assert.Contains(await SymbolsMatching("orgn"), symbol => symbol.Name == "origin");
        // A declaration in a `module name =` block has the block as its container.
        var area = Assert.Single(await SymbolsMatching("area"));
        Assert.Equal("geometry", area.ContainerName);
    }

    [Fact]
    public async Task A_file_made_after_the_scan_is_found_once_the_watcher_reports_it()
    {
        var root = Path.GetDirectoryName(TempPath("app.nyxp"))!;
        File.WriteAllText(TempPath("first.nyx"), "def one = 1\n");
        await RestartInFolderAsync(root);
        Assert.Single(await SymbolsMatching("one"));

        var second = TempPath("second.nyx");
        File.WriteAllText(second, "def two = 2\n");
        File.WriteAllText(TempPath("first.nyx"), "def uno = 1\n");
        _client.Workspace.DidChangeWatchedFiles(new DidChangeWatchedFilesParams
        {
            Changes = new Container<FileEvent>(
                new FileEvent { Uri = DocumentUri.FromFileSystemPath(second), Type = FileChangeType.Created },
                new FileEvent { Uri = DocumentUri.FromFileSystemPath(TempPath("first.nyx")), Type = FileChangeType.Changed })
        });

        // The notification has no reply, so the request after it is answered after it is handled.
        Assert.Single(await SymbolsMatching("two"));
        Assert.Single(await SymbolsMatching("uno"));
        Assert.Empty(await SymbolsMatching("one"));
    }

    private async Task<List<WorkspaceSymbol>> SymbolsMatching(string query) =>
        (await _client.TextDocument.RequestWorkspaceSymbols(new WorkspaceSymbolParams { Query = query }, _cancellation.Token))?.ToList() ?? [];
}
