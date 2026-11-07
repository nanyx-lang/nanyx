namespace Nanyx.LanguageServer;

/// <summary>
/// What surrounds the cursor in the text being typed, read from the characters alone, since the text being typed
/// rarely parses. Offsets are into the text as the parser sees it (see <see cref="SourcePositions"/>).
/// </summary>
public static class CompletionContext
{
    /// <summary>Where the name ending at <paramref name="offset"/> starts: <paramref name="offset"/> itself if none does.</summary>
    public static int WordStart(string text, int offset)
    {
        var start = offset;
        while (start > 0 && IsNameCharacter(text[start - 1]))
        {
            start--;
        }

        return start;
    }

    /// <summary>
    /// Whether a name in scope can be written at <paramref name="wordStart"/>, the start of the word being typed: in
    /// code (not a comment, nor a string outside an interpolation hole), not a number, not a member after a dot or
    /// a tag after a <c>#</c>, and not the new name a <c>def</c> is declaring.
    /// </summary>
    public static bool OffersNames(string text, int wordStart)
    {
        if (wordStart < text.Length && char.IsDigit(text[wordStart]))
        {
            return false;
        }

        if (wordStart > 0 && text[wordStart - 1] is '.' or '#')
        {
            return false;
        }

        var before = text[..wordStart].TrimEnd(' ', '\t');
        if (before.Length < wordStart && (EndsWithWord(before, "def") || EndsWithWord(before, "rec")))
        {
            return false;
        }

        return IsCode(text, wordStart);
    }

    /// <summary>
    /// The names written before the dot that the word at <paramref name="wordStart"/> follows: <c>p</c> for
    /// <c>p.x</c>, <c>p</c> and <c>inner</c> for <c>p.inner.x</c>. <c>null</c> if the word does not follow a dot,
    /// the dot is not in code, or what is before it is not a name or a dotted name (a number, a call's result, a
    /// tag).
    /// </summary>
    public static IReadOnlyList<string>? MemberPath(string text, int wordStart)
    {
        if (wordStart == 0 || text[wordStart - 1] != '.')
        {
            return null;
        }

        var start = wordStart - 1;
        while (start > 0 && (IsNameCharacter(text[start - 1]) || text[start - 1] == '.'))
        {
            start--;
        }

        var path = text[start..(wordStart - 1)].Split('.');
        var isName = path.All(segment => segment.Length > 0) && !char.IsDigit(path[0][0]);
        var isTag = start > 0 && text[start - 1] == '#';
        return isName && !isTag && IsCode(text, wordStart) ? path : null;
    }

    /// <summary>
    /// Whether <paramref name="offset"/> is in code: not in a <c>--</c> comment, a <c>---</c> block comment, a
    /// <c>'raw'</c> string, or a <c>"string"</c> outside its <c>{interpolation}</c> holes.
    /// </summary>
    public static bool IsCode(string text, int offset)
    {
        // What the scan is inside, innermost last. In code, the count is of the braces opened in it: a `}` with none
        // open ends the interpolation hole the code is in.
        var frames = new Stack<(Frame Kind, int Braces)>();
        frames.Push((Frame.Code, 0));
        var index = 0;
        while (index < offset)
        {
            var (kind, braces) = frames.Peek();
            var character = text[index];
            switch (kind)
            {
                case Frame.Code when frames.Count == 1 && IsLineStart(text, index) && BlockCommentFence(text, index) is { } fence:
                    // A block comment runs to the next line that is the same fence.
                    var close = FindFence(text, LineEnd(text, index), fence);
                    if (close < 0 || close >= offset)
                    {
                        return false;
                    }

                    index = LineEnd(text, close);
                    continue;
                case Frame.Code when character == '-' && index + 1 < text.Length && text[index + 1] == '-':
                    var end = LineEnd(text, index);
                    if (end >= offset)
                    {
                        return false;
                    }

                    index = end;
                    continue;
                case Frame.Code when character == '"':
                    frames.Push((Frame.String, 0));
                    break;
                case Frame.Code when character == '\'':
                    frames.Push((Frame.RawString, 0));
                    break;
                case Frame.Code when character == '{':
                    frames.Pop();
                    frames.Push((Frame.Code, braces + 1));
                    break;
                case Frame.Code when character == '}':
                    frames.Pop();
                    if (braces > 0)
                    {
                        frames.Push((Frame.Code, braces - 1));
                    }
                    else if (frames.Count == 0)
                    {
                        // An unmatched brace in code at the top: still code.
                        frames.Push((Frame.Code, 0));
                    }

                    break;
                case Frame.String when character == '\\':
                    index++;
                    break;
                case Frame.String when (character == '{' || character == '}') && index + 1 < text.Length && text[index + 1] == character:
                    index++;
                    break;
                case Frame.String when character == '{':
                    frames.Push((Frame.Code, 0));
                    break;
                case Frame.String when character == '"':
                case Frame.RawString when character == '\'':
                    frames.Pop();
                    break;
            }

            index++;
        }

        return frames.Peek().Kind == Frame.Code;
    }

    private enum Frame
    {
        Code,
        String,
        RawString
    }

    private static bool IsNameCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';

    private static bool EndsWithWord(string text, string word) =>
        text.EndsWith(word, StringComparison.Ordinal)
        && (text.Length == word.Length || !IsNameCharacter(text[^(word.Length + 1)]));

    private static bool IsLineStart(string text, int index) => index == 0 || text[index - 1] == '\n';

    private static int LineEnd(string text, int index)
    {
        var end = text.IndexOf('\n', index);
        return end < 0 ? text.Length : end;
    }

    /// <summary>The dashes of the line starting at <paramref name="lineStart"/> if it is a block comment's fence: three or more dashes alone.</summary>
    private static string? BlockCommentFence(string text, int lineStart)
    {
        var line = text[lineStart..LineEnd(text, lineStart)].Trim();
        return line.Length >= 3 && line.All(character => character == '-') ? line : null;
    }

    /// <summary>The start of the first line from <paramref name="from"/> that is the fence <paramref name="fence"/>, or -1.</summary>
    private static int FindFence(string text, int from, string fence)
    {
        var lineStart = from < text.Length ? from + 1 : text.Length;
        while (lineStart < text.Length)
        {
            if (BlockCommentFence(text, lineStart) == fence)
            {
                return lineStart;
            }

            lineStart = LineEnd(text, lineStart) + 1;
        }

        return -1;
    }
}
