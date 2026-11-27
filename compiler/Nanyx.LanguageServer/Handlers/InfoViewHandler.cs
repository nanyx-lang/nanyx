using MediatR;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using InfoView = NyxCompiler.InfoView;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using SymbolKind = NyxCompiler.SymbolKind;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// The parameters of <c>nanyx/infoView</c>: the document and the position to describe; the selection, if the client
/// sends one, whose expression it describes at the top, or with none or an empty one the expression at the position;
/// and the width, in characters, the client shows an item's text in, if it says: a signature that doesn't fit it is
/// laid out as the formatter lays out a def's (see <see cref="Nanyx.Formatter.SignatureLayout.layOut"/>).
/// </summary>
[Parallel, Method(InfoViewHandler.Method, Direction.ClientToServer)]
public sealed record InfoViewParams : IRequest<InfoViewResult?>
{
    public required TextDocumentIdentifier TextDocument { get; init; }
    public required Position Position { get; init; }
    public Range? Selection { get; init; }
    public int? Width { get; init; }
}

/// <summary>
/// What <c>nanyx/infoView</c> returns: the scopes at the position, innermost first (see <see cref="InfoViewScope"/>),
/// and the ranges of the document for the editor to highlight there (see <see cref="InfoViewHighlight"/>).
/// </summary>
public sealed record InfoViewResult
{
    public required IReadOnlyList<InfoViewScope> Scopes { get; init; }
    public IReadOnlyList<InfoViewHighlight> Highlights { get; init; } = [];
}

/// <summary>
/// A scope at the position. <see cref="Kind"/> is one of <c>messages</c> (first, only when the position's line has a
/// diagnostic, with its <see cref="Messages"/> and no sections), <c>expression</c> (next, with the section
/// <c>type</c>, only when there is an expression there that no scope below shows), or in its place <c>type</c> (a
/// type there: its <c>definition</c>, a selected type's <c>type</c>, or the <c>contexts</c> of a type variable),
/// <c>lambda</c> (<c>type</c>, <c>names</c>, <c>uses</c>), <c>function</c> (<c>signature</c>, <c>names</c>,
/// <c>contexts</c>, <c>flow</c>), <c>typeDefinition</c> (<c>definition</c>, <c>parameters</c>, <c>attached</c>) and
/// <c>module</c> (always last, with <c>names</c> and <c>contexts</c>); a lambda, function or type definition scope comes
/// once for each lambda, def or <c>type</c>, <c>context</c>, <c>dimension</c> or <c>unit</c> declaration around the
/// position, and leaves out the sections it has nothing for. <see cref="Heading"/> names the scope, as code but for the
/// messages', after <see cref="Label"/>, which says what kind of code it is where it doesn't, and
/// <see cref="Location"/> is where it links (see <c>InfoView.Heading</c>): the expression or selected type by its
/// text, a lambda by its parameters and line, a <c>for</c> loop's as the loop is written, with its line, a def by its
/// name, a type as declared, the module by its declared name. <see cref="Documentation"/> is the module's doc comment.
/// <see cref="Collapsed"/> says whether a client shows it closed at first: the module, and the scopes after the third,
/// not counting the messages and the expression.
/// </summary>
public sealed record InfoViewScope
{
    public required string Kind { get; init; }
    public string? Label { get; init; }
    public required string Heading { get; init; }
    public Location? Location { get; init; }
    public string? Documentation { get; init; }
    public bool Collapsed { get; init; }
    public required IReadOnlyList<InfoViewSection> Sections { get; init; }
    public IReadOnlyList<InfoViewMessage>? Messages { get; init; }
}

