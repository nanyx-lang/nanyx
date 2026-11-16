namespace Nanyx.LanguageServer;

/// <summary>
/// A block of whole lines that a diff replaces: the lines from <paramref name="Start"/> up to, not including,
/// <paramref name="End"/> of the text before, with <paramref name="NewLines"/> in their place. An empty block
/// (<c>Start == End</c>) inserts its lines before line <paramref name="Start"/>.
/// </summary>
public sealed record LineHunk(int Start, int End, IReadOnlyList<string> NewLines);

/// <summary>The blocks of lines that differ between two texts, by Myers' algorithm.</summary>
public static class LineDiff
{
    /// <summary>
    /// Past this many lines added or removed, the diff stops looking for the fewest and gives one block from the first
    /// line that differs to the last: its search keeps a row of positions per line changed, so it would cost time and
    /// memory in the square of them, and a text that different is better highlighted whole.
    /// </summary>
    public const int MaxChanges = 1000;

    /// <summary>
    /// The blocks of <paramref name="before"/> that differ from <paramref name="after"/>, in order, each with the lines
    /// of <paramref name="after"/> that replace it. Lines that match between two blocks are left out of both, so the
    /// blocks are as few lines as can be, up to <see cref="MaxChanges"/>.
    /// </summary>
    public static IReadOnlyList<LineHunk> Hunks(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var prefix = 0;
        while (prefix < before.Count && prefix < after.Count && before[prefix] == after[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < before.Count - prefix && suffix < after.Count - prefix && before[^(suffix + 1)] == after[^(suffix + 1)])
        {
            suffix++;
        }

        var a = before.Skip(prefix).Take(before.Count - prefix - suffix).ToList();
        var b = after.Skip(prefix).Take(after.Count - prefix - suffix).ToList();
        if (a.Count == 0 && b.Count == 0)
        {
            return [];
        }

        var matches = Matches(a, b);
        if (matches is null)
        {
            return [new LineHunk(prefix, prefix + a.Count, b)];
        }

        var hunks = new List<LineHunk>();
        var (nextA, nextB) = (0, 0);
        foreach (var (x, y) in matches.Append((a.Count, b.Count)))
        {
            if (x > nextA || y > nextB)
            {
                hunks.Add(new LineHunk(prefix + nextA, prefix + x, b.GetRange(nextB, y - nextB)));
            }

            (nextA, nextB) = (x + 1, y + 1);
        }

        return hunks;
    }

    /// <summary>
    /// The lines of <paramref name="a"/> and <paramref name="b"/> that a shortest edit from one to the other keeps, as
    /// pairs of their indexes in order, or <c>null</c> if it changes more than <see cref="MaxChanges"/> lines.
    /// </summary>
    private static List<(int X, int Y)>? Matches(List<string> a, List<string> b)
    {
        // `furthest[k]` is how far along `a` the furthest path with `d` changes reaches on diagonal `k` (x - y = k). The
        // row for each `d`, of diagonals -d to d, is kept to walk the path back.
        var limit = Math.Min(a.Count + b.Count, MaxChanges);
        var offset = limit + 1;
        var furthest = new int[2 * limit + 3];
        var rows = new List<int[]>();
        for (var d = 0; d <= limit; d++)
        {
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && furthest[offset + k - 1] < furthest[offset + k + 1])
                    ? furthest[offset + k + 1]
                    : furthest[offset + k - 1] + 1;
                var y = x - k;
                while (x < a.Count && y < b.Count && a[x] == b[y])
                {
                    (x, y) = (x + 1, y + 1);
                }

                furthest[offset + k] = x;
                if (x >= a.Count && y >= b.Count)
                {
                    rows.Add(furthest[(offset - d)..(offset + d + 1)]);
                    return WalkBack(rows, a.Count, b.Count);
                }
            }

            rows.Add(furthest[(offset - d)..(offset + d + 1)]);
        }

        return null;
    }

    /// <summary>The lines kept on the path whose rows are <paramref name="rows"/>, walked back from its end.</summary>
    private static List<(int X, int Y)> WalkBack(List<int[]> rows, int x, int y)
    {
        var matches = new List<(int X, int Y)>();
        void Keep(int fromX)
        {
            while (x > fromX)
            {
                (x, y) = (x - 1, y - 1);
                matches.Add((x, y));
            }
        }

        for (var d = rows.Count - 1; d > 0; d--)
        {
            // Row d - 1 holds diagonals -(d - 1) to d - 1, from index 0.
            var row = rows[d - 1];
            int At(int diagonal) => row[diagonal + d - 1];
            var k = x - y;
            var down = k == -d || (k != d && At(k - 1) < At(k + 1));
            var previousK = down ? k + 1 : k - 1;
            var previousX = At(previousK);
            // The change moved down (a line added) or right (a line removed), and the lines after it were kept.
            Keep(down ? previousX : previousX + 1);
            (x, y) = (previousX, previousX - previousK);
        }

        Keep(0);
        matches.Reverse();
        return matches;
    }
}
