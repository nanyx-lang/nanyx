using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NyxCompiler;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Nanyx.LanguageServer;

/// <summary>An open document: its text and the result of compiling that text.</summary>
/// <param name="Text">The editor's buffer, which is usually ahead of the saved file.</param>
/// <param name="FileName">
/// The file the compiler's spans name for this document: its full path, or empty for a document not on disk.
/// </param>
/// <param name="Compiled">
/// What the compiler made of <paramref name="Text"/>; <c>null</c> if the compiler threw, or if it has not compiled the
/// text yet (see <see cref="CompileScheduler"/>).
/// </param>
/// <param name="CompilerFailure">The exception the compiler threw, if it did.</param>
/// <param name="PreviousGood">
/// The last earlier text of this document that was typed, if this one was not: see <see cref="LastGood"/>.
/// </param>
public sealed record OpenDocument(
    string Text,
    string FileName,
    CompileResult? Compiled,
    Exception? CompilerFailure,
    OpenDocument? PreviousGood = null)
{
    private EditedRegion? _editsSinceLastGood;
    private Lazy<Outline.OutlineItem[]?>? _outline;
    private Lazy<Parser.Program.DocCommentParsing.FileDocComments>? _docComments;
    private Lazy<IReadOnlyList<OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic>>? _published;

    /// <summary>Converts positions in <see cref="Text"/>, built once per change rather than on every request.</summary>
    public SourcePositions Positions { get; } = new(Text);

    /// <summary>
    /// What <see cref="Text"/> declares, as a tree (see <c>NyxCompiler.Outline</c>), or <c>null</c> if it does not
    /// parse. It needs only a parse, so a text that does not type-check has one. Parsed once, when first asked for.
    /// </summary>
    public Outline.OutlineItem[]? Outline =>
        (_outline ??= new Lazy<Outline.OutlineItem[]?>(() => NyxCompiler.Outline.ofSource(FileName, Text)?.Value.ToArray())).Value;

    /// <summary>
    /// The doc comments in <see cref="Text"/>, each with what it documents (see <c>NyxCompiler.Documentation</c>).
    /// Parsed once, when first asked for.
    /// </summary>
    public Parser.Program.DocCommentParsing.FileDocComments DocComments =>
        (_docComments ??= new Lazy<Parser.Program.DocCommentParsing.FileDocComments>(() => Documentation.ofSource(FileName, Text))).Value;

    /// <summary>
    /// The LSP diagnostics of this text's compile, as published on <paramref name="uri"/>, the document's own (see
    /// <see cref="DiagnosticsPublisher.Convert"/>). Converted once, when first asked for, so that the info view and code
    /// actions, asked on every move of the cursor, neither convert them again nor read other files from disk.
    /// </summary>
    public IReadOnlyList<OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic> PublishedDiagnostics(DocumentUri uri) =>
        (_published ??= new Lazy<IReadOnlyList<OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic>>(
            () => DiagnosticsPublisher.Convert(uri, this))).Value;

    /// <summary>
    /// Reads the doc comments of the names in <see cref="Compiled"/>, for a request at <paramref name="offset"/>,
    /// with each link in them made a link to where the name it names is declared (see
    /// <see cref="SourceLocations.LinkTo"/>). The document must have been typed.
    /// </summary>
    public Documentation.Reader DocReader(DocumentUri uri, int offset, StandardLibrarySource standardLibrary) =>
        new(FileName, DocComments, Compiled!, offset, span => SourceLocations.LinkTo(uri, this, span, standardLibrary)!);

    /// <summary>
    /// Whether <see cref="Compiled"/> has the types and symbols editor features read. A text with type or name errors
    /// has them: the type checker types what did check, and gives what did not the type <c>???</c>. A text that does
    /// not parse, or whose imports do not check, has none.
    /// </summary>
    public bool Typed => Compiled is { Syntax: not null, Typed: not null };

    /// <summary>
    /// The text of this document that hover, type hints and completion read: this one if it was typed, or else the
    /// last one that was, since while the user is typing the text often does not parse. <c>null</c> if no text of it
    /// has been. Diagnostics always come from this text's own compile.
    /// </summary>
    public OpenDocument? LastGood => Typed ? this : PreviousGood;

    /// <summary>
    /// The offset in <see cref="LastGood"/>'s text of the character at <paramref name="offset"/> in this one, or
    /// <c>null</c> if there is no last good text, or the character is in a top-level item edited since: what that
    /// compile says about the item may no longer be true.
    /// </summary>
    public int? ToLastGood(int offset)
    {
        if (LastGood is not { } lastGood)
        {
            return null;
        }

        if (ReferenceEquals(lastGood, this))
        {
            return offset;
        }

        var edits = EditsSinceLastGood(lastGood);
        return edits.ToBefore(offset) is { } before && !InEditedItem(lastGood, before, before, edits) ? before : null;
    }

    /// <summary>
    /// The offsets in this text of a span in <see cref="LastGood"/>'s, or <c>null</c> if the span, or the top-level
    /// item it is in, has been edited since.
    /// </summary>
    public (int Start, int End)? FromLastGood(int start, int end)
    {
        if (LastGood is not { } lastGood)
        {
            return null;
        }

        if (ReferenceEquals(lastGood, this))
        {
            return (start, end);
        }

        var edits = EditsSinceLastGood(lastGood);
        return InEditedItem(lastGood, start, end, edits) ? null : edits.ToAfter(start, end);
    }

    /// <summary>
    /// The offset in <see cref="LastGood"/>'s text nearest to <paramref name="offset"/> in this one, for asking
    /// which names are in scope there rather than what anything's type is: an offset in text edited since maps to
    /// where the edit starts, which is in the same scope unless the edit opened or closed one. <c>null</c> if no
    /// text of the document has been typed.
    /// </summary>
    public int? NearestInLastGood(int offset)
    {
        if (LastGood is not { } lastGood)
        {
            return null;
        }

        if (ReferenceEquals(lastGood, this))
        {
            return offset;
        }

        var edits = EditsSinceLastGood(lastGood);
        return edits.ToBefore(offset) ?? edits.Start;
    }

    private EditedRegion EditsSinceLastGood(OpenDocument lastGood) =>
        _editsSinceLastGood ??= EditedRegion.Between(lastGood.Positions.NormalizedText, Positions.NormalizedText);

    /// <summary>Whether the top-level item of <paramref name="lastGood"/> around a span has been edited since.</summary>
    private static bool InEditedItem(OpenDocument lastGood, int start, int end, EditedRegion edits) =>
        lastGood.Compiled!.Syntax.Value.Any(item =>
            item.Span.Start >= 0
            && item.Span.Start <= start
            && end <= item.Span.End
            && edits.Touches(item.Span.Start, item.Span.End));
}