/// <summary>
/// A diagnostic on the position's line, in full, as <c>nanyx run</c> prints it: its <see cref="Severity"/>
/// (<c>error</c>, <c>warning</c>, <c>info</c>, <c>hint</c>) and <see cref="Code"/>, which links to
/// <see cref="Reference"/>, its section of the errors reference, if it has one (a linter's code has none); its
/// <see cref="Headline"/> (<c>Type mismatch</c>), at <see cref="Location"/>; its <see cref="Notes"/>, the body and then
/// each suggestion after <c>help:</c> (<c>'float' vs 'int'</c>); the other places that explain it, each linking there;
/// and its fixes.
/// </summary>
public sealed record InfoViewMessage
{
    public required string Severity { get; init; }
    public string? Code { get; init; }
    public Uri? Reference { get; init; }
    public required string Headline { get; init; }
    public required Location Location { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    public IReadOnlyList<InfoViewItem> Related { get; init; } = [];
    public IReadOnlyList<InfoViewFix> Fixes { get; init; } = [];
}

/// <summary>A fix of a message: its title, and the edit it makes, the same edit as its quick fix makes.</summary>
public sealed record InfoViewFix
{
    public required string Title { get; init; }
    public required WorkspaceEdit Edit { get; init; }
}

/// <summary>
/// A range of the document for the editor to highlight: a <c>provider</c> of contexts (a <c>use</c>, or a context of
/// a signature) or a <c>call</c> it serves. On a provider, the calls it serves are highlighted; on a call, what
/// provides its contexts (see <see cref="NyxCompiler.ContextFlow.at"/>).
/// </summary>
public sealed record InfoViewHighlight
{
    public required Range Range { get; init; }
    public required string Kind { get; init; }
}

/// <summary>A section of a scope in the info view: <see cref="Id"/> is one of those <see cref="InfoViewScope"/> lists.</summary>
public sealed record InfoViewSection
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<InfoViewItem> Items { get; init; }
}

/// <summary>
/// An item of a section: its text (<c>name: type</c>, a context, or, among a message's other places, what one says),
/// what kind of thing it is or where it comes from (<c>parameter</c>, <c>use</c>, <c>line 4</c>), where it is, for a
/// link, and the doc comment of what it names, as Markdown with its links written out, as hover shows it: for an
/// import, that of the module it brings in. <see cref="Name"/> is the part of the text that links to
/// <see cref="Location"/>: the name it starts with (a def's, a local's, a parameter's, a context's, a callee's); an
/// item without one, such as a type, has no link in its text. <see cref="Links"/> are the other parts of it that link: the names of types, each to its
/// definition (see <see cref="InfoViewTypeLinks"/>). <see cref="Children"/> are the items under it: the calls a provider in the flow serves.
/// </summary>
public sealed record InfoViewItem
{
    public required string Text { get; init; }
    public InfoViewName? Name { get; init; }
    public IReadOnlyList<InfoViewLink>? Links { get; init; }
    public string? Detail { get; init; }
    public Location? Location { get; init; }
    public string? Documentation { get; init; }
    public IReadOnlyList<InfoViewItem>? Children { get; init; }
}

/// <summary>The part of an item's text that names something: where it starts, and how long it is.</summary>
public sealed record InfoViewName(int Start, int Length)
{
    /// <summary>The name <paramref name="name"/> where <paramref name="text"/> starts with it, if it does.</summary>
    public static InfoViewName? AtStart(string text, string name) =>
        name.Length > 0 && text.StartsWith(name, StringComparison.Ordinal)
        && (text.Length == name.Length || !IsNamePart(text[name.Length]))
            ? new InfoViewName(0, name.Length)
            : null;

    private static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';
}

