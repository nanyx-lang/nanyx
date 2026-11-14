using Microsoft.Extensions.Logging;
using Nanyx.Formatter;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Formats the top-level items a range touches ("Format Selection"), with the settings and safety check of
/// <see cref="FormattingHandler"/>: the whole document is formatted, and only those items' lines are replaced (see
/// <see cref="RangeFormatting.formatRange"/>). No edit when they are formatted already, or the document does not parse
/// or could not be formatted safely.
/// </summary>
public sealed class RangeFormattingHandler : DocumentRangeFormattingHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly ILogger<RangeFormattingHandler> _logger;

    public RangeFormattingHandler(DocumentStore documents, ILogger<RangeFormattingHandler> logger)
    {
        _documents = documents;
        _logger = logger;
    }

    public override Task<TextEditContainer?> Handle(DocumentRangeFormattingParams request, CancellationToken cancellationToken) =>
        Task.FromResult<TextEditContainer?>(Compute(request));

    private TextEditContainer Compute(DocumentRangeFormattingParams request)
    {
        if (_documents.TryGet(request.TextDocument.Uri) is not { } document
            || FormattingHandler.SettingsFor(document, request.TextDocument.Uri, _logger) is not { } settings)
        {
            return new TextEditContainer();
        }

        var positions = document.Positions;
        var result = RangeFormatting.formatRange(
            settings,
            document.FileName,
            document.Text,
            positions.ToOffset(request.Range.Start),
            positions.ToOffset(request.Range.End));
        if (result.IsError)
        {
            if (result.ErrorValue is FormatCommand.Formatting.NotSafe notSafe)
            {
                _logger.LogWarning("Not formatting {Uri}: {Reason}", request.TextDocument.Uri, notSafe.Item);
            }
            return new TextEditContainer();
        }

        // The edits' line breaks are the document's own, as whole-document formatting keeps them.
        var lineBreak = document.Text.IndexOf('\n') is > 0 and var at && document.Text[at - 1] == '\r' ? "\r\n" : "\n";
        return new TextEditContainer(result.ResultValue.Select(edit => new TextEdit
        {
            Range = positions.ToRange(edit.Start, edit.End),
            NewText = edit.Text.Replace("\n", lineBreak)
        }));
    }

    protected override DocumentRangeFormattingRegistrationOptions CreateRegistrationOptions(
        DocumentRangeFormattingCapability capability,
        ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = TextDocumentSyncHandler.Selector };
}
