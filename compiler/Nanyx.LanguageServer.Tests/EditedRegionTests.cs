using Xunit;

namespace Nanyx.LanguageServer.Tests;

public sealed class EditedRegionTests
{
    [Fact]
    public void Offsets_before_and_after_an_insertion_map_back_and_the_inserted_text_does_not()
    {
        var edits = EditedRegion.Between("abc", "abXc");

        Assert.Equal(1, edits.ToBefore(1));
        Assert.Null(edits.ToBefore(2));
        Assert.Equal(2, edits.ToBefore(3));
    }

    [Fact]
    public void A_line_inserted_whole_leaves_the_line_after_it_unchanged_though_both_start_alike()
    {
        var edits = EditedRegion.Between("def a = 1\ndef b = 2\n", "def a = 1\ndef x = 3\ndef b = 2\n");

        Assert.Equal((0, 9), edits.ToAfter(0, 9));
        Assert.Equal((20, 29), edits.ToAfter(10, 19));
    }

    [Fact]
    public void A_span_with_a_changed_character_is_touched_and_has_no_place_after()
    {
        var edits = EditedRegion.Between("def a = 1", "def a = 2");

        Assert.True(edits.Touches(0, 9));
        Assert.Null(edits.ToAfter(0, 9));
        Assert.Equal((4, 5), edits.ToAfter(4, 5));
    }

    [Fact]
    public void Text_typed_straight_after_a_span_leaves_it_as_it_was()
    {
        var edits = EditedRegion.Between("xs", "xs.");

        Assert.False(edits.Touches(0, 2));
        Assert.Equal((0, 2), edits.ToAfter(0, 2));
    }

    [Fact]
    public void Identical_texts_map_every_offset_to_itself()
    {
        var edits = EditedRegion.Between("def a = 1", "def a = 1");

        Assert.True(edits.IsEmpty);
        Assert.Equal(4, edits.ToBefore(4));
        Assert.Equal((4, 5), edits.ToAfter(4, 5));
    }
}
