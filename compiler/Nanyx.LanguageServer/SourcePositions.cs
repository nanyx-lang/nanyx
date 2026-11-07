using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer;

/// <summary>
/// Converts between LSP positions (zero-based line and UTF-16 character) and the character offsets the compiler's
/// spans use. The parser sees the source with every line ending made a single <c>\n</c>, so offsets count into
/// that normalised text; lines and characters are the same in both, because .NET strings are UTF-16 too.
/// </summary>
public sealed class SourcePositions
{
    private readonly string _normalized;
    private readonly List<int> _lineStarts = [0];

    public SourcePositions(string text)
    {
        _normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        for (var index = 0; index < _normalized.Length; index++)
        {
            if (_normalized[index] == '\n')
            {
                _lineStarts.Add(index + 1);
            }
        }
    }

    /// <summary>The text with its line endings normalised, as the parser sees it: what offsets count into.</summary>
    public string NormalizedText => _normalized;

    /// <summary>The offset of <paramref name="position"/>, clamped to its line (and the line to the text).</summary>
    public int ToOffset(Position position)
    {
        var line = Math.Clamp(position.Line, 0, _lineStarts.Count - 1);
        var lineStart = _lineStarts[line];
        var lineEnd = line + 1 < _lineStarts.Count ? _lineStarts[line + 1] - 1 : _normalized.Length;
        return lineStart + Math.Clamp(position.Character, 0, lineEnd - lineStart);
    }

    /// <summary>The text of a line without its line ending, or empty for a line past the end.</summary>
    public string LineText(int line)
    {
        if (line < 0 || line >= _lineStarts.Count)
        {
            return string.Empty;
        }

        var lineEnd = line + 1 < _lineStarts.Count ? _lineStarts[line + 1] - 1 : _normalized.Length;
        return _normalized[_lineStarts[line]..lineEnd];
    }

    /// <summary>The position of <paramref name="offset"/>.</summary>
    public Position ToPosition(int offset)
    {
        offset = Math.Clamp(offset, 0, _normalized.Length);
        var line = _lineStarts.BinarySearch(offset);
        if (line < 0)
        {
            // Not a line start: the line is the one starting before the insertion point.
            line = ~line - 1;
        }

        return new Position(line, offset - _lineStarts[line]);
    }

    /// <summary>The range from <paramref name="start"/> to <paramref name="end"/> (one past the last character).</summary>
    public Range ToRange(int start, int end) => new(ToPosition(start), ToPosition(end));
}
