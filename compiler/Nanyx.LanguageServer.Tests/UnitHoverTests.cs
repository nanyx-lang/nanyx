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
        "Units: `ns` (storage), `us = 1_000ns`, `ms = 1_000us`, `s = 1_000ms` (display), `min = 60s`, `hr = 60min`, `d = 24hr`\n\nWrite a value as `500ms`.";

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

        const string lengthUnits = "Units: `mm = 0.001m` (storage), `m` (display), `km = 1_000m`\n\nWrite a value as `500mm`.";
        var dimension = await HoverAt(uri, 0, 12);
        Assert.Equal($"```nanyx\ndimension Length\n```\n\n{lengthUnits}", dimension?.Contents.MarkupContent?.Value);

        var displayUnit = await HoverAt(uri, 2, 16);
        Assert.Equal(
            $"```nanyx\n@baseUnit unit m: Length\n```\n\nThe default display unit of `Length`: `1_000mm`.\n\n{lengthUnits}",
            displayUnit?.Contents.MarkupContent?.Value);

        var storageUnit = await HoverAt(uri, 4, 6);
        Assert.Equal(
            $"```nanyx\nunit mm: Length = 0.001m\n```\n\nThe default storage unit of `Length`, which its values are stored as a number of.\n\n{lengthUnits}",
            storageUnit?.Contents.MarkupContent?.Value);

        var multiple = await HoverAt(uri, 3, 6);
        Assert.Equal(
            $"```nanyx\nunit km: Length = 1_000m\n```\n\nA unit of `Length`: `1_000_000mm`.\n\n{lengthUnits}",
            multiple?.Contents.MarkupContent?.Value);

        // The dimension in a type of the module's own.
        var inType = await HoverAt(uri, 6, 10);
        Assert.Equal($"```nanyx\ndimension Length\n```\n\n{lengthUnits}", inType?.Contents.MarkupContent?.Value);
    }

    [Fact]
    public async Task The_value_a_hover_says_to_write_compiles()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("hover-hinted-values.nyx"));
        Open(uri, """
            dimension Length

            @baseUnit unit m: Length
            unit mm: Length = 0.001m
            unit km: Length = 1_000m

            dimension Speed = Length / Time

            unit kmh: Speed = 1km / 1hr

            def time = 500ms
            def length = 500.0mm
            def speed = 500.0kmh

            """);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // Each hint is what a value of its dimension is written as above.
        Assert.EndsWith("Write a value as `500ms`.", (await HoverAt(uri, 10, 5))?.Contents.MarkupContent?.Value);
        Assert.EndsWith("Write a value as `500mm`.", (await HoverAt(uri, 11, 5))?.Contents.MarkupContent?.Value);
        Assert.EndsWith("Write a value as `500.0kmh`.", (await HoverAt(uri, 12, 5))?.Contents.MarkupContent?.Value);
    }

    [Fact]
    public async Task A_derived_dimension_lists_its_units_and_a_unit_of_one_its_declaration()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("hover-derived-dimension.nyx"));
        Open(uri, """
            dimension Length

            @baseUnit unit m: Length
            unit km: Length = 1_000m

            dimension Speed = Length / Time

            unit kmh: Speed = 1km / 1hr
            unit mps: Speed = 1m / 1s

            dimension Acceleration = Length / (Time * Time)

            """);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        const string speedUnits = "Units: `kmh = 1km / 1hr`, `mps = 1m / 1s`\n\nWrite a value as `500.0kmh`.";
        var speed = await HoverAt(uri, 5, 12);
        Assert.Equal($"```nanyx\ndimension Speed = Length / Time\n```\n\n{speedUnits}", speed?.Contents.MarkupContent?.Value);

        var unit = await HoverAt(uri, 7, 6);
        Assert.Equal(
            $"```nanyx\nunit kmh: Length / Time = 1km / 1hr\n```\n\nA unit of `Length / Time`.\n\n{speedUnits}",
            unit?.Contents.MarkupContent?.Value);

        // One with no units of its own says how to write a value of it.
        var acceleration = await HoverAt(uri, 10, 12);
        Assert.Equal(
            "```nanyx\ndimension Acceleration = Length / (Time * Time)\n```\n\nIt has no units here: write a value of it from its parts' units, as `100m / 9.58s`, or declare one, as `unit kmh: Speed = 1km / 1hr`.",
            acceleration?.Contents.MarkupContent?.Value);
    }
}
