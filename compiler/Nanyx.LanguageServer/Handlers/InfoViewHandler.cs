using MediatR;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using InfoView = NyxCompiler.InfoView;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using SymbolKind = NyxCompiler.SymbolKind;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// The parameters of <c>nanyx/infoView</c>: the document and the position to describe, and the width, in characters,
/// the client shows an item's text in, if it says: a signature that doesn't fit it is laid out as the formatter lays
/// out a def's (see <see cref="Nanyx.Formatter.SignatureLayout.layOut"/>).
/// </summary>
[Parallel, Method(InfoViewHandler.Method, Direction.ClientToServer)]
public sealed record InfoViewParams : IRequest<InfoViewResult?>
{
    public required TextDocumentIdentifier TextDocument { get; init; }
    public required Position Position { get; init; }
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
/// diagnostic, with its <see cref="Messages"/> and no sections), <c>lambda</c> (<c>type</c>, <c>names</c>,
/// <c>uses</c>), <c>function</c> (<c>signature</c>, <c>names</c>, <c>contexts</c>, <c>flow</c>) and <c>module</c>
/// (always last, with <c>names</c> and <c>contexts</c>); a lambda or function scope comes once for each lambda or def
/// around the position, and leaves out the sections it has nothing for. <see cref="Heading"/> names the scope and
/// <see cref="Location"/> is where it links: a lambda by its parameters and line, a def by its name, the module by its
/// declared name. <see cref="Documentation"/> is the module's doc comment. <see cref="Collapsed"/> says whether a client
/// shows it closed at first: the module, and the scopes after the third, not counting the messages.
/// </summary>
public sealed record InfoViewScope
{
    public required string Kind { get; init; }
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
/// First, if the position's line has any, its diagnostics, each in full (see <see cref="InfoViewMessage"/>). Then each
/// lambda and def around the position (see <see cref="InfoView.Scope"/>): a lambda's type, with the contexts it
/// carries, a def's signature and doc comment, the parameters, locals and pattern variables each declares, the
/// contexts each provides, and in the innermost def the flow of contexts through it, each provider with the calls it
/// serves, an instance the compiler builds among them (see <see cref="NyxCompiler.ContextFlow"/>). Last the module: its
/// doc comment, its top-level defs and imports in scope, and the runtime's contexts. It also gives the ranges to
/// highlight: a provider at the position and the calls it serves, or a call and what provides its contexts. Read from
/// the document's last typed compile, as hover is (see <see cref="OpenDocument.LastGood"/>); the diagnostics are this
/// text's own. Each item links to what it describes: a name to its declaration, a context to what provides it. Each
/// item whose name or context has a doc comment carries it, read as hover reads it (see
/// <see cref="NyxCompiler.Documentation.Reader"/>): a context's is that of the def a <c>use</c> names, if it has
/// one, or else the context's own.
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
        NyxCompiler.ContextFlow.Flow? flow = null;
        NyxCompiler.ContextFlow.Highlights? highlights = null;
        NyxCompiler.Documentation.Reader? docs = null;
        string? fileName = null;
        if (document.LastGood is { Compiled: { } compiled } lastGood && document.NearestInLastGood(offset) is { } nearest)
        {
            fileName = lastGood.FileName;
            info = InfoView.at(_settings.TypeNotation, lastGood.FileName, compiled, nearest);
            var contexts = NyxCompiler.ContextFlow.at(_settings.TypeNotation, compiled, nearest);
            flow = contexts.Flow?.Value;
            highlights = contexts.Highlights;
            docs = lastGood.DocReader(uri, nearest, _standardLibrary);
        }

        Location? Locate(Parser.Program.SourceSpan span) => LocationOf(uri, document, span);
        string Signature(string name, string type) => LaidOut(request.Width, name, type);
        string? DocOf(NyxCompiler.Symbol symbol) => docs?.ForSymbol(symbol)?.Value;

        InfoViewItem Name(InfoView.ScopedName name) => new()
        {
            Text = name.Type?.Value is { } type ? Signature(name.Name, type) : name.Name,
            Name = new InfoViewName(0, name.Name.Length),
            Detail = Describe(name.Kind),
            Location = Locate(name.Declaration),
            Documentation = DocOf(name.Symbol)
        };

        InfoViewItem Context(InfoView.ScopedContext context) => new()
        {
            Text = context.Context,
            Name = InfoViewName.AtStart(context.Context, context.Name),
            Detail = Describe(context.Source),
            Location = Locate(context.Span),
            Documentation = context.Documented.Select(DocOf).FirstOrDefault(doc => doc is not null)
        };

        var scopes = new List<InfoViewScope>();
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
            string heading = scope.Heading;
            string? documentation = null;
            if (scope.Kind.IsLambdaScope)
            {
                kind = "lambda";
                if (Locate(scope.Span) is { } at)
                {
                    heading = $"{scope.Heading} (line {at.Range.Start.Line + 1})";
                }

                Add("type", "Type", scope.Type?.Value is { } type ? [new InfoViewItem { Text = type, Location = Locate(scope.Span) }] : []);
                Add("names", "Parameters and locals", names);
                Add("uses", "Uses", contexts);
            }
            else if (scope.Kind.IsFunctionScope)
            {
                kind = "function";
                var signature = scope.Type?.Value is { } type
                    ? new InfoViewItem
                    {
                        Text = Signature(scope.Symbol?.Value.Name ?? "", type),
                        Name = scope.Symbol?.Value is { } named ? new InfoViewName(0, named.Name.Length) : null,
                        Detail = scope.Declared ? "declared" : "inferred",
                        Location = Locate(scope.Span),
                        Documentation = scope.Symbol?.Value is { } def ? DocOf(def) : null
                    }
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
        return new InfoViewResult
        {
            // The side bar is narrow: past the third scope they start closed, as the module always does.
            Scopes =
            [
                .. messagesScope,
                .. scopes.Select((scope, index) => scope with
                {
                    Collapsed = index >= 3 || scope.Kind == "module",
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
