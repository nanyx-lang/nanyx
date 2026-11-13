using Microsoft.FSharp.Core;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Diagnostic = OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer;

/// <summary>Sends the diagnostics from a document's compile to the client.</summary>
public sealed class DiagnosticsPublisher
{
    /// <summary>The <c>source</c> field on every diagnostic, so the editor can attribute them.</summary>
    public const string Source = "nanyx";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly ILanguageServerFacade _server;

    public DiagnosticsPublisher(ILanguageServerFacade server)
    {
        _server = server;
    }

    /// <summary>Publishes what the compiler reported for the document at <paramref name="uri"/>.</summary>
    public void Publish(DocumentUri uri, OpenDocument document, int? version)
    {
        var diagnostics = document.PublishedDiagnostics(uri);

        _server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = uri,
            Version = version,
            Diagnostics = new Container<Diagnostic>(diagnostics)
        });
    }

    /// <summary>Tells the client a document has no diagnostics any more (it was closed).</summary>
    public void Clear(DocumentUri uri)
    {
        _server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = uri,
            Diagnostics = new Container<Diagnostic>()
        });
    }

    /// <summary>
    /// The LSP diagnostics for a compiled document. Each is published on the document itself, including one the
    /// compiler found in a module the document imports: that one is shown at the start of the document, says which
    /// file it is in, and links to its place there. Read them from <see cref="OpenDocument.PublishedDiagnostics"/>,
    /// which converts them once.
    /// </summary>
    internal static IReadOnlyList<Diagnostic> Convert(DocumentUri uri, OpenDocument document)
    {
        if (document.Compiled is null)
        {
            // The compiler threw: report it where the user will see it.
            return
            [
                new Diagnostic
                {
                    Severity = DiagnosticSeverity.Error,
                    Source = Source,
                    Range = new Range(0, 0, 0, 0),
                    Message = $"Internal compiler error: {document.CompilerFailure?.Message}"
                }
            ];
        }

        var locator = new Locator(uri, document);
        return document.Compiled.Diagnostics.Select(diagnostic => ToLsp(diagnostic, document, locator)).ToList();
    }

    private static Diagnostic ToLsp(NyxCompiler.Diagnostic diagnostic, OpenDocument document, Locator locator)
    {
        var span = FSharpOption<Parser.Program.SourceSpan>.get_IsSome(diagnostic.Span) ? diagnostic.Span.Value : null;
        var inDocument = span is null || IsDocumentFile(span.File, document);

        var related = diagnostic.Related
            .Select(item => locator.TryLocate(item.Item1) is { } location
                ? new DiagnosticRelatedInformation { Location = location, Message = item.Item2 }
                : null)
            .OfType<DiagnosticRelatedInformation>()
            .ToList();

        var message = diagnostic.Message;
        if (!inDocument)
        {
            message = $"In {span!.File}: {message}";
            if (locator.TryLocate(span) is { } location)
            {
                related.Insert(0, new DiagnosticRelatedInformation { Location = location, Message = diagnostic.Title });
            }
        }

        return new Diagnostic
        {
            Severity = ToLsp(diagnostic.Severity),
            Code = diagnostic.Code,
            Source = Source,
            Message = message,
            // A diagnostic about the whole compilation, or about another file, is shown at the start of this one.
            Range = inDocument && span is not null
                ? document.Positions.ToRange(span.Start, span.End)
                : new Range(0, 0, 0, 0),
            RelatedInformation = related.Count > 0 ? new Container<DiagnosticRelatedInformation>(related) : null
        };
    }

    private static DiagnosticSeverity ToLsp(NyxCompiler.Severity severity) =>
        severity.Tag switch
        {
            NyxCompiler.Severity.Tags.Warning => DiagnosticSeverity.Warning,
            NyxCompiler.Severity.Tags.Info => DiagnosticSeverity.Information,
            NyxCompiler.Severity.Tags.Hint => DiagnosticSeverity.Hint,
            _ => DiagnosticSeverity.Error
        };

    private static bool IsDocumentFile(string file, OpenDocument document) =>
        string.Equals(file, document.FileName, PathComparison);

    /// <summary>
    /// Finds the LSP location of a span: in the document from its text, and in another file on disk from that
    /// file's text, read once. A file that is not on disk (a module of the embedded standard library) has none.
    /// </summary>
    private sealed class Locator(DocumentUri uri, OpenDocument document)
    {
        private readonly Dictionary<string, SourcePositions?> _files = new();

        public Location? TryLocate(Parser.Program.SourceSpan span)
        {
            if (IsDocumentFile(span.File, document))
            {
                return new Location { Uri = uri, Range = document.Positions.ToRange(span.Start, span.End) };
            }

            if (!_files.TryGetValue(span.File, out var positions))
            {
                positions = Path.IsPathRooted(span.File) && File.Exists(span.File)
                    ? new SourcePositions(File.ReadAllText(span.File))
                    : null;
                _files[span.File] = positions;
            }

            return positions is null
                ? null
                : new Location { Uri = DocumentUri.FromFileSystemPath(span.File), Range = positions.ToRange(span.Start, span.End) };
        }
    }
}
