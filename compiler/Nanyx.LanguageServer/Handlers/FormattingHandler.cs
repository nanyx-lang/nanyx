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
/// <c>::format</c> directives, as for <c>nanyx format</c>; the request's <c>tabSize</c> and <c>insertSpaces</c> are
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
        if (_documents.TryGet(request.TextDocument.Uri) is not { } document
            || FormattedText(document, request.TextDocument.Uri, _logger) is not { } formatted
            || formatted == document.Text)
        {
            return new TextEditContainer();
        }

        // Positions count lines and characters, which are the same in the text and its normalised form.
        var whole = document.Positions.ToRange(0, document.Positions.NormalizedText.Length);
        return new TextEditContainer(new TextEdit { Range = whole, NewText = formatted });
    }

    /// <summary>
    /// <paramref name="document"/>'s text formatted, as <c>nanyx format</c> would write it, with its line endings; or
    /// null when it does not parse, its settings can't be read, or the safety check fails, which is logged. Formatting
    /// and the highlighting of what formatting would change both read it, so they never disagree.
    /// </summary>
    internal static string? FormattedText(OpenDocument document, DocumentUri uri, ILogger logger)
    {
        if (SettingsFor(document, uri, logger) is not { } settings)
        {
            return null;
        }

        switch (FormatCommand.formatText(settings, document.FileName, document.Text))
        {
            case FormatCommand.Formatting.Formatted formatted:
                return formatted.Item;
            case FormatCommand.Formatting.NotSafe notSafe:
                logger.LogWarning("Not formatting {Uri}: {Reason}", uri, notSafe.Item);
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The formatter's settings for <paramref name="document"/>, or null, logged, when they can't be read. They are read
    /// every time, rather than cached, so an edit to the manifest applies to the next format. A document not on disk
    /// has no manifest. A script's <c>::format</c> directives apply on top.
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