/// <summary>
/// <c>nanyx/infoView</c>: what the compiler knows at a position, for the info view panel, by scope, innermost first.
/// First, if the position's line has any, its diagnostics, each in full (see <see cref="InfoViewMessage"/>). Then the
/// expression selected, or with no selection the one at the position, with its type, as hover writes it (see
/// <see cref="InfoView.expressionAt"/>), or in its place the type there (see <see cref="InfoView.typeAt"/>). Then each
/// lambda and def around the position (see <see cref="InfoView.Scope"/>): a lambda's type, with the contexts it
/// carries, a def's signature and doc comment, the parameters, locals and pattern variables each declares, the
/// contexts each provides, and in the innermost def the flow of contexts through it, each provider with the calls it
/// serves, an instance the compiler builds among them (see <see cref="NyxCompiler.ContextFlow"/>). Then the type
/// declaration around the position, if any: its definition, its parameters and the definitions attached to it. Last the module: its
/// doc comment, its top-level defs and imports in scope, and the runtime's contexts. It also gives the ranges to
/// highlight: a provider at the position and the calls it serves, or a call and what provides its contexts. Read from
/// the document's last typed compile, as hover is (see <see cref="OpenDocument.LastGood"/>); the diagnostics are this
/// text's own. Each item links to what it describes: a name to its declaration, a context to what provides it. Each
/// item whose name or context has a doc comment carries it, read as hover reads it (see
/// <see cref="NyxCompiler.Documentation.Reader"/>): a context's is that of the def a <c>use</c> names, if it has
/// one, or else the context's own. An item that repeats one a scope further out shows is left out (see
/// <see cref="WithoutRepeats"/>).
/// </summary>
public sealed class InfoViewHandler : IJsonRpcRequestHandler<InfoViewParams, InfoViewResult?>
{
    public const string Method = "nanyx/infoView";

    private readonly DocumentStore _documents;
    private readonly ServerSettings _settings;
    private readonly StandardLibrarySource _standardLibrary;

    public InfoViewHandler(DocumentStore documents, ServerSettings settings, StandardLibrarySource standardLibrary)
    {
        _documents = documents;
        _settings = settings;
        _standardLibrary = standardLibrary;
    }

    public Task<InfoViewResult?> Handle(InfoViewParams request, CancellationToken cancellationToken) =>
        Task.FromResult(Compute(request));