/// <summary>
/// Every document the client has open, keyed by URI, with the last compile of its text and the last one that was
/// typed. A document is compiled when it is opened, and after a change once the user pauses (see
/// <see cref="CompileScheduler"/>); diagnostics, hover and type hints all read that result.
/// </summary>
public sealed class DocumentStore
{
    private readonly ConcurrentDictionary<DocumentUri, OpenDocument> _documents = new();
    private readonly ILogger<DocumentStore> _logger;
    private readonly ServerSettings _settings;

    public DocumentStore(ILogger<DocumentStore> logger, ServerSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    /// <summary>
    /// Records a document's new text before it is compiled, so that requests about it read positions in this text,
    /// and what they need from a compile from the last text that was typed (see <see cref="OpenDocument.LastGood"/>).
    /// </summary>
    public void Edit(DocumentUri uri, string text) => Record(uri, new OpenDocument(text, FileNameOf(uri), null, null));

    /// <summary>Records a compiled text of a document, as its latest.</summary>
    public OpenDocument Record(DocumentUri uri, OpenDocument document)
    {
        if (!document.Typed && TryGet(uri)?.LastGood is { } lastGood)
        {
            document = document with { PreviousGood = lastGood };
        }

        _documents[uri] = document;
        return document;
    }

    /// <summary>Forgets a document the client has closed.</summary>
    public void Remove(DocumentUri uri) => _documents.TryRemove(uri, out _);

    /// <summary>An open document, or <c>null</c> if the client has not opened it.</summary>
    public OpenDocument? TryGet(DocumentUri uri) => _documents.TryGetValue(uri, out var document) ? document : null;

    /// <summary>The file the compiler's spans name for a document: its full path, or empty if it is not on disk.</summary>
    private static string FileNameOf(DocumentUri uri) =>
        uri.Scheme == "file" ? Path.GetFullPath(uri.GetFileSystemPath()) : string.Empty;

    /// <summary>
    /// Compiles a text of a document, without recording it, and logs how long it took, with each module's time when
    /// <see cref="ServerSettings.Timings"/> asks for them.
    /// </summary>
    public OpenDocument Compile(DocumentUri uri, string text)
    {
        _settings.CompileStarted?.Invoke(uri);
        // A file on disk gets its imports resolved through its package's modules if it is a source file of one, or
        // else relative to itself; anything else (an untitled buffer, say) can still import the standard library. A
        // file of the standard library is checked as the library module it is, as a build of the library checks it.
        // The compiler names a file by its full path.
        var path = uri.Scheme == "file" ? FileNameOf(uri) : null;
        try
        {
            var directives = Script.read(path ?? string.Empty, text);
            var linters = _settings.LinterOptionsFor(path, directives);
            var stopwatch = Stopwatch.StartNew();
            var timings = new CompileTimings();
            var result = path is null
                ? Compiler.compileReporting(timings, linters, text)
                : Compiler.compileStandardLibraryFileReporting(timings, linters, path, text)?.Value
                    ?? PackageModule.compileSourceReporting(timings, linters, path, text);
            LogCompiled(uri, stopwatch.Elapsed, timings);
            return new OpenDocument(text, path ?? string.Empty, Script.withDirectives(directives, result), null);
        }
        catch (Exception ex)
        {
            // A compiler bug must not take the server down with it: it is reported as a diagnostic instead.
            _logger.LogError(ex, "The compiler failed on {Uri}", uri);
            return new OpenDocument(text, path ?? string.Empty, null, ex);
        }
    }

    /// <summary>
    /// Logs that a document compiled in <paramref name="elapsed"/>: one line, for the editor's output channel to show
    /// how long each compile takes, with the modules' times under it when the server was started with
    /// <c>--timings</c>.
    /// </summary>
    private void LogCompiled(DocumentUri uri, TimeSpan elapsed, CompileTimings timings)
    {
        if (_settings.Timings)
        {
            var tree = ModuleCheckModule.tree(Microsoft.FSharp.Core.FuncConvert.FromFunc((string name) => name), timings.Checks);
            _logger.LogInformation("Compiled {Uri} in {Seconds:0.000}s\n{Tree}", uri, elapsed.TotalSeconds, tree);
        }
        else
        {
            _logger.LogInformation("Compiled {Uri} in {Seconds:0.000}s", uri, elapsed.TotalSeconds);
        }
    }
}
