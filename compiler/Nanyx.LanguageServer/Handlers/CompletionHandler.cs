using NyxCompiler;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Offers the names in scope at the cursor, with their types, and the language's keywords. Completion is asked for
/// while the user types, when the text rarely type-checks, so the names come from the document's last compile that
/// did, at the place there nearest the cursor (see <see cref="OpenDocument.NearestInLastGood"/>): a name declared
/// since is not offered until the text type-checks again. Nothing is offered in a comment or a string, after a dot
/// (a member), or where a <c>def</c> names something new.
/// </summary>
public sealed class CompletionHandler : CompletionHandlerBase
{
    /// <summary>The words the language reserves, offered wherever a name is.</summary>
    private static readonly string[] Keywords =
    [
        "as", "await", "context", "def", "defer", "else", "except", "export", "for", "if", "import", "in", "match",
        "module", "mut", "private", "rec", "return", "then", "try", "type", "typeof", "use", "while", "with", "yield"
    ];

    private readonly DocumentStore _documents;
    private readonly ServerSettings _settings;

    public CompletionHandler(DocumentStore documents, ServerSettings settings)
    {
        _documents = documents;
        _settings = settings;
    }

    public override Task<CompletionList> Handle(CompletionParams request, CancellationToken cancellationToken) =>
        Task.FromResult(Compute(request));

    // Every item is sent complete, so there is nothing left to resolve.
    public override Task<CompletionItem> Handle(CompletionItem request, CancellationToken cancellationToken) =>
        Task.FromResult(request);

    private CompletionList Compute(CompletionParams request)
    {
        var document = _documents.TryGet(request.TextDocument.Uri);
        if (document is null)
        {
            return new CompletionList();
        }

        var text = document.Positions.NormalizedText;
        var wordStart = CompletionContext.WordStart(text, document.Positions.ToOffset(request.Position));
        if (!CompletionContext.OffersNames(text, wordStart))
        {
            return new CompletionList();
        }

        var names = document.LastGood is { Compiled: { } compiled } lastGood
            && document.NearestInLastGood(wordStart) is { } offset
                ? Completion.typedNamesInScope(_settings.TypeVarNames, compiled, offset)
                    .Select(name => NameItem(name, lastGood.FileName))
                : [];
        var keywords = Keywords.Select(keyword => new CompletionItem
        {
            Label = keyword,
            Kind = CompletionItemKind.Keyword,
            SortText = $"3{keyword}"
        });
        return new CompletionList(names.Concat(keywords));
    }

    /// <summary>
    /// The item for a name. Names sort nearest first: locals, parameters and pattern variables, then the file's
    /// top-level defs and import aliases, then what imports and the prelude bring in.
    /// </summary>
    private static CompletionItem NameItem(Completion.NameInScope name, string fileName)
    {
        var symbol = name.Symbol;
        var group =
            symbol.Kind.IsLocalValue || symbol.Kind.IsParameter || symbol.Kind.IsPatternVariable ? 0
            : symbol.Declaration.File == fileName ? 1
            : 2;
        return new CompletionItem
        {
            Label = symbol.Name,
            Kind = symbol.Kind.IsImportedModule ? CompletionItemKind.Module
                : name.IsFunction ? CompletionItemKind.Function
                : CompletionItemKind.Variable,
            Detail = name.Type?.Value,
            SortText = $"{group}{symbol.Name}"
        };
    }

    protected override CompletionRegistrationOptions CreateRegistrationOptions(
        CompletionCapability capability,
        ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = TextDocumentSyncHandler.Selector, ResolveProvider = false };
}
