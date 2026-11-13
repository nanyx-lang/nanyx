using MediatR;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using InfoView = NyxCompiler.InfoView;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using SymbolKind = NyxCompiler.SymbolKind;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>The parameters of <c>nanyx/infoView</c>: the document and the position to describe.</summary>
[Parallel, Method(InfoViewHandler.Method, Direction.ClientToServer)]
public sealed record InfoViewParams : IRequest<InfoViewResult?>
{
    public required TextDocumentIdentifier TextDocument { get; init; }
    public required Position Position { get; init; }
}

/// <summary>
/// What <c>nanyx/infoView</c> returns: its sections, always all of them and in this order, and the ranges of the
/// document for the editor to highlight at the position (see <see cref="InfoViewHighlight"/>).
/// </summary>
public sealed record InfoViewResult
{
    public required IReadOnlyList<InfoViewSection> Sections { get; init; }
    public IReadOnlyList<InfoViewHighlight> Highlights { get; init; } = [];
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

/// <summary>
/// A section of the info view. <see cref="Id"/> is one of <c>cursor</c>, <c>messages</c>, <c>inScope</c>,
/// <c>topLevel</c>, <c>contexts</c>, <c>call</c> and <c>flow</c>; <see cref="Group"/> is what the section is about, for
/// a client to set the groups apart: <c>cursor</c> (what is at the position and the messages on its line),
/// <c>scope</c> (the names in scope) or <c>contexts</c>; <see cref="Collapsed"/> says whether a client shows it closed
/// at first.
/// </summary>
public sealed record InfoViewSection
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Group { get; init; }
    public bool Collapsed { get; init; }
    public required IReadOnlyList<InfoViewItem> Items { get; init; }
}

/// <summary>
/// An item of a section: its text (<c>name: type</c>, a context, a message), what kind of thing it is or where it
/// comes from (<c>parameter</c>, <c>use</c>, <c>error NYX0101</c>), where it is, for a link, and the doc comment of
/// what it names, as Markdown with its links written out, as hover shows it: for an import, that of the module it
/// brings in. <see cref="Children"/> are the items under it: the calls a provider in the flow serves.
/// </summary>
public sealed record InfoViewItem
{
    public required string Text { get; init; }
    public string? Detail { get; init; }
    public Location? Location { get; init; }
    public string? Documentation { get; init; }
    public IReadOnlyList<InfoViewItem>? Children { get; init; }
}

