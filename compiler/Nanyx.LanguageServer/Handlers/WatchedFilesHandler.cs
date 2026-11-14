using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Compiles again the open documents that import a Nanyx file changed on disk (<c>workspace/didChangeWatchedFiles</c>):
/// one changed outside the editor, by <c>git checkout</c>, say. The server asks the client to watch every <c>.nyx</c>
/// file. A file open in the editor is left to its buffer, which its importers read.
/// </summary>
public sealed class WatchedFilesHandler : DidChangeWatchedFilesHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly CompileScheduler _compiles;

    public WatchedFilesHandler(DocumentStore documents, CompileScheduler compiles)
    {
        _documents = documents;
        _compiles = compiles;
    }

    public override Task<Unit> Handle(DidChangeWatchedFilesParams request, CancellationToken cancellationToken)
    {
        foreach (var change in request.Changes)
        {
            if (change.Uri.Scheme == "file" && _documents.TryGet(change.Uri) is null)
            {
                _compiles.FileChanged(DocumentStore.FileNameOf(change.Uri));
            }
        }

        return Unit.Task;
    }

    protected override DidChangeWatchedFilesRegistrationOptions CreateRegistrationOptions(
        DidChangeWatchedFilesCapability capability,
        ClientCapabilities clientCapabilities) =>
        new()
        {
            Watchers = new Container<OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher>(
                new OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher
                {
                    GlobPattern = new GlobPattern("**/*.nyx"),
                    Kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete
                })
        };
}