    private InfoViewResult? Compute(InfoViewParams request)
    {
        var uri = request.TextDocument.Uri;
        if (_documents.TryGet(uri) is not { } document)
        {
            return null;
        }

        var offset = document.Positions.ToOffset(request.Position);
        InfoView.Info? info = null;
        InfoView.Described? expression = null;
        InfoView.DescribedType? typeAt = null;
        NyxCompiler.ContextFlow.Flow? flow = null;
        NyxCompiler.ContextFlow.Highlights? highlights = null;
        NyxCompiler.Documentation.Reader? docs = null;
        string? fileName = null;
        if (document.LastGood is { Compiled: { } compiled } lastGood && document.NearestInLastGood(offset) is { } nearest)
        {
            fileName = lastGood.FileName;
            info = InfoView.at(_settings.TypeNotation, lastGood.FileName, lastGood.Positions.NormalizedText, compiled, nearest);
            // A type there is described in place of the expression around it, as a lambda is around its parameter's type.
            if (SelectedInLastGood(request, document, offset) is { } selected)
            {
                typeAt = InfoView.typeAt(_settings.TypeNotation, lastGood.FileName, lastGood.Positions.NormalizedText, compiled, selected)?.Value;
                expression = typeAt is null
                    ? InfoView.expressionAt(_settings.TypeNotation, lastGood.Positions.NormalizedText, compiled, selected)?.Value
                    : null;
            }

            var contexts = NyxCompiler.ContextFlow.at(_settings.TypeNotation, compiled, nearest);
            flow = contexts.Flow?.Value;
            highlights = contexts.Highlights;
            docs = lastGood.DocReader(uri, nearest, _standardLibrary);
        }

        Location? Locate(Parser.Program.SourceSpan span) => LocationOf(uri, document, span);
        string Signature(string name, string type) => LaidOut(request.Width, name, type);
        string? DocOf(NyxCompiler.Symbol symbol) => docs?.ForSymbol(symbol)?.Value;

        // A custom pattern's def is written as the pattern was declared, whose keyword links to the declaration (see
        // `Signatures.patternDeclaration`).
        InfoViewItem AsDeclared(string name, string? type, InfoViewItem item, string? detail) =>
            type is not null && NyxCompiler.Signatures.patternDeclaration(name, type)?.Value is { } pattern
                ? item with { Text = pattern, Name = new InfoViewName(0, Parser.Program.PatternSyntax.keyword.Length), Detail = detail }
                : item;

        // A type's definition writes its name, `type Bag.elem = int`.
        InfoViewItem Name(InfoView.ScopedName name) => AsDeclared(name.Name, name.Type?.Value, new()
        {
            Text = name.Type?.Value is not { } type ? name.Name
                : name.Kind.IsTypeDefinition ? type
                : Signature(name.Name, type),
            Name = new InfoViewName(0, name.Name.Length),
            Detail = Describe(name.Kind),
            Location = Locate(name.Declaration),
            Documentation = DocOf(name.Symbol)
        }, "pattern");

        InfoViewItem Context(InfoView.ScopedContext context) => new()
        {
            Text = context.Context,
            Name = InfoViewName.AtStart(context.Context, context.Name),
            Detail = Describe(context.Source),
            Location = Locate(context.Span),
            Documentation = context.Documented.Select(DocOf).FirstOrDefault(doc => doc is not null)
        };

        // A type's definition, as hover shows it: what hover says after it, then the type's doc comment.
        InfoViewItem Definition(string definition, string? note, NyxCompiler.Symbol? symbol, Parser.Program.SourceSpan span)
        {
            var notes = new[] { note, symbol is null ? null : DocOf(symbol) }.OfType<string>().ToList();
            return new InfoViewItem
            {
                Text = definition,
                Location = Locate(symbol?.Declaration ?? span),
                Documentation = notes.Count == 0 ? null : string.Join("\n\n", notes)
            };
        }

        var scopes = new List<InfoViewScope>();
        if (expression is not null)
        {
            scopes.Add(new InfoViewScope
            {
                Kind = "expression",
                Label = expression.Heading.Label?.Value,
                Heading = expression.Heading.Code,
                Location = Locate(expression.Span),
                Sections =
                [
                    new InfoViewSection
                    {
                        Id = "expression",
                        Title = "Expression",
                        Items =
                        [
                            AsDeclared(expression.Name, expression.Type, new InfoViewItem
                            {
                                Text = Signature(expression.Name, expression.Type),
                                Location = Locate(expression.Span),
                                Documentation = expression.Symbol?.Value is { } symbol ? DocOf(symbol) : null
                            }, null) with { Name = null }
                        ]
                    }
                ]
            });
        }

        if (typeAt is not null)
        {
            List<InfoViewSection> sections = [];
            if (typeAt.Definition?.Value is { } definition)
            {
                sections.Add(new InfoViewSection
                {
                    Id = "definition",
                    Title = "Definition",
                    Items = [Definition(definition, typeAt.Note?.Value, typeAt.Symbol?.Value, typeAt.Span)]
                });
            }

            if (typeAt.Type?.Value is { } type)
            {
                sections.Add(new InfoViewSection { Id = "type", Title = "Type", Items = [new InfoViewItem { Text = type, Location = Locate(typeAt.Span) }] });
            }

            if (!typeAt.Contexts.IsEmpty)
            {
                sections.Add(new InfoViewSection { Id = "contexts", Title = "Contexts", Items = typeAt.Contexts.Select(Context).ToList() });
            }

            scopes.Add(new InfoViewScope
            {
                Kind = "type",
                Label = typeAt.Heading.Label?.Value,
                Heading = typeAt.Heading.Code,
                Location = Locate(typeAt.Span),
                Sections = sections
            });
        }

        // The flow of contexts is through the innermost def around the position.
        var flowShown = false;
        foreach (var scope in info?.Scopes ?? [])
        {
            var names = scope.Names.Select(Name).ToList();
            var contexts = scope.Contexts.Select(Context).ToList();
            var sections = new List<InfoViewSection>();
            void Add(string id, string title, IReadOnlyList<InfoViewItem> items)
            {
                if (items.Count > 0)
                {
                    sections.Add(new InfoViewSection { Id = id, Title = title, Items = items });
                }
            }

            string kind;
            string heading = scope.Heading.Code;
            string? documentation = null;
            if (scope.Kind.IsLambdaScope)
            {
                kind = "lambda";
                if (Locate(scope.Span) is { } at)
                {
                    heading = $"{heading} (line {at.Range.Start.Line + 1})";
                }

                Add("type", "Type", scope.Type?.Value is { } type ? [new InfoViewItem { Text = type, Location = Locate(scope.Span) }] : []);
                Add("names", "Parameters and locals", names);
                Add("uses", "Uses", contexts);
            }
            else if (scope.Kind.IsFunctionScope)
            {
                kind = "function";
                var signature = scope.Type?.Value is { } type
                    ? AsDeclared(scope.Symbol?.Value.Name ?? "", type, new InfoViewItem
                    {
                        Text = Signature(scope.Symbol?.Value.Name ?? "", type),
                        Name = scope.Symbol?.Value is { } named ? new InfoViewName(0, named.Name.Length) : null,
                        Location = Locate(scope.Span),
                        Documentation = scope.Symbol?.Value is { } def ? DocOf(def) : null
                    }, null)
                    : null;
                Add("signature", "Signature", signature is null ? [] : [signature]);
                Add("names", "Parameters and locals", names);
                Add("contexts", "Contexts", contexts);
                if (!flowShown && flow is not null)
                {
                    flowShown = true;
                    Add("flow", "Context flow", flow.Entries.Select(entry => FlowItem(entry, Locate)).ToList());
                }
            }
            else if (scope.Kind.IsTypeScope)
            {
                kind = "typeDefinition";
                Add("definition", "Definition", scope.Type?.Value is { } definition
                    ? [Definition(definition, scope.Note?.Value, scope.Symbol?.Value, scope.Span)]
                    : []);
                Add("parameters", "Parameters", scope.Parameters.Select(parameter => new InfoViewItem
                {
                    Text = parameter.Bounds.IsEmpty ? parameter.Name : $"[{string.Join(", ", parameter.Bounds)}] {parameter.Name}",
                    Detail = parameter.IsAssociated ? "associated type" : "parameter"
                }).ToList());
                // An attached def is listed among the module's too, but says here that it is attached.
                Add("attached", "Attached", names.Select(name => name with { Detail = "attached" }).ToList());
            }
            else
            {
                kind = "module";
                documentation = docs?.ForModule(fileName!)?.Value;
                Add("names", "Definitions and imports", names);
                Add("contexts", "Contexts", contexts);
            }

            scopes.Add(new InfoViewScope
            {
                Kind = kind,
                Label = scope.Heading.Label?.Value,
                Heading = heading,
                Location = Locate(scope.Span),
                Documentation = documentation,
                Sections = sections
            });
        }

        // Each type's name links to its definition, in every item.
        var types = new Dictionary<string, Location>();
        foreach (var (name, symbol) in info?.TypeNames.Select(entry => (entry.Item1, entry.Item2)) ?? [])
        {
            if (Locate(symbol.Declaration) is { } definition)
            {
                types.TryAdd(name, definition);
            }
        }

        // The line's messages come first, and only when it has any.
        var messages = Messages(uri, document, request.Position.Line);
        IEnumerable<InfoViewScope> messagesScope = messages.Count == 0
            ? []
            : [new InfoViewScope { Kind = "messages", Heading = "Messages", Sections = [], Messages = messages }];
        // The expression's item is compared with the others as `name: type`, as a def's signature or a local is written,
        // and then shown as its type alone, as a lambda's is: its heading already writes the expression.
        var shown = WithoutRepeats(scopes)
            .Select(scope => scope.Kind == "expression" && expression is not null
                ? scope with { Sections = scope.Sections.Select(section => AsType(section, expression.Type)).ToList() }
                : scope)
            .ToList();
        var expressionScopes = shown.Count(scope => scope.Kind is "expression" or "type");
        return new InfoViewResult
        {
            // The side bar is narrow: past the third scope around the position they start closed, as the module always
            // does.
            Scopes =
            [
                .. messagesScope,
                .. shown.Select((scope, index) => scope with
                {
                    Collapsed = index - expressionScopes >= 3 || scope.Kind == "module",
                    Sections = scope.Sections
                        .Select(section => section with { Items = section.Items.Select(item => InfoViewTypeLinks.WithLinks(item, types)).ToList() })
                        .ToList()
                })
            ],
            Highlights = highlights is null
                ? []
                :
                [
                    .. Highlighted(highlights.Providers, "provider", Locate, uri),
                    .. Highlighted(highlights.Calls, "call", Locate, uri)
                ]
        };
    }

