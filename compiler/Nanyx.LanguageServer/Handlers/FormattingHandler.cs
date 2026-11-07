using Microsoft.Extensions.Logging;
using Microsoft.FSharp.Core;
using Nanyx.Formatter;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Formats a document ("Format Document", and format-on-save) with the same formatter and safety check as
/// <c>nanyx format</c>: one edit replacing the whole document, or none when it is already formatted, does not parse,
/// or could not be formatted safely. The line width and indent come from the package's <c>.nyxpack</c>, as for
/// <c>nanyx format</c>; the request's <c>tabSize</c> and <c>insertSpaces</c> are ignored.
/// </summary>
public sealed class FormattingHandler : DocumentFormattingHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly ILogger<FormattingHandler> _logger;

    public FormattingHandler(DocumentStore documents, ILogger<FormattingHandler> logger)
    {
        _documents = documents;
        _logger = logger;
    }

    public override Task<TextEditContainer?> Handle(DocumentFormattingParams request, CancellationToken cancellationToken) =>
        Task.FromResult<TextEditContainer?>(Compute(request));

    private TextEditContainer Compute(DocumentFormattingParams request)
    {
        if (_documents.TryGet(request.TextDocument.Uri) is not { } document)
        {
            return new TextEditContainer();
        }

        // Read every time, rather than cached, so an edit to the manifest applies to the next format. A document not on
        // disk has no manifest.
        var settings = document.FileName == ""
            ? FSharpResult<Formatter.Formatter.Options, string>.NewOk(Formatter.Formatter.OptionsModule.defaults)
            : Settings.forFile(document.FileName);
        if (settings.IsError)
        {
            _logger.LogWarning("Not formatting {Uri}: {Reason}", request.TextDocument.Uri, settings.ErrorValue);
            return new TextEditContainer();
        }

        switch (FormatCommand.formatText(settings.ResultValue, document.FileName, document.Text))
        {
            case FormatCommand.Formatting.Formatted formatted when formatted.Item != document.Text:
                // Positions count lines and characters, which are the same in the text and its normalised form.
                var whole = document.Positions.ToRange(0, document.Positions.NormalizedText.Length);
                return new TextEditContainer(new TextEdit { Range = whole, NewText = formatted.Item });
            case FormatCommand.Formatting.NotSafe notSafe:
                _logger.LogWarning("Not formatting {Uri}: {Reason}", request.TextDocument.Uri, notSafe.Item);
                return new TextEditContainer();
            default:
                return new TextEditContainer();
        }
    }

    protected override DocumentFormattingRegistrationOptions CreateRegistrationOptions(
        DocumentFormattingCapability capability,
        ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = TextDocumentSyncHandler.Selector };
}
