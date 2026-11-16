using Xunit;

namespace Nanyx.LanguageServer.Tests;

/// <summary>The blocks of lines that differ between two texts, which formatting hints show.</summary>
public sealed class LineDiffTests
{
    private static List<(int Start, int End, string NewLines)> Hunks(string before, string after) =>
        LineDiff.Hunks(before.Split('\n'), after.Split('\n'))
            .Select(hunk => (hunk.Start, hunk.End, string.Join("|", hunk.NewLines)))
            .ToList();

    [Fact]
    public void The_same_text_has_no_blocks() =>
        Assert.Empty(Hunks("a\nb\nc\n", "a\nb\nc\n"));

    [Fact]
    public void A_changed_line_is_a_block_of_its_own() =>
        Assert.Equal([(1, 2, "B")], Hunks("a\nb\nc\n", "a\nB\nc\n"));

    [Fact]
    public void Lines_kept_between_changes_separate_their_blocks() =>
        Assert.Equal([(0, 1, "A"), (3, 4, "D")], Hunks("a\nb\nc\nd\ne", "A\nb\nc\nD\ne"));

    [Fact]
    public void Lines_added_are_an_empty_block_before_the_line_they_go_before() =>
        Assert.Equal([(1, 1, "x|y")], Hunks("a\nb\n", "a\nx\ny\nb\n"));

    [Fact]
    public void Lines_removed_are_a_block_with_no_new_lines() =>
        Assert.Equal([(1, 3, "")], Hunks("a\nx\ny\nb", "a\nb"));

    [Fact]
    public void A_block_can_take_out_lines_and_put_others_in() =>
        Assert.Equal([(1, 3, "q"), (4, 4, "r")], Hunks("a\nb\nc\nd\ne", "a\nq\nd\nr\ne"));

    [Fact]
    public void Lines_the_texts_start_and_end_with_are_left_out_even_where_they_repeat() =>
        Assert.Equal([(2, 2, "}|x")], Hunks("{\n}\n{\n}", "{\n}\n}\nx\n{\n}"));

    [Fact]
    public void Texts_too_different_are_one_block_from_the_first_difference_to_the_last()
    {
        var before = Enumerable.Range(0, LineDiff.MaxChanges + 2).Select(i => $"a{i}").Prepend("same").Append("end").ToList();
        var after = Enumerable.Range(0, LineDiff.MaxChanges + 2).Select(i => $"b{i}").Prepend("same").Append("end").ToList();

        var hunk = Assert.Single(LineDiff.Hunks(before, after));
        Assert.Equal((1, before.Count - 1), (hunk.Start, hunk.End));
        Assert.Equal(after[1..^1], hunk.NewLines);
    }
}
