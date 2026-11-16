using NyxCompiler;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Go to Symbol in Workspace: the defs, types, contexts and <c>module name =</c> blocks declared at the top level of
/// any <c>.nyx</c> file under the workspace folders, and the declarations in each block, whose names match the query
/// (see <see cref="WorkspaceIndex"/>). Files are only parsed, so a file with type errors still has its symbols; one that
/// does not parse has none. An open file's symbols are those of its editor text.
/// </summary>
public sealed class WorkspaceSymbolsHandler : WorkspaceSymbolsHandlerBase
{
    /// <summary>The most symbols one answer lists; the editor asks again as the query grows.</summary>
    private const int Limit = 500;

    private readonly WorkspaceIndex _index;
    private readonly DocumentStore _documents;
    private readonly ILanguageServerFacade _server;

    public WorkspaceSymbolsHandler(WorkspaceIndex index, DocumentStore documents, ILanguageServerFacade server)
    {
        _index = index;
        _documents = documents;
        _server = server;
    }

    public override Task<Container<WorkspaceSymbol>?> Handle(WorkspaceSymbolParams request, CancellationToken cancellationToken) =>
        Task.FromResult<Container<WorkspaceSymbol>?>(new Container<WorkspaceSymbol>(Compute(request.Query).Take(Limit)));

    private IEnumerable<WorkspaceSymbol> Compute(string query)
    {
        foreach (var (path, text, outline) in _index.Files(WorkspaceFolders))
        {
            // An open file is searched as the editor has it.
            var (uri, positions, items) = _documents.TryGetFile(path) is var (openUri, document) && document.Outline is { } openOutline
                ? (openUri, document.Positions, openOutline)
                : (DocumentUri.FromFileSystemPath(path), new SourcePositions(text), outline);
            foreach (var (item, container) in items.SelectMany(Declared))
            {
                if (Matches(query, item.Name))
                {
                    yield return new WorkspaceSymbol
                    {
                        Name = item.Name,
                        Kind = DocumentSymbolHandler.KindOf(item.Kind),
                        ContainerName = container,
                        Location = new Location { Uri = uri, Range = positions.ToRange(item.Span.Start, item.Span.End) }
                    };
                }
            }
        }
    }

    /// <summary>A top-level item, and each declaration in it if it is a <c>module name =</c> block, with the block's name.</summary>
    private static IEnumerable<(Outline.OutlineItem Item, string? Container)> Declared(Outline.OutlineItem item) =>
        item.Kind.IsModuleBlock
            ? item.Children.Select(child => (child, (string?)item.Name)).Prepend((item, null))
            : [(item, null)];

    /// <summary>
    /// Whether <paramref name="name"/> matches <paramref name="query"/>: it has the query's characters in order, ignoring
    /// case, as VS Code matches symbols. An empty query matches every name.
    /// </summary>
    internal static bool Matches(string query, string name)
    {
        var at = 0;
        foreach (var character in query)
        {
            at = name.IndexOf(character.ToString(), at, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return false;
            }

            at++;
        }

        return true;
    }

    /// <summary>The workspace folders the client opened, or its root folder, as full paths.</summary>
    private IEnumerable<string> WorkspaceFolders()
    {
        var settings = _server.ClientSettings;
        var folders = settings.WorkspaceFolders?.Select(folder => folder.Uri).ToList() ?? [];
        if (folders.Count == 0 && settings.RootUri is { } root)
        {
            folders.Add(root);
        }

        return folders.Where(uri => uri.Scheme == "file").Select(DocumentStore.FileNameOf);
    }

    protected override WorkspaceSymbolRegistrationOptions CreateRegistrationOptions(
        WorkspaceSymbolCapability capability,
        ClientCapabilities clientCapabilities) =>
        new();
}