    /// <summary>
    /// The expression scope's <paramref name="section"/> as its <c>type</c> section, each item written as
    /// <paramref name="type"/> alone; but a custom pattern's, which is written as it was declared.
    /// </summary>
    private static InfoViewSection AsType(InfoViewSection section, string type) =>
        section.Id != "expression"
            ? section
            : section with
            {
                Id = "type",
                Title = "Type",
                Items = section.Items
                    .Select(item => item.Text.StartsWith(Parser.Program.PatternSyntax.keyword + " ") ? item : item with { Text = type })
                    .ToList()
            };

    /// <summary>
    /// The span in the document's last typed text that the expression at the top describes: the request's selection,
    /// or, where it sends none or an empty one, the position at <paramref name="offset"/>, as an empty span. <c>null</c>
    /// where that text has been edited since, as hover is.
    /// </summary>
    private static Parser.Program.SourceSpan? SelectedInLastGood(InfoViewParams request, OpenDocument document, int offset)
    {
        var (start, end) = request.Selection is { } range
            ? (document.Positions.ToOffset(range.Start), document.Positions.ToOffset(range.End))
            : (offset, offset);
        if (end <= start)
        {
            (start, end) = (offset, offset);
        }

        // The end is past the selection's last character, which is the one mapped.
        var typedStart = document.ToLastGood(start);
        var typedEnd = end > start ? document.ToLastGood(end - 1) + 1 : typedStart;
        return document.LastGood is { } lastGood && typedStart is { } from && typedEnd is { } to
            ? new Parser.Program.SourceSpan(lastGood.FileName, from, to, 0, 0)
            : null;
    }

