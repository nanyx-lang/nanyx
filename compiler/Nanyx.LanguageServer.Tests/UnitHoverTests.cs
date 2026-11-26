using OmniSharp.Extensions.LanguageServer.Protocol;
using Xunit;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer.Tests;

/// <summary>
/// Hover on units of measure: a dimension, wherever it is written, shows its declaration and the units that make
/// values of it, and a unit shows its declaration and its dimension.
/// </summary>
public sealed class UnitHoverTests : LanguageServerTestBase
{
    /// <summary>What every hover on the prelude's <c>Time</c> says after its declaration.</summary>
    private const string TimeUnits =
        "Units: `ns` (base), `us = 1_000ns`, `ms = 1_000us`, `s = 1_000ms`, `min = 60s`, `hr = 60min`, `d = 24hr`\n\nWrite a value as `500ms`.";

    [Fact]
    public async Task A_dimension_in_a_type_lists_its_units_from_the_smallest()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("hover-dimension-type.nyx"));
        Open(uri, "def isLong: Time(int) -> bool = { t -> t > 1s }\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var hover = await HoverAt(uri, 0, 13);
        Assert.Equal($"```nanyx\ndimension Time\n```\n\n{TimeUnits}", hover?.Contents.MarkupContent?.Value);
        Assert.Equal(new Range(0, 12, 0, 16), hover?.Range);
    }

    [Fact]
    public async Task A_measured_value_lists_the_units_of_its_dimension_after_its_type()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("hover-measured-value.nyx"));
        Open(uri, "def timeout = 500ms\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var hover = await HoverAt(uri, 0, 5);
        Assert.Equal($"```nanyx\ndef timeout: Time(int)\n```\n\n{TimeUnits}", hover?.Contents.MarkupContent?.Value);
    }

    [Fact]
    public async Task The_unit_of_a_number_with_a_unit_shows_its_declaration_and_its_dimension()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("hover-unit-literal.nyx"));
        Open(uri, "def timeout = 500ms\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var unit = await HoverAt(uri, 0, 18);
        Assert.Equal(
            $"```nanyx\nunit ms: Time = 1_000us\n```\n\nA unit of `Time`: `1_000_000ns`.\n\n{TimeUnits}",
            unit?.Contents.MarkupContent?.Value);
        Assert.Equal(new Range(0, 17, 0, 19), unit?.Range);

        // The number itself is the literal, of its type.
        var number = await HoverAt(uri, 0, 15);
        Assert.StartsWith("```nanyx\nTime(int)\n```", number?.Contents.MarkupContent?.Value);
    }

    [Fact]
    public async Task A_dimension_and_unit_declared_in_the_module_show_its_units()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("hover-unit-declarations.nyx"));
        Open(uri, """
            dimension Length

            @baseUnit unit m: Length
            unit km: Length = 1_000m
            unit mm: Length = 0.001m

            def far: Length(float) = 2.0km

            """);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        const string lengthUnits = "Units: `mm = 0.001m`, `m` (base), `km = 1_000m`\n\nWrite a value as `500mm`.";
        var dimension = await HoverAt(uri, 0, 12);
        Assert.Equal($"```nanyx\ndimension Length\n```\n\n{lengthUnits}", dimension?.Contents.MarkupContent?.Value);

        var baseUnit = await HoverAt(uri, 2, 16);
        Assert.Equal(
            $"```nanyx\n@baseUnit unit m: Length\n```\n\nThe base unit of `Length`, which its values are stored in.\n\n{lengthUnits}",
            baseUnit?.Contents.MarkupContent?.Value);

        var multiple = await HoverAt(uri, 4, 6);
        Assert.Equal(
            $"```nanyx\nunit mm: Length = 0.001m\n```\n\nA unit of `Length`: `1/1000m`.\n\n{lengthUnits}",
            multiple?.Contents.MarkupContent?.Value);

        // The dimension in a type of the module's own.
        var inType = await HoverAt(uri, 6, 10);
        Assert.Equal($"```nanyx\ndimension Length\n```\n\n{lengthUnits}", inType?.Contents.MarkupContent?.Value);
    }

    [Fact]
    public async Task A_derived_dimension_says_it_has_no_units_of_its_own()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("hover-derived-dimension.nyx"));
        Open(uri, """
            dimension Length

            @baseUnit unit m: Length

            dimension Speed = Length / Time

            """);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var hover = await HoverAt(uri, 4, 12);
        Assert.Equal(
            "```nanyx\ndimension Speed = Length / Time\n```\n\nA derived dimension has no units of its own: write a value of it in its parts' units, as `1km / 1hr`.",
            hover?.Contents.MarkupContent?.Value);
    }
}
