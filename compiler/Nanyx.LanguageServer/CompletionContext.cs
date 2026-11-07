namespace Nanyx.LanguageServer;

/// <summary>An import path written up to the cursor.</summary>
/// <param name="IsFile">Whether it is a file's path, in quotes (<c>"util.nyx"</c>), rather than a module's (<c>nanyx/text</c>).</param>
/// <param name="Written">What is written of it so far, without the opening quote.</param>
public sealed record ImportPath(bool IsFile, string Written);

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
    /// The import path being written at <paramref name="cursor"/>, if the cursor is on an item of an <c>import</c>:
    /// on its line, or inside its <c>import ( ... )</c> block. <c>null</c> elsewhere, and on an import's alias.
    /// </summary>
    public static ImportPath? ImportPathAt(string text, int cursor)
    {
        if (ImportItemsStart(text, cursor) is not { } itemsStart)
        {
            return null;
        }

        var items = text[itemsStart..cursor];
        if (items.Count(character => character == '"') % 2 == 1)
        {
            return new ImportPath(IsFile: true, Written: items[(items.LastIndexOf('"') + 1)..]);
        }

        var start = items.Length;
        while (start > 0 && !char.IsWhiteSpace(items[start - 1]) && items[start - 1] != '(')
        {
            start--;
        }

        var written = items[start..];
        var before = items[..start].TrimEnd();
        var isAlias = EndsWithWord(before, "as");
        return written.Contains('"') || isAlias ? null : new ImportPath(IsFile: false, Written: written);
    }

    /// <summary>
    /// Where the items of the import the cursor is in start: after <c>import</c> on the cursor's own line, or after
    /// the <c>(</c> of an <c>import ( ... )</c> block not yet closed before the cursor.
    /// </summary>
    private static int? ImportItemsStart(string text, int cursor)
    {
        var lineStart = cursor == 0 ? 0 : text.LastIndexOf('\n', cursor - 1) + 1;
        while (true)
        {
            var line = text[lineStart..LineEnd(text, lineStart)];
            var indent = line.Length - line.TrimStart().Length;
            var keyword = lineStart + indent;
            if (line.TrimStart().StartsWith("import", StringComparison.Ordinal)
                && keyword + 6 <= text.Length
                && (keyword + 6 == text.Length || !IsNameCharacter(text[keyword + 6])))
            {
                var afterKeyword = keyword + 6;
                if (cursor < afterKeyword)
                {
                    return null;
                }

                var open = text.IndexOf('(', afterKeyword, LineEnd(text, lineStart) - afterKeyword);
                if (open < 0)
                {
                    // `import item`: only on its own line.
                    return cursor <= LineEnd(text, lineStart) ? afterKeyword : null;
                }

                return cursor > open && text.IndexOf(')', open, cursor - open) < 0 ? open + 1 : null;
            }

            // Only the lines of a block can come between the cursor and its `import (`: indented, or the `)`.
            if (lineStart == 0 || (lineStart < cursor && indent == 0 && line.Trim().Length > 0 && !line.Trim().StartsWith(')')))
            {
                return null;
            }

            // The line before: its own line ending is at `lineStart - 1`.
            lineStart = lineStart < 2 ? 0 : text.LastIndexOf('\n', lineStart - 2) + 1;
        }
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
    /// The names of the value piped into the word at <paramref name="wordStart"/>, if the word follows a pipe's
    /// <c>\</c>: <c>s</c> for <c>s \tr</c>, <c>p</c> and <c>inner</c> for <c>p.inner\</c>. The <c>\</c> may stand
    /// at the start of a line, piping what the lines before it end with. Empty when what is piped is not a name or a
    /// dotted name (a call's result, a literal, an earlier pipe). <c>null</c> if the word follows no <c>\</c>, or is
    /// not in code. Only the names just before the <c>\</c> are read, though a pipe takes all of the expression
    /// before it: in <c>a + b \f</c> they are <c>b</c>.
    /// </summary>
    public static IReadOnlyList<string>? PipedPath(string text, int wordStart)
    {
        var pipe = wordStart;
        while (pipe > 0 && text[pipe - 1] is ' ' or '\t')
        {
            pipe--;
        }

        if (pipe == 0 || text[pipe - 1] != '\\' || !IsCode(text, wordStart))
        {
            return null;
        }

        var end = pipe - 1;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        var start = end;
        while (start > 0 && (IsNameCharacter(text[start - 1]) || text[start - 1] == '.'))
        {
            start--;
        }

        var path = text[start..end].Split('.');
        var isName = start < end
            && path.All(segment => segment.Length > 0)
            && !char.IsDigit(path[0][0])
            && (start == 0 || text[start - 1] is not ('#' or '"' or '\''));
        return isName ? path : [];
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
