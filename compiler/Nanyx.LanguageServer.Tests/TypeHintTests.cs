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

/// <summary>Type hints: code lenses and inlay hints.</summary>
[Collection(LanguageServerTestBase.Collection)]
public sealed class TypeHintTests : LanguageServerTestBase
{
    [Fact]
    public void Server_advertises_code_lenses_and_inlay_hints()
    {
        Assert.NotNull(_client.ServerSettings.Capabilities.CodeLensProvider);
        Assert.NotNull(_client.ServerSettings.Capabilities.InlayHintProvider);
    }

    [Fact]
    public async Task Code_lenses_show_the_types_of_unannotated_top_level_defs()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "lenses.nyx"));

        Open(uri, "def id = { x -> x }\r\ndef answer: int = 42\r\ndef (p, q) = (1, \"s\")\r\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var lenses = (await _client.TextDocument.RequestCodeLens(
            new CodeLensParams { TextDocument = new TextDocumentIdentifier(uri) },
            _cancellation.Token))?.ToList() ?? [];

        // The annotated `answer` has none; the two names of one destructuring def each say which they are for.
        Assert.Equal(
            [
                (new Range(0, 4, 0, 6), "α -> α"),
                (new Range(2, 5, 2, 6), "p: int"),
                (new Range(2, 8, 2, 9), "q: string")
            ],
            lenses.Select(lens => (lens.Range, lens.Command?.Title)));
    }

    [Fact]
    public async Task Inlay_hints_show_the_types_of_unannotated_locals_and_parameters()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "inlay-hints.nyx"));

        Open(uri, "def scale = { factor ->\r\n  def offset = 1\r\n  factor * 2 + offset\r\n}\r\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var everywhere = await InlayHintsIn(uri, new Range(0, 0, 4, 0));
        Assert.Equal(
            [(new Position(0, 20), ": int"), (new Position(1, 12), ": int")],
            everywhere.Select(hint => (hint.Position, hint.Label.String)));
        Assert.All(everywhere, hint => Assert.Equal(InlayHintKind.Type, hint.Kind));

        // Only the hints in the range asked for.
        var secondLine = await InlayHintsIn(uri, new Range(1, 0, 1, 16));
        Assert.Equal([new Position(1, 12)], secondLine.Select(hint => hint.Position));
    }

    [Fact]
    public async Task Top_level_inlay_hints_show_top_level_defs_types_as_inlay_hints_instead_of_lenses()
    {
        await RestartWithAsync(ServerSettings.Default with { TopLevelTypeHints = TopLevelTypeHints.Inlay });
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "top-level-hints.nyx"));

        Open(uri, "def id = { x -> x }\r\ndef answer: int = 42\r\ndef (p, q) = (1, \"s\")\r\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var lenses = await _client.TextDocument.RequestCodeLens(
            new CodeLensParams { TextDocument = new TextDocumentIdentifier(uri) },
            _cancellation.Token);
        Assert.Empty(lenses ?? []);

        // Each name of the destructuring def has its own hint, so none needs to say which name it is for.
        Assert.Equal(
            [
                (new Position(0, 6), ": α -> α"),
                (new Position(0, 12), ": α"),
                (new Position(2, 6), ": int"),
                (new Position(2, 9), ": string")
            ],
            (await InlayHintsIn(uri, new Range(0, 0, 3, 0))).Select(hint => (hint.Position, hint.Label.String)));
    }

    [Fact]
    public async Task Top_level_inlay_hint_of_a_def_that_writes_only_its_contexts_follows_them()
    {
        await RestartWithAsync(ServerSettings.Default with { TopLevelTypeHints = TopLevelTypeHints.Inlay });
        var uri = DocumentUri.Parse("untitled:Untitled-10");

        Open(uri, "context Log = (log: string -> ())\ndef greet: [Log] = { name -> log(name) }\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // `def greet: [Log] string -> () = { name: string -> ...`, the type completing the annotation.
        Assert.Equal(
            [
                (new Position(1, 16), "string -> ()", true),
                (new Position(1, 25), ": string", false)
            ],
            (await InlayHintsIn(uri, new Range(1, 0, 2, 0))).Select(hint => (hint.Position, hint.Label.String, hint.PaddingLeft)));
    }

    [Fact]
    public async Task Top_level_type_hints_off_shows_no_lenses_and_only_the_locals_inlay_hints()
    {
        await RestartWithAsync(ServerSettings.Default with { TopLevelTypeHints = TopLevelTypeHints.Off });
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "top-level-hints-off.nyx"));

        Open(uri, "def id = { x -> x }\r\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var lenses = await _client.TextDocument.RequestCodeLens(
            new CodeLensParams { TextDocument = new TextDocumentIdentifier(uri) },
            _cancellation.Token);
        Assert.Empty(lenses ?? []);
        Assert.Equal(
            [(new Position(0, 12), ": α")],
            (await InlayHintsIn(uri, new Range(0, 0, 1, 0))).Select(hint => (hint.Position, hint.Label.String)));
    }

    [Theory]
    [InlineData(false, true, true, new[] { "scale", "factor", "rest", "items", "tail" })]
    [InlineData(true, false, true, new[] { "scale", "offset", "rest", "tail" })]
    [InlineData(true, true, false, new[] { "scale", "factor", "offset", "rest", "items" })]
    // The top-level defs' hints are there whatever the other settings say.
    [InlineData(false, false, false, new[] { "scale", "rest" })]
    public async Task Inlay_hints_for_each_kind_of_declaration_can_be_turned_off(
        bool locals, bool parameters, bool patternVariables, string[] shown)
    {
        await RestartWithAsync(ServerSettings.Default with
        {
            TopLevelTypeHints = TopLevelTypeHints.Inlay,
            LocalTypeHints = locals,
            ParameterTypeHints = parameters,
            PatternVariableTypeHints = patternVariables
        });
        var uri = DocumentUri.Parse("untitled:Untitled-11");
        var source =
            "def scale = { factor ->\n  def offset = 1\n  factor * 2 + offset\n}\n" +
            "def rest = { items -> match items\n  | [_, ...tail] -> tail\n  | _ -> items }\n";
        var lines = source.Split('\n');

        Open(uri, source);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.Equal(
            shown.Select(PositionAfter),
            (await InlayHintsIn(uri, new Range(0, 0, lines.Length, 0))).Select(hint => hint.Position));

        Position PositionAfter(string name)
        {
            var line = Array.FindIndex(lines, text => text.Contains(name));
            return new Position(line, lines[line].IndexOf(name, StringComparison.Ordinal) + name.Length);
        }
    }

    [Fact]
    public async Task Lenses_and_inlay_hints_in_a_document_with_type_errors_read_its_own_types()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "hints-error.nyx"));

        Open(uri, "def scale = { factor -> factor + missing }\ndef broken = { x -> nope }\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        // `scale` checks around the unknown name; `broken`'s result is what failed, `???`.
        Assert.Equal(
            [(new Range(0, 4, 0, 9), "int -> int"), (new Range(1, 4, 1, 10), "α -> ???")],
            (await CodeLenses(uri)).Select(lens => (lens.Range, lens.Command?.Title)));
        Assert.Equal(
            [(new Position(0, 20), ": int"), (new Position(1, 16), ": α")],
            (await InlayHintsIn(uri, new Range(0, 0, 2, 0))).Select(hint => (hint.Position, hint.Label.String)));
    }

    [Fact]
    public async Task Lenses_and_inlay_hints_in_a_document_that_has_never_parsed_are_empty()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "hints-unparsed.nyx"));

        Open(uri, "def scale = { factor -> factor + }\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        var lenses = await _client.TextDocument.RequestCodeLens(
            new CodeLensParams { TextDocument = new TextDocumentIdentifier(uri) },
            _cancellation.Token);
        Assert.Empty(lenses ?? []);
        Assert.Empty(await InlayHintsIn(uri, new Range(0, 0, 1, 0)));
    }

    [Fact]
    public async Task Lenses_and_inlay_hints_stay_on_unchanged_defs_while_the_file_does_not_type_check()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "hints-last-good.nyx"));

        Open(uri, "def id = { x -> x }\r\ndef scale = { factor ->\r\n  def offset = 1\r\n  factor * 2 + offset\r\n}\r\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // A broken line inserted between the two defs: both keep their hints, the second moved down a line.
        Change(uri, version: 2,
            "def id = { x -> x }\r\ndef broken = missing +\r\ndef scale = { factor ->\r\n  def offset = 1\r\n  factor * 2 + offset\r\n}\r\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        Assert.Equal(
            [(new Range(0, 4, 0, 6), "α -> α"), (new Range(2, 4, 2, 9), "int -> int")],
            (await CodeLenses(uri)).Select(lens => (lens.Range, lens.Command?.Title)));
        Assert.Equal(
            [(new Position(0, 12), ": α"), (new Position(2, 20), ": int"), (new Position(3, 12), ": int")],
            (await InlayHintsIn(uri, new Range(0, 0, 6, 0))).Select(hint => (hint.Position, hint.Label.String)));
    }
}
