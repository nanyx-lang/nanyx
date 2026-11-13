using System.Text;

/// <summary>
/// The source a diagnostic is about, as the CLI shows it under the diagnostic's line: the line its span starts on, after
/// its number, and a line of carets under the span.
/// <code>
///   |
/// 3 | def total = count + "one"
///   |                     ^^^^^
/// </code>
/// A span that runs onto later lines is underlined to the end of its first line.
/// </summary>
internal static class SourceExcerpt
{
    /// <summary>
    /// The excerpt's lines for <paramref name="span"/>, in <paramref name="source"/>, its file's text: the gutter, the
    /// line and the underline, each without a line break, with the carets given to <paramref name="marked"/> (to colour
    /// them). None when the span is not in the source.
    /// </summary>
    public static IReadOnlyList<string> Lines(Parser.Program.SourceSpan span, string source, Func<string, string> marked)
    {
        // The compiler's offsets are into the source with its line endings normalised to `\n`.
        source = source.Replace("\r\n", "\n").Replace('\r', '\n');
        if (span.Start < 0 || span.Start > source.Length)
        {
            return [];
        }

        var lineStart = span.Start == 0 ? 0 : source.LastIndexOf('\n', span.Start - 1) + 1;
        var lineEnd = source.IndexOf('\n', span.Start);
        if (lineEnd < 0)
        {
            lineEnd = source.Length;
        }

        var line = source[lineStart..lineEnd];
        var number = (span.StartLine + 1).ToString();
        var gutter = new string(' ', number.Length);
        // The carets line up under the span whatever the line indents with: each tab before it is kept as a tab.
        var indent = new StringBuilder();
        foreach (var c in source.AsSpan(lineStart, span.Start - lineStart))
        {
            indent.Append(c == '\t' ? '\t' : ' ');
        }

        var width = Math.Max(1, Math.Min(span.End, lineEnd) - span.Start);
        return
        [
            $"{gutter} |",
            $"{number} | {line}".TrimEnd(),
            $"{gutter} | {indent}{marked(new string('^', width))}",
        ];
    }

    /// <summary>
    /// The text of the file <paramref name="file"/> a span names: a file on disk, by its full path, or a standard-library
    /// module, by its import path (<c>nanyx/list.nyx</c>). <c>null</c> when it can't be read.
    /// </summary>
    public static string? SourceOf(string file)
    {
        if (string.IsNullOrEmpty(file))
        {
            return null;
        }

        if (Path.IsPathRooted(file))
        {
            try
            {
                return File.ReadAllText(file);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        var standard = NyxCompiler.StandardLibrary.tryLoadFile(file);
        return standard is null ? null : standard.Value;
    }
}