    /// <summary>
    /// <paramref name="scopes"/>, innermost first, less each item that repeats one a scope further out shows, which is
    /// kept, with its context: the same text, naming the same thing, at the same place, and saying the same of it. An
    /// item's detail says what it is, a local or a parameter, so two that say different things are both kept, but one
    /// that says nothing, as the expression at the top, repeats any. A def's signature is never left out, though the
    /// module's names list the def too: it is what the def's scope is about. So the cursor on a def's name shows its
    /// signature only in the def's scope, with its doc comment. A section left with no items is left out, and so is a
    /// scope left with no sections.
    /// </summary>
    internal static IReadOnlyList<InfoViewScope> WithoutRepeats(IReadOnlyList<InfoViewScope> scopes)
    {
        static (string, string?, Range?) Place(InfoViewItem item) => (item.Text, item.Location?.Uri.ToString(), item.Location?.Range);

        // The details of the items shown further out, by their text and place.
        var shownFurtherOut = new Dictionary<(string, string?, Range?), HashSet<string?>>();
        bool Repeats(InfoViewItem item) =>
            shownFurtherOut.TryGetValue(Place(item), out var details) && (item.Detail is null || details.Contains(item.Detail));

        var kept = new List<InfoViewScope>();
        foreach (var scope in scopes.Reverse())
        {
            // A def's signature is what its scope is about, so it is kept though the module's names list the def too.
            var sections = scope.Sections
                .Select(section => section.Id == "signature" ? section : section with { Items = section.Items.Where(item => !Repeats(item)).ToList() })
                .Where(section => section.Items.Count > 0)
                .ToList();
            foreach (var item in scope.Sections.SelectMany(section => section.Items))
            {
                if (!shownFurtherOut.TryGetValue(Place(item), out var details))
                {
                    shownFurtherOut[Place(item)] = details = [];
                }

                details.Add(item.Detail);
            }

            if (sections.Count > 0 || scope.Sections.Count == 0)
            {
                kept.Add(scope with { Sections = sections });
            }
        }

        kept.Reverse();
        return kept;
    }

