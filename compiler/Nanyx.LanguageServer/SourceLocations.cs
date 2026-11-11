using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer;

/// <summary>Where a span the compiler reports is, as a location the editor can open.</summary>
internal static class SourceLocations
{
    /// <summary>
    /// The location of <paramref name="span"/>, a name: in <paramref name="document"/> (at <paramref name="uri"/>),
    /// in another source file, or in the standard library, opened from the copy of its source on disk. Null when
    /// the span is in no file the editor can open.
    /// </summary>
    public static Location? Of(
        DocumentUri uri,
        OpenDocument document,
        Parser.Program.SourceSpan span,
        StandardLibrarySource standardLibrary)
    {
        if (span.File == document.FileName)
        {
            return new Location { Uri = uri, Range = document.Positions.ToRange(span.Start, span.End) };
        }

        // Another source file. A standard-library module is named by its import path instead ("nanyx/text.nyx").
        var path = Path.IsPathRooted(span.File) ? span.File : standardLibrary.TryGetPath(span.File);
        if (path is null)
        {
            return null;
        }

        // A name is on one line, so its range follows from where it starts.
        return new Location
        {
            Uri = DocumentUri.FromFileSystemPath(path),
            Range = new Range(span.StartLine, span.StartColumn, span.StartLine, span.StartColumn + span.End - span.Start)
        };
    }

    /// <summary>
    /// A Markdown link target for <paramref name="span"/>, a name, as <see cref="Of"/> finds its location: the file's
    /// URI with the line and column, which an editor opens at that place (<c>file:///a.nyx#L3,5</c>). Null when the
    /// span is in no file the editor can open.
    /// </summary>
    public static string? LinkTo(
        DocumentUri uri,
        OpenDocument document,
        Parser.Program.SourceSpan span,
        StandardLibrarySource standardLibrary) =>
        Of(uri, document, span, standardLibrary) is { } location
            ? $"{location.Uri}#L{location.Range.Start.Line + 1},{location.Range.Start.Character + 1}"
            : null;
}
