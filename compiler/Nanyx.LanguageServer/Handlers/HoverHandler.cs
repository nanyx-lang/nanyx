using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using CompilerHover = NyxCompiler.Hover;
using Definition = NyxCompiler.Definition;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Shows the inferred type of the expression under the pointer, read from the document's last compile that was
/// typed: this one even with type or name errors, where what failed has the type <c>???</c>. On a call's function name it shows the function's type instantiated at that call, and on a
/// declaration's own name (a def, a parameter, a pattern variable) the type of what it declares. On a type's name it
/// shows the type's definition instead, even inside an expression. Nothing is shown in a top-level item edited since
/// that compile, nor in a document that has never been typed (see <see cref="OpenDocument.LastGood"/>).
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
        if (document?.LastGood is not { Compiled: { } compiled } lastGood
            || document.ToLastGood(document.Positions.ToOffset(request.Position)) is not { } offset)
        {
            return null;
        }

        // A type's name, in an annotation inside a block say, is about the type, not the block around it.
        var info = Definition.typeDefinitionAt(_settings.TypeNotation, lastGood.FileName, compiled, offset)
            ?? CompilerHover.typeAt(_settings.TypeNotation, compiled, offset);
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
