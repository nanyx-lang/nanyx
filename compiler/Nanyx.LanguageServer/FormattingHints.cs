using Microsoft.Extensions.Logging;
using Nanyx.LanguageServer.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer;

/// <summary>A block of a document's lines that formatting changes: its diagnostic, and the edit that formats it.</summary>
public sealed record FormattingHint(Diagnostic Diagnostic, TextEdit Edit);

/// <summary>
/// Where a document differs from its formatted text, as one Hint diagnostic per block of whole lines that formatting
/// changes, each with the edit that formats it (<see cref="CodeActionHandler"/> offers it as a quick fix). It needs
/// only a parse, so a document with type errors has them; one that does not parse, or that the formatter's safety check
/// refuses, has none. The language server publishes them with the compiler's diagnostics unless
/// <see cref="ServerSettings.FormattingHints"/> is off.
/// </summary>
public static class FormattingHints
{
    /// <summary>The <c>code</c> of every formatting diagnostic.</summary>
    public const string Code = "format";

    public const string Message = "Not formatted: `nanyx format` changes these lines";

    /// <summary>
    /// The formatting hints of <paramref name="document"/>. Read them from
    /// <see cref="OpenDocument.FormattingHints"/>, which works them out once per text.
    /// </summary>
    internal static IReadOnlyList<FormattingHint> Of(DocumentUri uri, OpenDocument document, ILogger logger)
    {
        if (FormattingHandler.FormattedText(document, uri, logger) is not { } formatted)
        {
            return [];
        }

        var lines = document.Positions.NormalizedText.Split('\n');
        var formattedLines = formatted.ReplaceLineEndings("\n").Split('\n');
        var lineEnding = document.Text.Contains("\r\n") ? "\r\n" : "\n";
        return LineDiff.Hunks(lines, formattedLines).Select(hunk => Hint(hunk, lines, lineEnding)).ToList();
    }

    private static FormattingHint Hint(LineHunk hunk, string[] lines, string lineEnding)
    {
        // A text split on its line breaks has one line more than it has breaks: the last, which is empty when the
        // text ends in one. A block that reaches it ends at the end of the text, where there is no break to keep.
        var toEnd = hunk.End == lines.Length;
        var edit = new TextEdit
        {
            Range = toEnd
                ? new Range(hunk.Start, 0, lines.Length - 1, lines[^1].Length)
                : new Range(hunk.Start, 0, hunk.End, 0),
            NewText = toEnd
                ? string.Join(lineEnding, hunk.NewLines)
                : string.Concat(hunk.NewLines.Select(line => line + lineEnding))
        };

        // Lines added between two kept lines are shown on the line after them, or before them at the end.
        var (first, last) = hunk.Start < hunk.End
            ? (hunk.Start, hunk.End - 1)
            : hunk.Start < lines.Length ? (hunk.Start, hunk.Start) : (hunk.Start - 1, hunk.Start - 1);
        var diagnostic = new Diagnostic
        {
            Severity = DiagnosticSeverity.Hint,
            Code = Code,
            Source = DiagnosticsPublisher.Source,
            Message = Message,
            Range = new Range(first, 0, last, lines[last].Length)
        };
        return new FormattingHint(diagnostic, edit);
    }
}
