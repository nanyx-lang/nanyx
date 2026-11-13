using Microsoft.FSharp.Core;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Diagnostic = OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer;

/// <summary>
/// The LSP diagnostics of a document's compile: those published on the document itself, and those found in files it
/// imports, by the file they are in.
/// </summary>
public sealed record ConvertedDiagnostics(
    IReadOnlyList<Diagnostic> Own,
    IReadOnlyDictionary<DocumentUri, IReadOnlyList<Diagnostic>> Imported);

/// <summary>
/// Sends the diagnostics from a document's compile to the client: the document's own on the document, and each one
/// in a file it imports on that file. An imported file the client has open is left to its own compile.
/// </summary>
public sealed class DiagnosticsPublisher
{
    /// <summary>The <c>source</c> field on every diagnostic, so the editor can attribute them.</summary>
    public const string Source = "nanyx";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly ILanguageServerFacade _server;
    private readonly DocumentStore _documents;
    private readonly object _lock = new();

    /// <summary>
    /// For each imported file published to, what the compile of each open document that imports it found there, the
    /// latest last. The latest is what the file shows.
    /// </summary>
    private readonly Dictionary<DocumentUri, List<(DocumentUri Importer, IReadOnlyList<Diagnostic> Diagnostics)>> _imported = new();

    public DiagnosticsPublisher(ILanguageServerFacade server, DocumentStore documents)
    {
        _server = server;
        _documents = documents;
    }

    /// <summary>
    /// Publishes what the compiler reported for the document at <paramref name="uri"/>, on it and on the files it
    /// imports, and clears what its last compile published on an imported file that has none now.
    /// </summary>
    public void Publish(DocumentUri uri, OpenDocument document, int? version)
    {
        var converted = document.ConvertedDiagnostics(uri);
        lock (_lock)
        {
            Send(uri, converted.Own, version);
            UpdateImported(uri, converted.Imported);
        }
    }

    /// <summary>
    /// Clears the diagnostics of a document the client has closed, and those its compile published on the files it
    /// imports. A closed file that an open document imports shows what that document's compile found in it.
    /// </summary>
    public void Closed(DocumentUri uri)
    {
        lock (_lock)
        {
            PublishImported(uri);
            UpdateImported(uri, new Dictionary<DocumentUri, IReadOnlyList<Diagnostic>>());
        }
    }

    /// <summary>Records what a compile of <paramref name="importer"/> found in the files it imports, and publishes them.</summary>
    private void UpdateImported(DocumentUri importer, IReadOnlyDictionary<DocumentUri, IReadOnlyList<Diagnostic>> found)
    {
        var changed = new HashSet<DocumentUri>(found.Keys);
        foreach (var (file, entries) in _imported)
        {
            if (entries.RemoveAll(entry => entry.Importer == importer) > 0)
            {
                changed.Add(file);
            }
        }

        foreach (var (file, diagnostics) in found)
        {
            if (!_imported.TryGetValue(file, out var entries))
            {
                entries = [];
                _imported[file] = entries;
            }

            entries.Add((importer, diagnostics));
        }

        foreach (var file in changed)
        {
            PublishImported(file);
            if (_imported.TryGetValue(file, out var entries) && entries.Count == 0)
            {
                _imported.Remove(file);
            }
        }
    }

    /// <summary>Publishes on an imported file what the latest compile of a document importing it found there.</summary>
    private void PublishImported(DocumentUri file)
    {
        if (_documents.TryGet(file) is not null)
        {
            // The file is open: its own compile publishes its diagnostics.
            return;
        }

        Send(file, _imported.TryGetValue(file, out var entries) && entries.Count > 0 ? entries[^1].Diagnostics : [], null);
    }

