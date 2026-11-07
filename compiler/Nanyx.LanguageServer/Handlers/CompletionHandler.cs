using Microsoft.FSharp.Collections;
using NyxCompiler;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Offers the names in scope at the cursor, with their types, and the language's keywords; after a dot, the fields
/// of a record or the names under a prefix (an import's alias, a type's attached definitions). Completion is asked
/// for while the user types, when the text rarely type-checks, so these come from the document's last compile that
/// did, at the place there nearest the cursor (see <see cref="OpenDocument.NearestInLastGood"/>): a name declared
/// since is not offered until the text type-checks again. Nothing is offered in a comment or a string, or where a
/// <c>def</c> names something new.
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
        if (CompletionContext.MemberPath(text, wordStart) is { } path)
        {
            return Members(document, path, wordStart - 1 - string.Join('.', path).Length);
        }

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
    /// What can follow <paramref name="path"/> and a dot (see <see cref="Completion.membersAt"/>), where the path
    /// starts at <paramref name="pathStart"/>. A record whose fields were added since the last good compile offers
    /// the fields it had then.
    /// </summary>
    private CompletionList Members(OpenDocument document, IReadOnlyList<string> path, int pathStart)
    {
        if (document.LastGood?.Compiled is not { } compiled || document.NearestInLastGood(pathStart) is not { } offset)
        {
            return new CompletionList();
        }

        return new CompletionList(
            Completion.membersAt(_settings.TypeVarNames, compiled, offset, ListModule.OfSeq(path))
                .Select(member => new CompletionItem
                {
                    Label = member.Name,
                    Kind = member.Kind.IsField ? CompletionItemKind.Field
                        : member.Kind.IsPrefix ? CompletionItemKind.Module
                        : member.IsFunction ? CompletionItemKind.Function
                        : CompletionItemKind.Variable,
                    Detail = member.Type?.Value
                }));
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
        new()
        {
            DocumentSelector = TextDocumentSyncHandler.Selector,
            ResolveProvider = false,
            // Members are offered as soon as the dot is typed; names as soon as a name is started, by the editor.
            TriggerCharacters = new Container<string>(".")
        };
}