/// <summary>
/// <c>nanyx/infoView</c>: what the compiler knows at a position, for the info view panel. What is at the cursor: on a
/// module's name, as hover finds it, the module and its doc comment (see <see cref="ModuleItem"/>); otherwise the
/// expression there with its type, as hover shows it, and on a use of a generic def the def's declared signature and
/// how the use fills in its type variables (see <see cref="ExpressionItems"/>); the diagnostics on the position's
/// line; the locals, parameters and pattern variables in scope, nearest first; the top-level defs
/// and imports in scope; the contexts in scope, each with where it comes from (a def's signature, a <c>use</c>, a def
/// that was inferred to require it, or the runtime); the contexts the innermost call around the position that requires
/// any requires, each with where it finds it (one of those, or an instance the compiler builds: from attached
/// definitions, derived from the type's parts, or its own); the flow of contexts through the def around the position,
/// each provider with the calls it serves (see <see cref="NyxCompiler.ContextFlow"/>). It also gives the ranges to highlight: a provider at the position and the calls it serves, or a
/// call and what provides its contexts. Read from the
/// document's last typed compile, as hover is (see <see cref="OpenDocument.LastGood"/>); the diagnostics are this
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
        if (document.LastGood is { Compiled: { } compiled } lastGood && document.NearestInLastGood(offset) is { } nearest)
        {
            info = InfoView.at(_settings.TypeNotation, lastGood.FileName, compiled, nearest);
            var contexts = NyxCompiler.ContextFlow.at(_settings.TypeNotation, compiled, nearest);
            flow = contexts.Flow?.Value;
            highlights = contexts.Highlights;
            docs = lastGood.DocReader(uri, nearest, _standardLibrary);
        }

        Location? Locate(Parser.Program.SourceSpan span) => LocationOf(uri, document, span);
        string? DocOf(NyxCompiler.Symbol symbol) => docs?.ForSymbol(symbol)?.Value;

        // What is at the cursor is only shown where the text has not been edited since it was typed.
        IReadOnlyList<InfoViewItem> cursor = document.ToLastGood(offset) is not { } typed
            ? []
            : document.LastGood is { Compiled: { } typedCompile } typedDocument
              && NyxCompiler.Hover.moduleAt(typedDocument.FileName, typedCompile, typed)?.Value is { } module
                ? [ModuleItem(module, typedDocument.DocReader(uri, typed, _standardLibrary), Locate)]
                : info?.Expression?.Value is { } hover
                    ? ExpressionItems(hover, info.Instantiation?.Value, Locate, info.ExpressionSymbol?.Value is { } symbol ? DocOf(symbol) : null)
                    : [];
        var line = request.Position.Line;
        var messages = document.PublishedDiagnostics(uri)
            .Where(diagnostic => diagnostic.Range.Start.Line <= line && line <= diagnostic.Range.End.Line)
            .Select(diagnostic => new InfoViewItem
            {
                Text = diagnostic.Message.String ?? "",
                Detail = $"{Describe(diagnostic.Severity)} {diagnostic.Code?.String}".TrimEnd(),
                Location = new Location { Uri = uri, Range = diagnostic.Range }
            })
            .ToList();

        InfoViewItem Name(InfoView.ScopedName name) => new()
        {
            Text = name.Type?.Value is { } type ? $"{name.Name}: {type}" : name.Name,
            Detail = Describe(name.Kind),
            Location = Locate(name.Declaration),
            Documentation = DocOf(name.Symbol)
        };

        return new InfoViewResult
        {
            Sections =
            [
                new InfoViewSection { Id = "cursor", Title = "At the cursor", Group = "cursor", Items = cursor },
                new InfoViewSection { Id = "messages", Title = "Messages", Group = "cursor", Items = messages },
                new InfoViewSection
                {
                    Id = "inScope",
                    Title = "In scope",
                    Group = "scope",
                    Items = info?.Locals.Select(Name).ToList() ?? []
                },
                new InfoViewSection
                {
                    Id = "topLevel",
                    Title = "Top level",
                    Group = "scope",
                    Collapsed = true,
                    Items = info?.TopLevel.Select(Name).ToList() ?? []
                },
                new InfoViewSection
                {
                    Id = "contexts",
                    Title = "Contexts",
                    Group = "contexts",
                    Items = info?.Contexts
                        .Select(context => new InfoViewItem
                        {
                            Text = context.Context,
                            Detail = Describe(context.Source),
                            Location = Locate(context.Span),
                            Documentation = context.Documented.Select(DocOf).FirstOrDefault(doc => doc is not null)
                        })
                        .ToList() ?? []
                },
                new InfoViewSection
                {
                    Id = "call",
                    Title = "This call",
                    Group = "contexts",
                    Items = info?.Call
                        .Select(context => new InfoViewItem
                        {
                            Text = context.Context,
                            Detail = Describe(context.Source),
                            Location = Locate(context.Span),
                            Documentation = context.Documented.Select(DocOf).FirstOrDefault(doc => doc is not null)
                        })
                        .ToList() ?? []
                },
                new InfoViewSection
                {
                    Id = "flow",
                    Title = flow is null ? "Context flow" : $"Context flow in {flow.Def}",
                    Group = "contexts",
                    Items = flow?.Entries.Select(entry => FlowItem(entry, Locate)).ToList() ?? []
                }
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
    /// The item of a provider in the context flow: its context, its source with how many calls it serves (a
    /// <c>use</c> that serves none is <c>unused</c>), linking to it, with an item under it for each call it serves.
    /// </summary>
    private static InfoViewItem FlowItem(NyxCompiler.ContextFlow.FlowEntry entry, Func<Parser.Program.SourceSpan, Location?> locate)
    {
        var source = Describe(entry.Source);
        var serves = entry.Calls.Length switch
        {
            0 when entry.Source.IsUse => "unused",
            0 => "no calls",
            1 => "1 call",
            var count => $"{count} calls"
        };
        return new InfoViewItem
        {
            Text = entry.Context,
            Detail = $"{source} · {serves}",
            Location = locate(entry.Span),
            Children = entry.Calls.Select(call => new InfoViewItem { Text = call.Callee, Detail = "call", Location = locate(call.Span) }).ToList()
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
    /// The item of a module's name at the cursor (see <see cref="NyxCompiler.Hover.moduleAt"/>): <c>module name</c>,
    /// linking to the name, with the module's doc comment, as hover shows it.
    /// </summary>
    private static InfoViewItem ModuleItem(
        NyxCompiler.Hover.ModuleHover module,
        NyxCompiler.Documentation.Reader docs,
        Func<Parser.Program.SourceSpan, Location?> locate) =>
        new()
        {
            Text = $"module {module.Name}",
            Detail = "module",
            Location = locate(module.Span),
            Documentation = docs.ForModule(module.File)?.Value
        };

    /// <summary>
    /// The items of the cursor section on an expression: the expression's type, as hover shows it, linking to the expression. On a
    /// use of a generic def, the def's declared signature (<c>declared</c>) comes first, then the type at this use
    /// (<c>at this use</c>), then what the use fills each of the signature's type variables with
    /// (<c>a = string, b = int</c>), each linking to the def. The first carries <paramref name="documentation"/>, the
    /// doc comment of the name there.
    /// </summary>
    private static IReadOnlyList<InfoViewItem> ExpressionItems(
        NyxCompiler.Hover.HoverInfo hover,
        NyxCompiler.Hover.Instantiation? instantiation,
        Func<Parser.Program.SourceSpan, Location?> locate,
        string? documentation)
    {
        if (instantiation is null)
        {
            return [new InfoViewItem { Text = hover.Type, Location = locate(hover.Span), Documentation = documentation }];
        }

        var definition = locate(instantiation.Definition);
        var items = new List<InfoViewItem>
        {
            new() { Text = instantiation.Declared, Detail = "declared", Location = definition, Documentation = documentation },
            new() { Text = hover.Type, Detail = "at this use", Location = definition }
        };
        if (!instantiation.Bindings.IsEmpty)
        {
            items.Add(new InfoViewItem
            {
                Text = string.Join(", ", instantiation.Bindings.Select(binding => $"{binding.Item1} = {binding.Item2}")),
                Detail = "type variables",
                Location = definition
            });
        }

        return items;
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
