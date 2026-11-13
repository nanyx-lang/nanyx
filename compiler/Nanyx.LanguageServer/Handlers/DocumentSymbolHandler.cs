using NyxCompiler;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using SymbolKind = OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// The outline of a document: its top-level defs, types, contexts and module blocks, each with the local defs,
/// fields or members declared inside it (see <c>NyxCompiler.Outline</c>). A text that does not parse shows the
/// outline of the last text that was typed, less the top-level items edited since.
/// </summary>
public sealed class DocumentSymbolHandler : DocumentSymbolHandlerBase
{
    private readonly DocumentStore _documents;

    public DocumentSymbolHandler(DocumentStore documents)
    {
        _documents = documents;
    }

    public override Task<SymbolInformationOrDocumentSymbolContainer?> Handle(
        DocumentSymbolParams request,
        CancellationToken cancellationToken) =>
        Task.FromResult(Compute(request));

    private SymbolInformationOrDocumentSymbolContainer? Compute(DocumentSymbolParams request)
    {
        if (_documents.TryGet(request.TextDocument.Uri) is not { } document)
        {
            return null;
        }

        IEnumerable<DocumentSymbol> symbols;
        if (document.Outline is { } outline)
        {
            symbols = outline.Select(item => ToSymbol(item, (start, end) => (start, end), document)).OfType<DocumentSymbol>();
        }
        else if (document.LastGood is { Outline: { } lastGoodOutline })
        {
            symbols = lastGoodOutline.Select(item => ToSymbol(item, document.FromLastGood, document)).OfType<DocumentSymbol>();
        }
        else
        {
            return null;
        }

        return new SymbolInformationOrDocumentSymbolContainer(
            symbols.Select(symbol => new SymbolInformationOrDocumentSymbol(symbol)));
    }

    /// <summary>
    /// The symbol for <paramref name="item"/>, with its spans moved into the document's text by
    /// <paramref name="map"/>; <c>null</c> if they cannot be, because the item has been edited since.
    /// </summary>
    private static DocumentSymbol? ToSymbol(
        Outline.OutlineItem item,
        Func<int, int, (int Start, int End)?> map,
        OpenDocument document)
    {
        if (map(item.Span.Start, item.Span.End) is not { } span || map(item.NameSpan.Start, item.NameSpan.End) is not { } name)
        {
            return null;
        }

        return new DocumentSymbol
        {
            Name = item.Name,
            Kind = KindOf(item.Kind),
            Detail = item.Kind.IsScopedContextType ? "scoped" : null,
            Range = document.Positions.ToRange(span.Start, span.End),
            SelectionRange = document.Positions.ToRange(name.Start, name.End),
            Children = new Container<DocumentSymbol>(item.Children.Select(child => ToSymbol(child, map, document)).OfType<DocumentSymbol>())
        };
    }

    private static SymbolKind KindOf(Outline.OutlineKind kind) =>
        kind.Tag switch
        {
            Outline.OutlineKind.Tags.Function => SymbolKind.Function,
            Outline.OutlineKind.Tags.Value => SymbolKind.Variable,
            Outline.OutlineKind.Tags.RecordType => SymbolKind.Struct,
            Outline.OutlineKind.Tags.UnionType => SymbolKind.Enum,
            Outline.OutlineKind.Tags.ContextType or Outline.OutlineKind.Tags.ScopedContextType => SymbolKind.Interface,
            Outline.OutlineKind.Tags.RecordField => SymbolKind.Field,
            Outline.OutlineKind.Tags.ContextFunction => SymbolKind.Method,
            Outline.OutlineKind.Tags.ContextValue => SymbolKind.Property,
            Outline.OutlineKind.Tags.ModuleBlock => SymbolKind.Module,
            _ => SymbolKind.Class
        };

    protected override DocumentSymbolRegistrationOptions CreateRegistrationOptions(
        DocumentSymbolCapability capability,
        ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = TextDocumentSyncHandler.Selector, Label = "Nanyx" };
}
