using OmniSharp.Extensions.LanguageServer.Protocol;
using Xunit;

namespace Nanyx.LanguageServer.Tests;

/// <summary>
/// Hover on a lambda's opening <c>{</c> or its parameters says what it captures, so the cost of a closure is visible:
/// one that captures nothing is a static closure, with no allocation, and one that captures allocates a closure block
/// (#1651).
/// </summary>
public sealed class LambdaCaptureHoverTests : LanguageServerTestBase
{
    /// <summary>The hover's text at the first place <paramref name="written"/> is on <paramref name="line"/> of <paramref name="text"/>.</summary>
    private async Task<string?> HoverOn(string name, string text, int line, string written)
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath(name));
        Open(uri, text);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);
        var hover = await HoverAt(uri, line, text.Split('\n')[line].IndexOf(written, StringComparison.Ordinal));
        return hover?.Contents.MarkupContent?.Value;
    }

    private const string Apply = "def apply: (int -> int), int -> int = { f, x -> f(x) }\n";

    [Fact]
    public async Task A_lambda_that_captures_nothing_says_it_allocates_nothing()
    {
        var text = Apply + "def run: int -> int = { n -> apply({ x -> x + 1 }, n) }\n";
        Assert.Equal("```nanyx\nint -> int\n```\n\nCaptures nothing: no allocation", await HoverOn("captures-nothing.nyx", text, 1, "{ x"));
    }

    [Fact]
    public async Task A_lambda_that_captures_names_them_in_the_order_it_uses_them_on_its_brace_and_its_parameters()
    {
        var text = Apply + "def run: int, int -> int = { a, b -> apply({ x -> b * x + a }, 1) }\n";
        Assert.EndsWith("\n\nCaptures `b`, `a` (allocates a closure)", await HoverOn("captures-two.nyx", text, 1, "{ x"));
        Assert.EndsWith("\n\nCaptures `b`, `a` (allocates a closure)", await HoverOn("captures-two-parameter.nyx", text, 1, "x ->"));
        // Not in its body, nor on a def's own lambda, which is the function being defined.
        Assert.DoesNotContain("Captures", await HoverOn("captures-two-body.nyx", text, 1, "b * x") ?? "");
        Assert.DoesNotContain("Captures", await HoverOn("captures-two-def.nyx", text, 1, "{ a") ?? "");
    }

    [Fact]
    public async Task A_captured_mut_cell_is_marked_as_one()
    {
        var text = "def count: int -> int = { n ->\n  memory {\n    mut total = 0\n    def add = { x: int -> total := total + x + n }\n    add(1)\n    total\n  }\n}\n";
        Assert.EndsWith("\n\nCaptures `total` (a `mut` cell), `n` (allocates a closure)", await HoverOn("captures-mut.nyx", text, 3, "{ x"));
    }

    [Fact]
    public async Task A_lambda_captures_what_a_lambda_inside_it_takes_from_around_it()
    {
        var text = "def run: int -> int = { n ->\n  def outer = { a: int ->\n    def inner = { b: int -> a + b + n }\n    inner(1)\n  }\n  outer(2)\n}\n";
        Assert.EndsWith("\n\nCaptures `n` (allocates a closure)", await HoverOn("captures-outer.nyx", text, 1, "{ a"));
        Assert.EndsWith("\n\nCaptures `a`, `n` (allocates a closure)", await HoverOn("captures-inner.nyx", text, 2, "{ b"));
    }
}
