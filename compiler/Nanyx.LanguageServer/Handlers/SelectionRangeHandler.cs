using NyxCompiler.Refactorings;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Expand and shrink selection: for each position, the syntax around it, from the smallest node out to the whole
/// document (see <c>NyxCompiler.Refactorings.SelectionRanges</c>). It needs only a parse of the editor's text; a text
/// that does not parse has no ranges, and the editor falls back on its own.
/// </summary>
public sealed class SelectionRangeHandler : SelectionRangeHandlerBase
{
    private readonly DocumentStore _documents;

    public SelectionRangeHandler(DocumentStore documents)
    {
        _documents = documents;
    }

    public override Task<Container<SelectionRange>?> Handle(SelectionRangeParams request, CancellationToken cancellationToken) =>
        Task.FromResult(Compute(request));

    private Container<SelectionRange>? Compute(SelectionRangeParams request)
    {
        if (_documents.TryGet(request.TextDocument.Uri) is not { } document)
        {
            return null;
        }

        var ranges = new List<SelectionRange>();
        foreach (var position in request.Positions)
        {
            var chain = SelectionRanges.at(document.FileName, document.Text, document.Positions.ToOffset(position));
            if (chain is null)
            {
                return null;
            }

            // The protocol links each range to the one around it, so the chain is built from the outside in.
            SelectionRange? range = null;
            foreach (var span in chain.Value.Reverse())
            {
                range = new SelectionRange { Range = document.Positions.ToRange(span.Start, span.End), Parent = range! };
            }

            ranges.Add(range!);
        }

        return new Container<SelectionRange>(ranges);
    }

    protected override SelectionRangeRegistrationOptions CreateRegistrationOptions(
        SelectionRangeCapability capability,
        ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = TextDocumentSyncHandler.Selector };
}