    /// <summary>
    /// The messages on <paramref name="line"/> of the document, each in full (see <see cref="InfoViewMessage"/>): those
    /// published on it, from this text's own compile, whose range takes in the line.
    /// </summary>
    private static IReadOnlyList<InfoViewMessage> Messages(DocumentUri uri, OpenDocument document, int line)
    {
        var converted = document.ConvertedDiagnostics(uri);
        return converted.Own
            .Zip(converted.Origins)
            .Where(pair => pair.First.Range.Start.Line <= line && line <= pair.First.Range.End.Line)
            .Select(pair => Message(uri, document, pair.First, pair.Second))
            .ToList();
    }

    /// <summary>
    /// <paramref name="published"/> in full: the headline and the body are the published message's first line and the
    /// rest, and <paramref name="origin"/>, the compiler's diagnostic it was made from, if any, gives the suggestions
    /// and the fixes. A fix that edits another file is left out, as its quick fix is.
    /// </summary>
    private static InfoViewMessage Message(DocumentUri uri, OpenDocument document, Diagnostic published, NyxCompiler.Diagnostic? origin)
    {
        var text = (published.Message.String ?? "").ReplaceLineEndings("\n");
        var lineEnd = text.IndexOf('\n');
        var headline = lineEnd < 0 ? text : text[..lineEnd];
        var body = lineEnd < 0 ? "" : text[(lineEnd + 1)..];
        return new InfoViewMessage
        {
            Severity = Describe(published.Severity),
            Code = published.Code?.String,
            Reference = published.CodeDescription?.Href,
            Headline = headline,
            Location = new Location { Uri = uri, Range = published.Range },
            Notes =
            [
                .. body.Length > 0 ? [body] : Array.Empty<string>(),
                .. origin?.Suggestions.Select(suggestion => $"help: {suggestion}") ?? []
            ],
            Related = published.RelatedInformation?
                .Select(related => new InfoViewItem { Text = related.Message, Detail = Place(uri, related.Location), Location = related.Location })
                .ToList() ?? [],
            Fixes = origin?.Fixes
                .Select(fix => CodeActionHandler.FixEdit(uri, document, fix) is { } edit ? new InfoViewFix { Title = fix.Title, Edit = edit } : null)
                .OfType<InfoViewFix>()
                .ToList() ?? []
        };
    }

    /// <summary>Where <paramref name="location"/> is, as a message's other place says: <c>line 4</c>, or <c>other.nyx:4</c> in another file.</summary>
    private static string Place(DocumentUri uri, Location location) =>
        location.Uri == uri
            ? $"line {location.Range.Start.Line + 1}"
            : $"{Path.GetFileName(location.Uri.Path)}:{location.Range.Start.Line + 1}";

