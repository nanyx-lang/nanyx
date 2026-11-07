using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using CompilerHover = NyxCompiler.Hover;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Shows the inferred type of the expression under the pointer, read from the document's last compile that
/// type-checked. On a call's function name it shows the function's type instantiated at that call, and on a
/// declaration's own name (a def, a parameter, a pattern variable) the type of what it declares. Nothing is shown in
/// a top-level item edited since that compile, nor in a document that has never type-checked.
/// </summary>
public sealed class HoverHandler : HoverHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly ServerSettings _settings;

    public HoverHandler(DocumentStore documents, ServerSettings settings)
    {
        _documents = documents;
        _settings = settings;
    }

    public override Task<Hover?> Handle(HoverParams request, CancellationToken cancellationToken) =>
        Task.FromResult(Compute(request));

    private Hover? Compute(HoverParams request)
    {
        var document = _documents.TryGet(request.TextDocument.Uri);
        if (document?.LastGood?.Compiled is not { } compiled
            || document.ToLastGood(document.Positions.ToOffset(request.Position)) is not { } offset)
        {
            return null;
        }

        var info = CompilerHover.typeAt(_settings.TypeVarNames, compiled, offset);
        if (info is null || document.FromLastGood(info.Value.Span.Start, info.Value.Span.End) is not { } span)
        {
            return null;
        }

        return new Hover
        {
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = $"```nanyx\n{info.Value.Type}\n```"
            }),
            Range = document.Positions.ToRange(span.Start, span.End)
        };
    }

    protected override HoverRegistrationOptions CreateRegistrationOptions(
        HoverCapability capability,
        ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = TextDocumentSyncHandler.Selector };
}
