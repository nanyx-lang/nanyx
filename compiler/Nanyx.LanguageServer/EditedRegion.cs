namespace Nanyx.LanguageServer;

/// <summary>
/// Where a document's text differs from an earlier text of it, as one region: everything before the region and
/// everything after it is the same in both. The client sends the whole document on every change, so the edits
/// themselves are not known; several edits since the earlier text make one region spanning all of them. Offsets are
/// into the texts as the parser sees them (see <see cref="SourcePositions"/>).
/// </summary>
public sealed class EditedRegion
{
    private EditedRegion(int start, int beforeEnd, int afterEnd)
    {
        Start = start;
        BeforeEnd = beforeEnd;
        AfterEnd = afterEnd;
    }

    /// <summary>Where the region starts, the same in both texts.</summary>
    public int Start { get; }

    /// <summary>Where the region ends in the earlier text.</summary>
    public int BeforeEnd { get; }

    /// <summary>Where the region ends in the later text.</summary>
    public int AfterEnd { get; }

    /// <summary>Whether the texts are the same.</summary>
    public bool IsEmpty => Start == BeforeEnd && Start == AfterEnd;

    private int Shift => AfterEnd - BeforeEnd;

    /// <summary>The region between the common start and the common end of two texts.</summary>
    public static EditedRegion Between(string before, string after)
    {
        // The common end is taken first, so a line inserted or deleted whole is placed after the end of the line
        // before it rather than inside the next line, whose start ("def ") it usually shares: every line ends in
        // the same newline. The common start then may not reach into it, or a character would be counted twice.
        var shorter = Math.Min(before.Length, after.Length);
        var suffix = 0;
        while (suffix < shorter && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
        {
            suffix++;
        }

        var prefix = 0;
        while (prefix < shorter - suffix && before[prefix] == after[prefix])
        {
            prefix++;
        }

        return new EditedRegion(prefix, before.Length - suffix, after.Length - suffix);
    }

    /// <summary>
    /// The offset in the earlier text of the character at <paramref name="offset"/> in the later one, or <c>null</c>
    /// if that character is in the region.
    /// </summary>
    public int? ToBefore(int offset) =>
        offset < Start ? offset
        : offset >= AfterEnd ? offset - Shift
        : null;

    /// <summary>
    /// Where the span from <paramref name="start"/> to <paramref name="end"/> in the earlier text is in the later
    /// one, or <c>null</c> if the region <see cref="Touches"/> it. A span that starts where text was inserted moves
    /// after it; one that ends there stays before it.
    /// </summary>
    public (int Start, int End)? ToAfter(int start, int end) =>
        Touches(start, end)
            ? null
            : (start < Start ? start : start + Shift, end <= Start ? end : end + Shift);

    /// <summary>
    /// Whether the region changed any of the span from <paramref name="start"/> to <paramref name="end"/> in the
    /// earlier text, or inserted text strictly inside it. Text inserted at either end of a span leaves it as it was.
    /// </summary>
    public bool Touches(int start, int end) => !IsEmpty && start < BeforeEnd && Start < end;
}
