using NyxCompiler;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Shows the inferred type after the name of each local def, lambda parameter and pattern variable that does not
/// write its own, read from the document's last compile that type-checked, as <c>: int</c>; with
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
                .Select(hint => (hint, span: document.FromLastGood(hint.Span.Start, hint.Span.End)))
                .Where(mapped => mapped.span is not null)
                .Select(mapped => new InlayHint
                {
                    Position = document.Positions.ToPosition(mapped.span!.Value.End),
                    Label = new StringOrInlayHintLabelParts($": {mapped.hint.Type}"),
                    Kind = InlayHintKind.Type
                })
                .Where(hint => Contains(request.Range, hint.Position)));
    }

    private static bool Contains(Range range, Position position) =>
        range.Start <= position && position <= range.End;

    protected override InlayHintRegistrationOptions CreateRegistrationOptions(
        InlayHintClientCapabilities capability,
        ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = TextDocumentSyncHandler.Selector, ResolveProvider = false };
}
