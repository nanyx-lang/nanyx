using Microsoft.Extensions.Logging;
using Microsoft.FSharp.Core;
using Nanyx.Formatter;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Formats a document ("Format Document", and format-on-save) with the same formatter and safety check as
/// <c>nanyx format</c>: one edit replacing the whole document, or none when it is already formatted, does not parse,
/// or could not be formatted safely. The line width and indent come from the package's <c>.nyxp</c>, or a script's
/// <c>::formatter</c> directives, as for <c>nanyx format</c>; the request's <c>tabSize</c> and <c>insertSpaces</c> are
/// ignored.
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

        if (SettingsFor(document, request.TextDocument.Uri, _logger) is not { } settings)
        {
            return new TextEditContainer();
        }

        switch (FormatCommand.formatText(settings, document.FileName, document.Text))
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

    /// <summary>
    /// The formatter's settings for <paramref name="document"/>, or null, logged, when they can't be read. They are read
    /// every time, rather than cached, so an edit to the manifest applies to the next format. A document not on disk
    /// has no manifest. A script's <c>::formatter</c> directives apply on top.
    /// </summary>
    internal static Formatter.Formatter.Options? SettingsFor(OpenDocument document, DocumentUri uri, ILogger logger)
    {
        var settings = document.FileName == ""
            ? FSharpResult<Formatter.Formatter.Options, string>.NewOk(Formatter.Formatter.OptionsModule.defaults)
            : Settings.forFile(document.FileName);
        if (settings.IsOk)
        {
            settings = Settings.withDirectives(document.FileName, document.Text, settings.ResultValue);
        }
        if (settings.IsError)
        {
            logger.LogWarning("Not formatting {Uri}: {Reason}", uri, settings.ErrorValue);
            return null;
        }

        return settings.ResultValue;
    }

    protected override DocumentFormattingRegistrationOptions CreateRegistrationOptions(
        DocumentFormattingCapability capability,
        ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = TextDocumentSyncHandler.Selector };
}