    /// <summary>
    /// The item of a provider in the context flow: its context, its source with how many calls it serves (a
    /// <c>use</c> that serves none is <c>unused</c>), linking to it, with an item under it for each call it serves. An
    /// instance the compiler builds says how: <c>attached Point.show</c>, <c>derived</c>, <c>built in</c>.
    /// </summary>
    private static InfoViewItem FlowItem(NyxCompiler.ContextFlow.FlowEntry entry, Func<Parser.Program.SourceSpan, Location?> locate)
    {
        var source = Describe(entry.Source);
        var serves = entry.Calls.Length switch
        {
            0 when entry.Source is InfoView.CallSource.Provided { Item.IsUse: true } => "unused",
            0 => "no calls",
            1 => "1 call",
            var count => $"{count} calls"
        };
        return new InfoViewItem
        {
            Text = entry.Context,
            Name = InfoViewName.AtStart(entry.Context, entry.Name),
            Detail = $"{source} · {serves}",
            Location = locate(entry.Span),
            Children = entry.Calls
                .Select(call => new InfoViewItem { Text = call.Callee, Name = new InfoViewName(0, call.Callee.Length), Detail = "call", Location = locate(call.Span) })
                .ToList()
        };
    }

    /// <summary>The highlights of kind <paramref name="kind"/> at <paramref name="spans"/> that are in this document.</summary>
    private static IEnumerable<InfoViewHighlight> Highlighted(
        IEnumerable<Parser.Program.SourceSpan> spans,
        string kind,
        Func<Parser.Program.SourceSpan, Location?> locate,
        DocumentUri uri) =>
        spans
            .Select(locate)
            .Where(location => location is not null && location.Uri == uri)
            .Select(location => new InfoViewHighlight { Range = location!.Range, Kind = kind });

    /// <summary>
    /// <c>name: type</c>, laid out to fit <paramref name="width"/> characters as the formatter lays out a def's
    /// signature, if the client gave a width and the formatter can read it; else on one line. A signature that fits on
    /// one line already is given back as it is, which is all the formatter could make of it: most of a request's names
    /// do, and laying each out took ten times the rest of the request (#2406).
    /// </summary>
    private static string LaidOut(int? width, string name, string type)
    {
        var oneLine = $"{name}: {type}";
        if (width is not { } columns || (oneLine.Length <= columns && !type.Contains('\n')))
        {
            return oneLine;
        }

        return Nanyx.Formatter.SignatureLayout.layOut(columns, name, type)?.Value ?? oneLine;
    }

    /// <summary>
    /// The location of a span of the last typed text: in this document's text, if it has not been edited since, or in
    /// another file (see <see cref="SourceLocations.Of"/>). Null for a made-up span.
    /// </summary>
    private Location? LocationOf(DocumentUri uri, OpenDocument document, Parser.Program.SourceSpan span)
    {
        if (span.Start < 0)
        {
            return null;
        }

        if (span.File == document.LastGood?.FileName)
        {
            return document.FromLastGood(span.Start, span.End) is { } here
                ? new Location { Uri = uri, Range = document.Positions.ToRange(here.Start, here.End) }
                : null;
        }

        return SourceLocations.Of(uri, document, span, _standardLibrary);
    }

    private static string Describe(SymbolKind kind) =>
        kind.Tag switch
        {
            SymbolKind.Tags.Parameter => "parameter",
            SymbolKind.Tags.LocalValue => "local",
            SymbolKind.Tags.PatternVariable => "pattern variable",
            SymbolKind.Tags.ImportedModule => "import",
            SymbolKind.Tags.TypeDefinition => "type",
            _ => "def"
        };

    private static string Describe(InfoView.ContextSource source) =>
        source.Tag switch
        {
            InfoView.ContextSource.Tags.Signature => "signature",
            InfoView.ContextSource.Tags.Use => "use",
            InfoView.ContextSource.Tags.Inferred => "inferred",
            _ => "runtime"
        };

    private static string Describe(InfoView.CallSource source) =>
        source switch
        {
            InfoView.CallSource.Provided provided => Describe(provided.Item),
            InfoView.CallSource.Attached attached => $"attached {string.Join(", ", attached.Item)}",
            _ when source.IsDerived => "derived",
            _ => "built in"
        };

    private static string Describe(DiagnosticSeverity? severity) =>
        severity switch
        {
            DiagnosticSeverity.Warning => "warning",
            DiagnosticSeverity.Information => "info",
            DiagnosticSeverity.Hint => "hint",
            _ => "error"
        };
}
