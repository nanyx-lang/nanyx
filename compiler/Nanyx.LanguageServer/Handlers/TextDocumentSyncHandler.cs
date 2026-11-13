using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Keeps <see cref="DocumentStore"/> in step with the editor (full-text sync: the client sends the whole
/// document on every change). A document is compiled, and its diagnostics published, when it is opened, and after a
/// change once the user pauses (see <see cref="CompileScheduler"/>).
/// </summary>
public sealed class TextDocumentSyncHandler : TextDocumentSyncHandlerBase
{
    /// <summary>The documents this server handles: Nanyx source, whatever scheme the editor uses for it.</summary>
    public static readonly TextDocumentSelector Selector = TextDocumentSelector.ForLanguage("nanyx");

    private readonly DocumentStore _documents;
    private readonly DiagnosticsPublisher _diagnostics;
    private readonly CompileScheduler _compiles;

    public TextDocumentSyncHandler(DocumentStore documents, DiagnosticsPublisher diagnostics, CompileScheduler compiles)
    {
        _documents = documents;
        _diagnostics = diagnostics;
        _compiles = compiles;
    }

    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri) => new(uri, "nanyx");

    public override Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
    {
        _compiles.CompileNow(request.TextDocument.Uri, request.TextDocument.Text, request.TextDocument.Version);
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
    {
        // With TextDocumentSyncKind.Full there is exactly one change, carrying the whole document.
        var text = request.ContentChanges.LastOrDefault()?.Text;
        if (text is not null)
        {
            _compiles.Changed(request.TextDocument.Uri, text, request.TextDocument.Version);
        }

        return Unit.Task;
    }

    public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken)
    {
        // Diagnostics already follow every change once the user pauses; saving adds nothing.
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
    {
        _compiles.Closed(request.TextDocument.Uri);
        _documents.Remove(request.TextDocument.Uri);
        _diagnostics.Closed(request.TextDocument.Uri);
        return Unit.Task;
    }

    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(
        TextSynchronizationCapability capability,
        ClientCapabilities clientCapabilities) =>
        new()
        {
            DocumentSelector = Selector,
            Change = TextDocumentSyncKind.Full,
            Save = new SaveOptions { IncludeText = false }
        };
}
