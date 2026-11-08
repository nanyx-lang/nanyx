using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NyxCompiler;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Nanyx.LanguageServer;

/// <summary>An open document: its text and the result of compiling that text.</summary>
/// <param name="Text">The editor's buffer, which is usually ahead of the saved file.</param>
/// <param name="FileName">
/// The file the compiler's spans name for this document: its full path, or empty for a document not on disk.
/// </param>
/// <param name="Compiled">What the compiler made of <paramref name="Text"/>; <c>null</c> if the compiler threw.</param>
/// <param name="CompilerFailure">The exception the compiler threw, if it did.</param>
/// <param name="PreviousGood">
/// The last earlier text of this document that type-checked, if this one did not: see <see cref="LastGood"/>.
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

    /// <summary>Converts positions in <see cref="Text"/>, built once per change rather than on every request.</summary>
    public SourcePositions Positions { get; } = new(Text);

    /// <summary>
    /// What <see cref="Text"/> declares, as a tree (see <c>NyxCompiler.Outline</c>), or <c>null</c> if it does not
    /// parse. It needs only a parse, so a text that does not type-check has one. Parsed once, when first asked for.
    /// </summary>
    public Outline.OutlineItem[]? Outline =>
        (_outline ??= new Lazy<Outline.OutlineItem[]?>(() => NyxCompiler.Outline.ofSource(FileName, Text)?.Value.ToArray())).Value;

    /// <summary>Whether <see cref="Compiled"/> type-checked, and so has the types and symbols editor features read.</summary>
    public bool TypeChecked => Compiled is { Syntax: not null, Typed: not null };

    /// <summary>
    /// The text of this document that hover and type hints read: this one if it type-checked, or else the last one
    /// that did, since while the user is typing the text mostly does not. <c>null</c> if no text of it has.
    /// Diagnostics always come from this text's own compile.
    /// </summary>
    public OpenDocument? LastGood => TypeChecked ? this : PreviousGood;

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
    /// text of the document has type-checked.
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
/// Every document the client has open, keyed by URI, with the last compile of its text and the last one that
/// type-checked. A document is compiled once per change, and diagnostics, hover and type hints all read that result.
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

    /// <summary>Compiles the full text of a document after it was opened or changed, and records both.</summary>
    public OpenDocument Update(DocumentUri uri, string text)
    {
        var document = Compile(uri, text);
        if (!document.TypeChecked && TryGet(uri)?.LastGood is { } lastGood)
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

    private OpenDocument Compile(DocumentUri uri, string text)
    {
        // A file on disk gets its imports resolved relative to itself; anything else (an untitled buffer, say)
        // can still import the standard library. The compiler names a file by its full path.
        var path = uri.Scheme == "file" ? Path.GetFullPath(uri.GetFileSystemPath()) : null;
        try
        {
            var directives = Script.read(path ?? string.Empty, text);
            var analyzers = _settings.AnalyzerOptionsFor(path, directives);
            var result = path is null ? Compiler.compileWith(analyzers, text) : Compiler.compileSourceWith(analyzers, path, text);
            return new OpenDocument(text, path ?? string.Empty, Script.withDirectives(directives, result), null);
        }
        catch (Exception ex)
        {
            // A compiler bug must not take the server down with it: it is reported as a diagnostic instead.
            _logger.LogError(ex, "The compiler failed on {Uri}", uri);
            return new OpenDocument(text, path ?? string.Empty, null, ex);
        }
    }
}
