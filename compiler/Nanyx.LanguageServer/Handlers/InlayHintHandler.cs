using NyxCompiler;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Shows the inferred type after the name of each local def, lambda parameter and pattern variable that does not
/// write its own, read from the document's last compile that was typed, as <c>: int</c>; with
/// <see cref="TopLevelTypeHints.Inlay"/>, after each such top-level def too. Nothing is shown in a top-level item edited
/// since that compile.
/// </summary>
public sealed class InlayHintHandler : InlayHintsHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly ServerSettings _settings;

    public InlayHintHandler(DocumentStore documents, ServerSettings settings)
    {
        _documents = documents;
        _settings = settings;
    }

    public override Task<InlayHintContainer?> Handle(InlayHintParams request, CancellationToken cancellationToken) =>
        Task.FromResult(Compute(request));

    // Every hint is sent complete, so there is nothing left to resolve.
    public override Task<InlayHint> Handle(InlayHint request, CancellationToken cancellationToken) =>
        Task.FromResult(request);

    private InlayHintContainer? Compute(InlayHintParams request)
    {
        var document = _documents.TryGet(request.TextDocument.Uri);
        if (document?.LastGood?.Compiled is not { } compiled)
        {
            return null;
        }

        var hints = _settings.TopLevelTypeHints == TopLevelTypeHints.Inlay
            ? TypeHints.inlayHintsWithTopLevel(_settings.TypeVarNames, document.FileName, compiled)
            : TypeHints.inlayHints(_settings.TypeVarNames, document.FileName, compiled);
        return new InlayHintContainer(
            hints
                .Select(hint => ToInlayHint(document, hint))
                .OfType<InlayHint>()
                .Where(hint => Contains(request.Range, hint.Position)));
    }

    /// <summary>
    /// The inlay hint for <paramref name="hint"/>: <c>: type</c> after the name, or, where the declaration writes only
    /// its contexts (<c>def f: [Console] = ...</c>), the type after those, as the rest of the annotation.
    /// <c>null</c> if what it is shown after has been edited since the last good compile.
    /// </summary>
    private static InlayHint? ToInlayHint(OpenDocument document, TypeHints.TypeHint hint)
    {
        var context = hint.Context?.Value;
        var after = context ?? hint.Span;
        if (document.FromLastGood(after.Start, after.End) is not { } span)
        {
            return null;
        }

        return new InlayHint
        {
            Position = document.Positions.ToPosition(span.End),
            Label = new StringOrInlayHintLabelParts(context is null ? $": {hint.Type}" : hint.Type),
            PaddingLeft = context is not null,
            Kind = InlayHintKind.Type
        };
    }

    private static bool Contains(Range range, Position position) =>
        range.Start <= position && position <= range.End;

    protected override InlayHintRegistrationOptions CreateRegistrationOptions(
        InlayHintClientCapabilities capability,
        ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = TextDocumentSyncHandler.Selector, ResolveProvider = false };
}
