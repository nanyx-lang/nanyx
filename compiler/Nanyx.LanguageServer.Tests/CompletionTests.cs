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

/// <summary>Completion.</summary>
[Collection(LanguageServerTestBase.Collection)]
public sealed class CompletionTests : LanguageServerTestBase
{
    [Fact]
    public void Server_advertises_completion()
    {
        Assert.NotNull(_client.ServerSettings.Capabilities.CompletionProvider);
    }

    [Fact]
    public async Task Completion_offers_the_names_in_scope_with_their_types_nearest_first()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("completion.nyx"));

        Open(uri, "def scale = { factor ->\r\n  def offset = 1\r\n  factor * 2 + offset\r\n}\r\ndef later = 3\r\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var items = await CompletionsAt(uri, 2, 2);
        var names = items.Where(item => item.Kind != CompletionItemKind.Keyword).OrderBy(item => item.SortText).ToList();
        Assert.Equal(
            [
                ("factor", CompletionItemKind.Variable, "int"),
                ("offset", CompletionItemKind.Variable, "int"),
                ("later", CompletionItemKind.Variable, "int"),
                ("scale", CompletionItemKind.Function, "int -> int")
            ],
            names.Where(item => item.SortText![0] != '2').Select(item => (item.Label, item.Kind, item.Detail)));
        // The prelude's names come after the file's own, and keywords last.
        Assert.Contains(names, item => item.Label == "proc" && item.SortText![0] == '2');
        Assert.Contains(items, item => item.Label == "match" && item.Kind == CompletionItemKind.Keyword);

        // Outside the lambda, its parameter and locals are not in scope.
        Assert.DoesNotContain(await CompletionsAt(uri, 4, 12), item => item.Label is "factor" or "offset");
    }

    [Fact]
    public async Task Completion_offers_the_reserved_words_as_keywords_and_not_a_workflows_keywords()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("completion-keywords.nyx"));

        Open(uri, "def answer = 42\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // `yield`, `await` and `defer` are members of a workflow's builder, not words the language reserves.
        Assert.Equal(
            [
                "and", "as", "context", "def", "do", "else", "except", "export", "for", "if", "import", "in", "is", "match",
                "module", "mut", "not", "or", "private", "protected", "rec", "return", "then", "try", "type", "typeof",
                "use", "where", "while", "with"
            ],
            (await CompletionsAt(uri, 1, 0)).Where(item => item.Kind == CompletionItemKind.Keyword).Select(item => item.Label).Order());
    }

    [Fact]
    public async Task Completion_reads_the_text_with_type_errors_and_the_last_typed_one_while_it_does_not_parse()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("completion-last-good.nyx"));

        Open(uri, "def scale = { factor ->\n  def offset = 1\n  factor * 2 + offset\n}\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // Typing a name that does not exist yet, inside the lambda: a type error, so the text is typed all the same.
        Change(uri, version: 2, "def scale = { factor ->\n  def offset = 1\n  factor * 2 + off\n}\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);
        var inLambda = await CompletionsAt(uri, 2, 18);
        Assert.Contains(inLambda, item => item.Label == "offset" && item.Detail == "int");
        Assert.Contains(inLambda, item => item.Label == "factor");

        // An unclosed bracket at the end of the file: a parse error, so the last typed text is read.
        Change(uri, version: 3, "def scale = { factor ->\n  def offset = 1\n  factor * 2 + off\n}\ndef later = (sc");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);
        var atEnd = await CompletionsAt(uri, 4, 15);
        Assert.Contains(atEnd, item => item.Label == "scale" && item.Detail == "int -> int");
        Assert.DoesNotContain(atEnd, item => item.Label == "factor");
    }

    [Fact]
    public async Task Completion_offers_nothing_in_a_comment_or_a_string_and_only_members_after_a_dot()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("completion-nothing.nyx"));

        Open(uri, "def a = 1 -- about a\ndef b = \"text {a} more\"\ndef p = (x = 1, y = 2)\ndef c = p.x\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.Empty(await CompletionsAt(uri, 0, 19));
        Assert.Empty(await CompletionsAt(uri, 1, 11));
        Assert.Contains(await CompletionsAt(uri, 1, 15), item => item.Label == "a");
        Assert.Equal(["x", "y"], (await CompletionsAt(uri, 3, 11)).Select(item => item.Label));
    }

    [Fact]
    public void Completion_is_triggered_by_a_dot_a_pipe_and_the_start_of_an_import_paths_next_part()
    {
        Assert.Equal([".", "\\", "/", "\""], _client.ServerSettings.Capabilities.CompletionProvider?.TriggerCharacters ?? []);
    }

    [Fact]
    public async Task Completion_after_a_pipe_just_typed_offers_what_is_attached_to_the_values_type_first()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-5");

        Open(uri, "import nanyx/text\ndef s = \"abc\"\ndef double = { n -> n * 2 }\ndef r = s\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // The `\` was just typed, so the text does not parse.
        Change(uri, version: 2, "import nanyx/text\ndef s = \"abc\"\ndef double = { n -> n * 2 }\ndef r = s \\\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        var items = (await CompletionsAt(uri, 3, 11)).OrderBy(item => item.SortText).ToList();
        Assert.Contains(items, item => item.Label == "startsWith" && item.Kind == CompletionItemKind.Function && item.SortText![0] == '0');
        Assert.Contains(items, item => item.Label == "double" && item.Detail == "int -> int" && item.SortText![0] == '1');
        Assert.DoesNotContain(items, item => item.Kind == CompletionItemKind.Keyword);
        Assert.True(
            items.FindIndex(item => item.Label == "startsWith") < items.FindIndex(item => item.Label == "double"));
    }

    [Fact]
    public async Task Completion_after_a_dot_just_typed_offers_a_records_fields()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("completion-fields.nyx"));

        Open(uri, "def p = (x = 1, name = \"a\")\ndef q = p.x\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // The dot was just typed, so the text does not parse.
        Change(uri, version: 2, "def p = (x = 1, name = \"a\")\ndef q = p.\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        Assert.Equal(
            [("name", CompletionItemKind.Field, "string"), ("x", CompletionItemKind.Field, "int")],
            (await CompletionsAt(uri, 1, 10)).Select(item => (item.Label, item.Kind, item.Detail)));
    }

    [Fact]
    public async Task Completion_after_a_dot_on_a_string_offers_its_length_and_nothing_attached()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-6");

        Open(uri, "import nanyx/text\ndef s = \"abc\"\ndef n = s.length\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.Equal(
            [("length", CompletionItemKind.Property, "int")],
            (await CompletionsAt(uri, 2, 10)).Select(item => (item.Label, item.Kind, item.Detail)));
    }

    [Fact]
    public async Task Completion_after_an_imports_alias_offers_its_exports()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-3");

        Open(uri, "import nanyx/text as t\ndef shout = t.string.uppercase(\"hi\")\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.Contains(await CompletionsAt(uri, 1, 14), item => item.Label == "string" && item.Kind == CompletionItemKind.Module);
        Assert.Contains(await CompletionsAt(uri, 1, 21), item => item.Label == "uppercase" && item.Kind == CompletionItemKind.Function);
    }

    [Fact]
    public async Task Completion_never_offers_the_standard_librarys_reserved_names()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-reserved");
        var text = """
            import nanyx/text
            import nanyx/console
            def s = "abc"
            def r = s
            def m = Result.map

            """.ReplaceLineEndings("\n");

        Open(uri, text);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // At the start of an expression, and after a dot.
        Assert.DoesNotContain(await CompletionsAt(uri, 3, 8), item => item.Label.StartsWith("__"));
        Assert.DoesNotContain(await CompletionsAt(uri, 4, 15), item => item.Label.StartsWith("__"));

        // After a pipe just typed.
        Change(uri, version: 2, text.Replace("def r = s\n", "def r = s \\\n"));
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);
        var piped = await CompletionsAt(uri, 3, 11);
        Assert.Contains(piped, item => item.Label == "startsWith");
        Assert.DoesNotContain(piped, item => item.Label.StartsWith("__"));
    }

    [Fact]
    public async Task Completion_in_an_import_offers_the_standard_librarys_modules()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-4");

        Open(uri, "import \n");
        await NextPublished(uri);
        var package = Assert.Single(await CompletionsAt(uri, 0, 7));
        Assert.Equal(("nanyx", CompletionItemKind.Module, "nanyx/"), (package.Label, package.Kind, package.TextEdit?.TextEdit?.NewText));
        // The rest of the path is asked for at once.
        Assert.Equal("editor.action.triggerSuggest", package.Command?.Name);

        Change(uri, version: 2, "import nanyx/co\n");
        await NextPublished(uri);
        var modules = await CompletionsAt(uri, 0, 15);
        Assert.Equal(["console", "host", "math", "result", "text"], modules.Select(item => item.Label));
        var console = modules[0];
        Assert.Equal(new Range(0, 13, 0, 15), console.TextEdit?.TextEdit?.Range);
        Assert.StartsWith("How a program talks to the terminal.\n", console.Documentation?.MarkupContent?.Value);
    }

    [Fact]
    public async Task Completion_in_a_quoted_import_offers_the_files_and_folders_beside_the_document()
    {
        var directory = Directory.CreateTempSubdirectory("nanyx-completion-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "util.nyx"), "module util\n");
            File.WriteAllText(Path.Combine(directory.FullName, "notes.txt"), "");
            Directory.CreateDirectory(Path.Combine(directory.FullName, "shared"));
            File.WriteAllText(Path.Combine(directory.FullName, "shared", "text.nyx"), "module text\n");
            var uri = DocumentUri.FromFileSystemPath(Path.Combine(directory.FullName, "main.nyx"));

            Open(uri, "import \"\n");
            await NextPublished(uri);
            // Not the document itself, nor a file that is not Nanyx source.
            Assert.Equal(
                [("shared", CompletionItemKind.Folder, "shared/"), ("util.nyx", CompletionItemKind.File, "util.nyx")],
                (await CompletionsAt(uri, 0, 8)).Select(item => (item.Label, item.Kind, item.TextEdit?.TextEdit?.NewText)));

            Change(uri, version: 2, "import \"shared/t\n");
            await NextPublished(uri);
            var inFolder = Assert.Single(await CompletionsAt(uri, 0, 16));
            Assert.Equal(("text.nyx", new Range(0, 15, 0, 16)), (inFolder.Label, inFolder.TextEdit?.TextEdit?.Range));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
