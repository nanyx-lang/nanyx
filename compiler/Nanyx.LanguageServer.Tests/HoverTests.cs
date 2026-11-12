using System.IO.Pipelines;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using OmniSharp.Extensions.LanguageServer.Client;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using Xunit;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using Linter = NyxCompiler.Linter;
using LinterOptions = NyxCompiler.LinterOptions;
using Fix = NyxCompiler.Fix;
using Report = NyxCompiler.Report;
using Rule = NyxCompiler.Rule;
using SemanticModel = NyxCompiler.SemanticModel;
using Severity = NyxCompiler.Severity;

namespace Nanyx.LanguageServer.Tests;

/// <summary>Hover: the type, declaration and doc comment of what is under the pointer.</summary>
[Collection(LanguageServerTestBase.Collection)]
public sealed class HoverTests : LanguageServerTestBase
{
    [Fact]
    public void Server_advertises_hover()
    {
        Assert.NotNull(_client.ServerSettings.Capabilities.HoverProvider);
    }

    [Fact]
    public async Task Hover_shows_the_inferred_type_of_the_expression_under_the_pointer()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "hover.nyx"));

        // CRLF line endings: positions are still counted per line, as the editor counts them.
        Open(uri, "def id = { x -> x }\r\ndef total = id(40) + 2\r\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // The literal `40`, innermost inside the call and the addition.
        var literal = await HoverAt(uri, 1, 16);
        Assert.NotNull(literal);
        Assert.Equal("```nanyx\nint\n```", literal.Contents.MarkupContent?.Value);
        Assert.Equal(MarkupKind.Markdown, literal.Contents.MarkupContent?.Kind);
        Assert.Equal(new Range(1, 15, 1, 17), literal.Range);

        // The polymorphic `id`, instantiated at this call.
        var callee = await HoverAt(uri, 1, 13);
        Assert.Equal("```nanyx\ndef id: int -> int\n```", callee?.Contents.MarkupContent?.Value);
        Assert.Equal(new Range(1, 12, 1, 14), callee?.Range);

        // The lambda in its own definition, with its type variable named for reading.
        var lambda = await HoverAt(uri, 0, 9);
        Assert.Equal("```nanyx\nα -> α\n```", lambda?.Contents.MarkupContent?.Value);
    }

    [Fact]
    public async Task Hover_on_a_declarations_name_shows_its_name_and_type()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "hover-declaration.nyx"));

        Open(uri, "def id = { x -> x }\ndef total = id(40) + 2\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var id = await HoverAt(uri, 0, 5);
        Assert.Equal("```nanyx\ndef id: α -> α\n```", id?.Contents.MarkupContent?.Value);
        Assert.Equal(new Range(0, 4, 0, 6), id?.Range);

        var total = await HoverAt(uri, 1, 6);
        Assert.Equal("```nanyx\ndef total: int\n```", total?.Contents.MarkupContent?.Value);

        var parameter = await HoverAt(uri, 0, 11);
        Assert.Equal("```nanyx\nx: α\n```", parameter?.Contents.MarkupContent?.Value);
        Assert.Equal(new Range(0, 11, 0, 12), parameter?.Range);
    }

    [Fact]
    public async Task Hover_shows_what_a_hole_in_a_type_was_inferred_to_be()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "hover-hole.nyx"));

        Open(uri, "def xs: list(_) = [1, 2]\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var name = await HoverAt(uri, 0, 5);
        Assert.Equal("```nanyx\ndef xs: list(int)\n```", name?.Contents.MarkupContent?.Value);

        var hole = await HoverAt(uri, 0, 13);
        Assert.Equal("```nanyx\nint\n```", hole?.Contents.MarkupContent?.Value);
        Assert.Equal(new Range(0, 13, 0, 14), hole?.Range);
    }

    [Fact]
    public async Task Hover_and_completion_show_a_names_doc_comment_with_its_links()
    {
        var path = Path.Combine(Path.GetTempPath(), "hover-doc.nyx");
        var uri = DocumentUri.FromFileSystemPath(path);

        Open(uri, """
            --| One more than `n`.
              See [`dec`], and [`nowhere`].
            def inc = { n -> n + 1 }
            def dec = { n -> n - 1 }
            def two = inc(1)
            """);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var expected = $"""
            One more than `n`.
            See [`dec`]({uri}#L4,5), and `nowhere`.
            """.ReplaceLineEndings("\n");
        var hover = await HoverAt(uri, 4, 11);
        Assert.Equal($"```nanyx\ndef inc: int -> int\n```\n\n---\n\n{expected}", hover?.Contents.MarkupContent?.Value);

        var item = (await CompletionsAt(uri, 4, 10)).Single(completion => completion.Label == "inc");
        Assert.Equal(expected, item.Documentation?.MarkupContent?.Value);
        Assert.Null((await CompletionsAt(uri, 4, 10)).Single(completion => completion.Label == "dec").Documentation);

        // A link goes to the declaration of the name it links to.
        var definition = await SingleDefinitionAt(uri, 1, 9);
        Assert.Equal(new Range(3, 4, 3, 7), definition?.Range);
    }

    [Fact]
    public async Task Hover_on_a_defs_name_shows_its_declaration_with_its_contexts_then_its_doc_comment()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "hover-def.nyx"));

        Open(uri, """
            --| Takes one order.
            def takeOrder: [Console] string, list(string) -> () = { customer, order -> println(customer) }
            @entry
            def main = {
              takeOrder("pat", [])
            }
            """);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var expected = "```nanyx\ndef takeOrder: [Console] string, list(string) -> ()\n```\n\n---\n\nTakes one order.";
        // At its definition, and where it is called.
        Assert.Equal(expected, (await HoverAt(uri, 1, 6))?.Contents.MarkupContent?.Value);
        Assert.Equal(expected, (await HoverAt(uri, 4, 4))?.Contents.MarkupContent?.Value);
        // A parameter shows its name and type.
        Assert.Equal("```nanyx\ncustomer: string\n```", (await HoverAt(uri, 1, 58))?.Contents.MarkupContent?.Value);
    }
    [Fact]
    public async Task Hover_outside_any_expression_returns_nothing()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "hover-nothing.nyx"));

        Open(uri, "def answer = 42\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.Null(await HoverAt(uri, 0, 1));
    }

    [Fact]
    public async Task Hover_in_a_document_with_type_errors_reads_its_own_types()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "hover-error.nyx"));

        Open(uri, "def answer = missing + 1\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        // What did check has its type; what did not has the type `???`.
        Assert.Equal("```nanyx\nint\n```", (await HoverAt(uri, 0, 23))?.Contents.MarkupContent?.Value);
        Assert.Equal("```nanyx\n???\n```", (await HoverAt(uri, 0, 15))?.Contents.MarkupContent?.Value);
    }

    [Fact]
    public async Task Hover_in_a_document_that_has_never_parsed_returns_nothing()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "hover-unparsed.nyx"));

        Open(uri, "def answer = (1 +\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        Assert.Null(await HoverAt(uri, 0, 14));
    }

    [Fact]
    public async Task Hover_on_a_type_name_shows_the_type_definition_even_inside_a_block()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-9");

        Open(uri, "type Point = (x: int, y: int)\ndef local = {\n  def p: Point = (x = 1, y = 2)\n  p.x\n}\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var hover = await _client.TextDocument.RequestHover(
            new HoverParams { TextDocument = new TextDocumentIdentifier(uri), Position = new Position(2, 10) },
            _cancellation.Token);

        Assert.NotNull(hover);
        Assert.Equal("```nanyx\ntype Point = (x: int, y: int)\n```", hover.Contents.MarkupContent?.Value);
        Assert.Equal(new Range(2, 9, 2, 14), hover.Range);
    }

    [Fact]
    public async Task Hover_reads_the_last_good_compile_except_in_a_def_edited_since()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "hover-last-good.nyx"));

        Open(uri, "def id = { x -> x }\ndef total = id(40) + 2\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // A text that does not parse has no types of its own.
        Change(uri, version: 2, "def id = { x -> x }\ndef total = id(40) +\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        // The untouched def still has its types; the edited one has none, rather than ones that may be stale.
        var lambda = await HoverAt(uri, 0, 9);
        Assert.Equal("```nanyx\nα -> α\n```", lambda?.Contents.MarkupContent?.Value);
        Assert.Null(await HoverAt(uri, 1, 16));
        Assert.Equal([new Range(0, 4, 0, 6)], (await CodeLenses(uri)).Select(lens => lens.Range));
    }

    [Fact]
    public async Task Hover_and_definition_on_an_operator_never_lead_to_what_it_is_desugared_to()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "reserved-operator.nyx"));

        Open(uri, "def p = 2.0 ** 3\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // The `**`, desugared to the prelude's `__pow`.
        var hover = (await HoverAt(uri, 0, 12))?.Contents.MarkupContent?.Value;
        Assert.Equal("```nanyx\nfloat\n```", hover);
        Assert.Empty(await DefinitionsAt(uri, 0, 12));
    }

    [Fact]
    public async Task Hover_on_an_operator_in_a_generic_function_shows_the_context_member_it_calls()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "context-operator.nyx"));

        Open(uri, "def plus: [Add(a)] a, a -> a = { x, y -> x + y }\ndef inferred = { a, b -> a + b }\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // The `+` of the `Add(a)` the function requires.
        var hover = await HoverAt(uri, 0, 43);
        Assert.Equal("```nanyx\n+: a, a -> a\n```", hover?.Contents.MarkupContent?.Value);
        Assert.Equal(new Range(0, 43, 0, 44), hover?.Range);
        // Where the context is inferred, there is no member to show, and the operator shows its type.
        var inferred = await HoverAt(uri, 1, 27);
        Assert.Equal("```nanyx\nα, α -> α\n```", inferred?.Contents.MarkupContent?.Value);
        Assert.Equal(new Range(1, 27, 1, 28), inferred?.Range);
    }
}
