using NyxCompiler.Refactorings;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Renames a name and every use of it in the document (see <c>NyxCompiler.Refactorings.Rename</c>), in a document
/// whose text type-checked. The renamed text is compiled before the edit is given, and a rename that would make any
/// name refer to something else is refused. A refusal is an error response with its reason, which the editor shows.
/// </summary>
public sealed class RenameHandler : RenameHandlerBase
{
    private readonly DocumentStore _documents;

    public RenameHandler(DocumentStore documents)
    {
        _documents = documents;
    }

    public override Task<WorkspaceEdit?> Handle(RenameParams request, CancellationToken cancellationToken) =>
        Task.FromResult(Compute(request));

    private WorkspaceEdit? Compute(RenameParams request)
    {
        var uri = request.TextDocument.Uri;
        if (_documents.TryGet(uri) is not { Typed: true, Compiled: { } compiled } document)
        {
            return null;
        }

        var result = Rename.edits(
            document.FileName,
            document.Text,
            compiled,
            document.Positions.ToOffset(request.Position),
            request.NewName,
            Microsoft.FSharp.Core.FuncConvert.FromFunc((string text) => _documents.Compile(uri, text).Compiled ?? throw Refused("The compiler failed on the renamed text")));
        if (result.IsError)
        {
            throw Refused(result.ErrorValue);
        }

        var edits = result.ResultValue.Select(edit => new TextEdit
        {
            Range = document.Positions.ToRange(edit.Span.Start, edit.Span.End),
            NewText = edit.NewText
        });
        return new WorkspaceEdit { Changes = new Dictionary<OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri, IEnumerable<TextEdit>> { [uri] = edits.ToList() } };
    }

    /// <summary>The error response for a refused rename: the request failed (<c>RequestFailed</c>, -32803), for the reason given.</summary>
    internal static Exception Refused(string reason) => new RpcErrorException(-32803, null, reason);

    protected override RenameRegistrationOptions CreateRegistrationOptions(
        RenameCapability capability,
        ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = TextDocumentSyncHandler.Selector, PrepareProvider = true };
}

/// <summary>
/// Says what <c>textDocument/rename</c> would rename at a position: the name's own part, or, for a name that can't be
/// renamed, an error response with the reason.
/// </summary>
public sealed class PrepareRenameHandler : PrepareRenameHandlerBase
{
    private readonly DocumentStore _documents;

    public PrepareRenameHandler(DocumentStore documents)
    {
        _documents = documents;
    }

    public override Task<RangeOrPlaceholderRange?> Handle(PrepareRenameParams request, CancellationToken cancellationToken) =>
        Task.FromResult(Compute(request));

    private RangeOrPlaceholderRange? Compute(PrepareRenameParams request)
    {
        if (_documents.TryGet(request.TextDocument.Uri) is not { Typed: true, Compiled: { } compiled } document
            || Rename.prepare(document.FileName, document.Text, compiled, document.Positions.ToOffset(request.Position))?.Value is not { } prepared)
        {
            return null;
        }

        if (prepared.IsError)
        {
            throw RenameHandler.Refused(prepared.ErrorValue);
        }

        var target = prepared.ResultValue;
        return new RangeOrPlaceholderRange(new PlaceholderRange
        {
            Range = document.Positions.ToRange(target.Span.Start, target.Span.End),
            Placeholder = target.Name
        });
    }

    protected override RenameRegistrationOptions CreateRegistrationOptions(
        RenameCapability capability,
        ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = TextDocumentSyncHandler.Selector, PrepareProvider = true };
}