    private void Send(DocumentUri uri, IReadOnlyList<Diagnostic> diagnostics, int? version) =>
        _server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = uri,
            Version = version,
            Diagnostics = new Container<Diagnostic>(diagnostics)
        });

    /// <summary>
    /// The LSP diagnostics for a compiled document. One the compiler found in a file the document imports is published
    /// on that file, at its place there, and the import in the document that brings the file in gets one diagnostic
    /// saying the file has errors, linking to each. One in a file that is not on disk (a module of the embedded
    /// standard library) is described there. Read them from <see cref="OpenDocument.ConvertedDiagnostics"/>, which
    /// converts them once.
    /// </summary>
    internal static ConvertedDiagnostics Convert(DocumentUri uri, OpenDocument document)
    {
        if (document.Compiled is null)
        {
            // The compiler threw: report it where the user will see it.
            return new ConvertedDiagnostics(
                [
                    new Diagnostic
                    {
                        Severity = DiagnosticSeverity.Error,
                        Source = Source,
                        Range = new Range(0, 0, 0, 0),
                        Message = $"Internal compiler error: {document.CompilerFailure?.Message}"
                    }
                ],
                new Dictionary<DocumentUri, IReadOnlyList<Diagnostic>>());
        }

        var locator = new Locator(uri, document);
        var own = new List<Diagnostic>();
        var imported = new Dictionary<DocumentUri, List<Diagnostic>>();
        // The diagnostics brought in by each import, and where in `own` the one at the import goes.
        var byImport = new Dictionary<(int Start, int End), (int Index, Parser.Program.SourceSpan Import, List<(NyxCompiler.Diagnostic Diagnostic, Location? Location)> Found)>();
        foreach (var diagnostic in document.Compiled.Diagnostics)
        {
            var span = Some(diagnostic.Span);
            if (span is null || IsDocumentFile(span.File, document))
            {
                own.Add(ToLsp(diagnostic, locator, span is null ? new Range(0, 0, 0, 0) : document.Positions.ToRange(span.Start, span.End)));
                continue;
            }

            var location = locator.TryLocate(span);
            if (location is not null)
            {
                if (!imported.TryGetValue(location.Uri, out var inFile))
                {
                    inFile = [];
                    imported[location.Uri] = inFile;
                }

                inFile.Add(ToLsp(diagnostic, locator, location.Range));
            }

            if (Some(diagnostic.Import) is { } import && IsDocumentFile(import.File, document))
            {
                if (!byImport.TryGetValue((import.Start, import.End), out var group))
                {
                    group = (own.Count, import, []);
                    byImport[(import.Start, import.End)] = group;
                    own.Add(null!);
                }

                group.Found.Add((diagnostic, location));
            }
            else
            {
                own.Add(OnlyInDocument(diagnostic, locator, span, location));
            }
        }

        foreach (var (index, import, found) in byImport.Values)
        {
            own[index] = AtImport(document, import, found);
        }

        return new ConvertedDiagnostics(own, imported.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<Diagnostic>)pair.Value));
    }

    /// <summary>The diagnostic at an import that brings in files with errors: it names them and links to each error.</summary>
    private static Diagnostic AtImport(
        OpenDocument document,
        Parser.Program.SourceSpan import,
        List<(NyxCompiler.Diagnostic Diagnostic, Location? Location)> found)
    {
        var files = found.Select(item => Shown(Some(item.Diagnostic.Span)!.File, document)).Distinct().ToList();
        var message = files.Count == 1
            ? $"'{files[0]}' has errors"
            : $"{string.Join(", ", files.SkipLast(1).Select(file => $"'{file}'"))} and '{files[^1]}' have errors";
        // An error in a file not on disk cannot be linked to, so it is described here.
        foreach (var (diagnostic, _) in found.Where(item => item.Location is null))
        {
            message += $"\n{Shown(Some(diagnostic.Span)!.File, document)}: {diagnostic.Message}";
        }

        var related = found
            .Where(item => item.Location is not null)
            .Select(item => new DiagnosticRelatedInformation { Location = item.Location!, Message = item.Diagnostic.Message })
            .ToList();
        return new Diagnostic
        {
            Severity = found.Min(item => ToLsp(item.Diagnostic.Severity)),
            Source = Source,
            Message = message,
            Range = document.Positions.ToRange(import.Start, import.End),
            RelatedInformation = related.Count > 0 ? new Container<DiagnosticRelatedInformation>(related) : null
        };
    }

    /// <summary>
    /// A diagnostic in another file that no import of the document brings in, shown at the start of the document: it
    /// says which file it is in, and links to its place there.
    /// </summary>
    private static Diagnostic OnlyInDocument(
        NyxCompiler.Diagnostic diagnostic,
        Locator locator,
        Parser.Program.SourceSpan span,
        Location? location)
    {
        var place = location is null
            ? null
            : new DiagnosticRelatedInformation { Location = location, Message = diagnostic.Title };
        return ToLsp(diagnostic, locator, new Range(0, 0, 0, 0), $"In {span.File}: {diagnostic.Message}", place);
    }

    private static Diagnostic ToLsp(
        NyxCompiler.Diagnostic diagnostic,
        Locator locator,
        Range range,
        string? message = null,
        DiagnosticRelatedInformation? place = null)
    {
        var related = diagnostic.Related
            .Select(item => locator.TryLocate(item.Item1) is { } location
                ? new DiagnosticRelatedInformation { Location = location, Message = item.Item2 }
                : null)
            .Prepend(place)
            .OfType<DiagnosticRelatedInformation>()
            .ToList();

        return new Diagnostic
        {
            Severity = ToLsp(diagnostic.Severity),
            Code = diagnostic.Code,
            // A compiler code links to its section of the error reference; a linter's has none.
            CodeDescription = NyxCompiler.DiagnosticCodes.referenceUrl(diagnostic.Code)?.Value is { } url
                ? new CodeDescription { Href = new Uri(url) }
                : null,
            Source = Source,
            Message = message ?? diagnostic.Message,
            Range = range,
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

    private static Parser.Program.SourceSpan? Some(FSharpOption<Parser.Program.SourceSpan> span) =>
        FSharpOption<Parser.Program.SourceSpan>.get_IsSome(span) ? span.Value : null;

    private static bool IsDocumentFile(string file, OpenDocument document) =>
        string.Equals(file, document.FileName, PathComparison);

    /// <summary>A file as a message names it: relative to the document's folder, if both are on disk.</summary>
    private static string Shown(string file, OpenDocument document) =>
        Path.IsPathRooted(file) && Path.GetDirectoryName(document.FileName) is { Length: > 0 } folder
            ? Path.GetRelativePath(folder, file)
            : file;

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
