using Microsoft.FSharp.Collections;
using NyxCompiler;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using TextEdit = OmniSharp.Extensions.LanguageServer.Protocol.Models.TextEdit;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Offers the names in scope at the cursor, with their types, and the language's keywords; after a dot, the fields
/// of a record or the names under a prefix (an import's alias, a type's attached definitions); and in an import, the
/// standard library's modules and the files beside the document (see <see cref="ImportPaths"/>). Completion is asked
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
        var cursor = document.Positions.ToOffset(request.Position);
        if (CompletionContext.ImportPathAt(text, cursor) is { } import)
        {
            return ImportPaths(document, import, cursor);
        }

        var wordStart = CompletionContext.WordStart(text, cursor);
        if (CompletionContext.MemberPath(text, wordStart) is { } path)
        {
            return Members(document, path, wordStart - 1 - string.Join('.', path).Length);
        }

        if (CompletionContext.PipedPath(text, wordStart) is { } piped)
        {
            return PipeTargets(document, piped, wordStart);
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
    /// What can follow the import path written up to <paramref name="cursor"/>: the standard library's modules
    /// after <c>nanyx/</c>, a level at a time (<c>nanyx/text.</c> offers the modules under <c>text</c>), and in quotes
    /// the <c>.nyx</c> files and folders beside the document, or in the folder the path has reached. An untitled
    /// document has no folder, so it is offered no files. Each item replaces only the part of the path after its last
    /// <c>/</c> (or <c>.</c>, in a module's path), and a folder or a level of modules asks for the next part.
    /// </summary>
    private static CompletionList ImportPaths(OpenDocument document, ImportPath import, int cursor)
    {
        var written = import.Written;
        var separator = import.IsFile ? written.LastIndexOf('/') : written.LastIndexOfAny(['/', '.']);
        var range = document.Positions.ToRange(cursor - (written.Length - separator - 1), cursor);
        CompletionItem Item(string label, CompletionItemKind kind, string insert, string? documentation = null) => new()
        {
            Label = label,
            Kind = kind,
            TextEdit = new TextEditOrInsertReplaceEdit(new TextEdit { Range = range, NewText = insert }),
            Documentation = documentation is null ? null : new StringOrMarkupContent(documentation),
            // A folder or a level of modules is only part of a path: the editor asks again for what follows it.
            Command = insert == label ? null : new Command { Name = "editor.action.triggerSuggest", Title = "Suggest" }
        };

        if (import.IsFile)
        {
            if (document.FileName.Length == 0 || Path.GetDirectoryName(document.FileName) is not { } documentFolder)
            {
                return new CompletionList();
            }

            var folder = Path.GetFullPath(Path.Combine(documentFolder, written[..(separator + 1)]));
            if (!Directory.Exists(folder))
            {
                return new CompletionList();
            }

            var folders = Directory.GetDirectories(folder)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => !name.StartsWith('.'))
                .Select(name => Item(name, CompletionItemKind.Folder, $"{name}/"));
            var files = Directory.GetFiles(folder, "*.nyx")
                .Where(file => !string.Equals(Path.GetFullPath(file), document.FileName, StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OfType<string>()
                .Select(name => Item(name, CompletionItemKind.File, name));
            return new CompletionList(folders.Concat(files));
        }

        var prefix = StandardLibrary.importPrefix;
        if (!written.StartsWith(prefix, StringComparison.Ordinal))
        {
            // Only the standard library's modules are imported by name, so far.
            return written.Contains('/')
                ? new CompletionList()
                : new CompletionList(Item(StandardLibrary.packageName, CompletionItemKind.Module, prefix));
        }

        var level = written[prefix.Length..(separator + 1)];
        return new CompletionList(
            StandardLibrary.moduleNames
                .Where(name => name.StartsWith(level, StringComparison.Ordinal))
                .Select(name => name[level.Length..])
                .GroupBy(rest => rest.Split('.')[0])
                .Select(group => group.Contains(group.Key)
                    ? Item(group.Key, CompletionItemKind.Module, group.Key, StandardLibrary.trySummary(level + group.Key)?.Value)
                    : Item(group.Key, CompletionItemKind.Module, $"{group.Key}.")));
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
                        : member.Kind.IsProperty ? CompletionItemKind.Property
                        : member.Kind.IsPrefix ? CompletionItemKind.Module
                        : member.IsFunction ? CompletionItemKind.Function
                        : CompletionItemKind.Variable,
                    Detail = member.Type?.Value
                }));
    }

    /// <summary>
    /// What a pipe can call (see <see cref="Completion.pipeTargetsAt"/>): the definitions attached to the type of
    /// the value <paramref name="piped"/> names, then the functions in scope.
    /// </summary>
    private CompletionList PipeTargets(OpenDocument document, IReadOnlyList<string> piped, int wordStart)
    {
        if (document.LastGood?.Compiled is not { } compiled || document.NearestInLastGood(wordStart) is not { } offset)
        {
            return new CompletionList();
        }

        return new CompletionList(
            Completion.pipeTargetsAt(_settings.TypeVarNames, compiled, offset, ListModule.OfSeq(piped))
                .Select(target => new CompletionItem
                {
                    Label = target.Name,
                    Kind = target.IsFunction ? CompletionItemKind.Function : CompletionItemKind.Variable,
                    Detail = target.Type?.Value,
                    SortText = $"{(target.IsAttached ? 0 : 1)}{target.Name}"
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
            // Members are offered as soon as the dot is typed, what a pipe can call as soon as its `\` is, and the
            // next part of an import path as soon as its `/` or opening quote is; names as soon as a name is started,
            // by the editor.
            TriggerCharacters = new Container<string>(".", "\\", "/", "\"")
        };
}
