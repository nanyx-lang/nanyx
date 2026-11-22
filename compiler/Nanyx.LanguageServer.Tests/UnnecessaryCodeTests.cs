using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer.Tests;

/// <summary>
/// Code that is unused or can never run is tagged <see cref="DiagnosticTag.Unnecessary"/>, so the editor draws it faint,
/// alongside the linter's warning about it. Exports are never faint.
/// </summary>
public sealed class UnnecessaryCodeTests : LanguageServerTestBase
{
    /// <summary>The one diagnostic of <paramref name="code"/> published for <paramref name="text"/>.</summary>
    private async Task<Diagnostic> Reported(string fileName, string text, string code)
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath(fileName));
        Open(uri, text);
        return Assert.Single((await NextPublished(uri)).Diagnostics!, d => d.Code?.String == code);
    }

    private static void AssertFaint(Diagnostic diagnostic, Range range)
    {
        Assert.Equal(range, diagnostic.Range);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal([DiagnosticTag.Unnecessary], diagnostic.Tags!);
    }

    [Fact]
    public async Task An_unused_local_is_drawn_faint()
    {
        var diagnostic = await Reported(
            "unused-local.nyx",
            "::linter nanyx.lint\nexport def f: () -> int = {\n  def unused = 1\n  2\n}\n",
            "default/unusedBindings");
        AssertFaint(diagnostic, new Range(2, 6, 2, 12));
    }

    [Fact]
    public async Task An_unused_parameter_is_drawn_faint()
    {
        var diagnostic = await Reported(
            "unused-parameter.nyx",
            "::linter nanyx.lint\nexport def f: int -> int = { n -> 2 }\n",
            "default/unusedBindings");
        AssertFaint(diagnostic, new Range(1, 29, 1, 30));
    }

    [Fact]
    public async Task An_unused_import_is_drawn_faint()
    {
        var diagnostic = await Reported(
            "unused-import.nyx",
            "::linter nanyx.lint\nimport nanyx/math\n\nexport def f: () -> int = { 2 }\n",
            "default/unusedImports");
        AssertFaint(diagnostic, new Range(1, 7, 1, 17));
    }

    [Fact]
    public async Task Code_after_a_crash_is_drawn_faint()
    {
        var diagnostic = await Reported(
            "after-crash.nyx",
            "::linter nanyx.lint\nexport def f: int -> int = { x ->\n  crash(\"bad\")\n  x + 1\n}\n",
            "default/unreachableCode");
        AssertFaint(diagnostic, new Range(3, 2, 3, 7));
    }

    [Fact]
    public async Task Code_after_a_return_is_drawn_faint()
    {
        var diagnostic = await Reported(
            "after-return.nyx",
            "::linter nanyx.lint\nexport def f: int -> #some(int) | #none = { n ->\n  proc {\n    return n\n    println(\"after\")\n  }\n}\n",
            "default/unreachableCode");
        AssertFaint(diagnostic, new Range(4, 4, 4, 20));
    }

    [Fact]
    public async Task A_match_arm_that_can_never_match_is_drawn_faint_whole()
    {
        var diagnostic = await Reported(
            "unreachable-arm.nyx",
            "::linter nanyx.lint\nexport def f: bool -> int = { b ->\n  match b\n    | _ -> 1\n    | true -> 2\n}\n",
            "default/unreachableArms");
        AssertFaint(diagnostic, new Range(4, 6, 4, 15));
    }

    [Fact]
    public async Task An_unused_private_def_is_drawn_faint()
    {
        var diagnostic = await Reported(
            "unused-def.nyx",
            "::linter nanyx.lint\ndef helper: () -> int = { 1 }\n\nexport def f: () -> int = { 2 }\n",
            "default/unusedPrivateDefinitions");
        AssertFaint(diagnostic, new Range(1, 4, 1, 10));
    }

    [Fact]
    public async Task An_unused_private_type_is_drawn_faint()
    {
        var diagnostic = await Reported(
            "unused-type.nyx",
            "::linter nanyx.lint\ntype Point = (x: int, y: int)\n\nexport def f: () -> int = { 2 }\n",
            "default/unusedPrivateDefinitions");
        AssertFaint(diagnostic, new Range(1, 5, 1, 10));
    }

    [Fact]
    public async Task An_unused_private_context_and_its_member_are_drawn_faint()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("unused-private-context.nyx"));
        Open(uri, "::linter nanyx.lint\ncontext Log = (log: string -> ())\n\nexport def f: () -> int = { 2 }\n");

        var diagnostics = (await NextPublished(uri)).Diagnostics!.Where(d => d.Code?.String == "default/unusedPrivateDefinitions").ToList();
        Assert.Equal(2, diagnostics.Count);
        AssertFaint(diagnostics[0], new Range(1, 8, 1, 11));
        AssertFaint(diagnostics[1], new Range(1, 15, 1, 18));
    }

    [Fact]
    public async Task An_unused_context_requirement_is_drawn_faint()
    {
        var diagnostic = await Reported(
            "unused-context.nyx",
            "::linter nanyx.lint\nscoped context Foo = protected ()\n\ndef f: [Foo] () -> () = { }\n\nexport def main: () -> () = { () -> () }\n",
            "default/unusedContexts");
        AssertFaint(diagnostic, new Range(3, 8, 3, 11));
    }

    [Fact]
    public async Task An_export_nothing_uses_is_not_drawn_faint()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("unused-export.nyx"));
        Open(uri, "::linter nanyx.lint\nexport def f: () -> int = { 2 }\n\nexport type Point = (x: int, y: int)\n");

        Assert.DoesNotContain((await NextPublished(uri)).Diagnostics!, d => d.Tags?.Contains(DiagnosticTag.Unnecessary) == true);
    }
}
