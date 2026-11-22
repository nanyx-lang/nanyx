using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace Nanyx.LanguageServer.Tests;

/// <summary>
/// Semantic tokens: what each name is, as the standard token types say it, and the names of nominal types, marked so
/// that the editor shows them apart.
/// </summary>
public sealed class SemanticTokensTests : LanguageServerTestBase
{
    [Fact]
    public void Server_advertises_the_standard_types_and_its_own_modifiers()
    {
        var legend = _client.ServerSettings.Capabilities.SemanticTokensProvider?.Legend;
        Assert.NotNull(legend);
        Assert.Equal(
            ["function", "parameter", "variable", "property", "enumMember", "type", "interface", "namespace", "operator", "keyword", "unresolved"],
            legend.TokenTypes.Select(type => type.ToString()));
        Assert.Equal(
            ["declaration", "readonly", "defaultLibrary", "mutable", "nominal", "private"],
            legend.TokenModifiers.Select(modifier => modifier.ToString()));
    }

    [Fact]
    public async Task Functions_parameters_and_variables_are_told_apart_with_their_declarations()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("values.nyx"));
        var source = """
            def twice = { (n: int) -> n * 2 }
            def four = twice(2)
            def first = { o ->
              match o
              | #some(value) -> value
              | #none -> 0
            }
            def total = { (xs: list(int)) ->
              memory {
                mut sum = 0
                for xs | x -> sum := sum + x
                sum
              }
            }
            """.ReplaceLineEndings("\n") + "\n";
        Open(uri, source);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.Equal(
            [
                (0, "twice", "function", "declaration"),
                (0, "n", "parameter", "declaration"),
                (0, "n", "parameter", ""),
                (1, "four", "variable", "declaration readonly"),
                (1, "twice", "function", ""),
                (2, "first", "function", "declaration"),
                (2, "o", "parameter", "declaration"),
                (3, "o", "parameter", ""),
                (4, "#some", "enumMember", ""),
                (4, "value", "variable", "declaration readonly"),
                (4, "value", "variable", "readonly"),
                (5, "#none", "enumMember", ""),
                (7, "total", "function", "declaration"),
                (7, "xs", "parameter", "declaration"),
                (8, "memory", "function", "defaultLibrary"),
                (9, "sum", "variable", "declaration mutable"),
                (10, "xs", "parameter", ""),
                (10, "x", "variable", "declaration readonly"),
                (10, "sum", "variable", "mutable"),
                (10, "sum", "variable", "mutable"),
                (10, "x", "variable", "readonly"),
                (11, "sum", "variable", "mutable")
            ],
            (await TokensIn(uri, source)).Select(token => (token.Line, token.Text, token.Type, token.Modifiers)));
    }

    [Fact]
    public async Task Modules_types_contexts_fields_and_attached_definitions_are_marked()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("shapes.nyx"));
        var source = """
            module shapes

            import nanyx/text as t

            type Point = (x: int, y: int)
            context Log = (log: string -> ())
            def origin: Point = (x = 0, y = 0)
            def Point.show: Point -> string = { p -> "{p.x}" }
            def label = origin \show
            def say: [Log] string -> () = { m -> log(m) }
            def loud = t.string.uppercase("hi")
            def x = match origin
              | (x = 0, ...) -> 0
              | _ -> 1
            """.ReplaceLineEndings("\n") + "\n";
        Open(uri, source);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.Equal(
            [
                (0, "shapes", "namespace", "declaration"),
                (2, "nanyx/text", "namespace", "declaration"),
                (2, "t", "namespace", "declaration"),
                (4, "Point", "type", "declaration"),
                (4, "x", "property", "declaration"),
                (4, "y", "property", "declaration"),
                (5, "Log", "interface", "declaration"),
                (5, "log", "function", "declaration"),
                (6, "origin", "variable", "declaration readonly"),
                (6, "Point", "type", ""),
                (6, "x", "property", ""),
                (6, "y", "property", ""),
                (7, "Point", "type", ""),
                (7, "show", "function", "declaration"),
                (7, "Point", "type", ""),
                (7, "p", "parameter", "declaration"),
                (7, "p", "parameter", ""),
                (7, "x", "property", ""),
                (8, "label", "variable", "declaration readonly"),
                (8, "origin", "variable", "readonly"),
                (8, "show", "function", ""),
                (9, "say", "function", "declaration"),
                (9, "Log", "interface", ""),
                (9, "m", "parameter", "declaration"),
                (9, "log", "function", ""),
                (9, "m", "parameter", ""),
                (10, "loud", "variable", "declaration readonly"),
                (10, "t", "namespace", ""),
                (10, "string", "type", ""),
                (10, "uppercase", "function", "defaultLibrary"),
                (11, "x", "variable", "declaration readonly"),
                (11, "origin", "variable", "readonly"),
                (12, "x", "property", "")
            ],
            (await TokensIn(uri, source)).Select(token => (token.Line, token.Text, token.Type, token.Modifiers)));
    }

    [Fact]
    public async Task An_operator_that_calls_a_definition_and_a_workflow_keyword_are_marked()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("operators.nyx"));
        var source = """
            type Point = (x: int, y: int)
            def Point.`+`: Point, Point -> Point = { a, b -> (x = a.x + b.x, y = a.y + b.y) }
            def q: Point = (x = 1, y = 2)
            def r: Point = q + q
            def halve = { (n: int) -> if n % 2 == 0 then #some(n / 2) else #error(#odd) }
            def quarter = { (n: int) -> proc {
              def half = try halve(n)
              return half / 2
            } }
            """.ReplaceLineEndings("\n") + "\n";
        Open(uri, source);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var tokens = (await TokensIn(uri, source)).Select(token => (token.Line, token.Text, token.Type, token.Modifiers)).ToList();
        Assert.Contains((3, "+", "operator", ""), tokens);
        // The `+`s on ints call no definition: they are the grammar's to colour.
        Assert.DoesNotContain(tokens, token => token is { Line: 1, Type: "operator" });
        Assert.Contains((6, "try", "keyword", ""), tokens);
        Assert.Contains((7, "return", "keyword", ""), tokens);
    }

    [Fact]
    public async Task A_shadowing_name_is_what_it_refers_to_and_an_unresolved_name_is_marked_unresolved()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("shadow.nyx"));
        var source = """
            def n = { (x: int) -> x }
            def m = { (n: int) -> n + missing }
            """.ReplaceLineEndings("\n") + "\n";
        Open(uri, source);
        // A file with a name error still has tokens for what resolved.
        Assert.Contains(await NextPublished(uri) is var published ? published.Diagnostics! : [], diagnostic => diagnostic.Code?.String == "NYX0101");

        var tokens = (await TokensIn(uri, source)).Select(token => (token.Line, token.Text, token.Type, token.Modifiers)).ToList();
        Assert.Contains((1, "n", "parameter", ""), tokens);
        Assert.Contains((1, "missing", "unresolved", ""), tokens);
    }

    [Fact]
    public async Task Each_kind_of_unknown_name_is_unresolved_and_the_names_around_it_keep_their_tokens()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("unknown.nyx"));
        var source = """
            def p = (name = "x", age = 3)
            def a = undefinedValue
            def b = undefinedFunction(1)
            def c: Strnig = "x"
            def d: [Consol] () -> () = { () }
            def e = nomodule.thing
            def f = p.nmae
            def Missing.g = { (x: int) -> x }
            """.ReplaceLineEndings("\n") + "\n";
        Open(uri, source);
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        var tokens = (await TokensIn(uri, source)).Select(token => (token.Line, token.Text, token.Type)).ToList();
        Assert.Equal(
            [(1, "undefinedValue"), (2, "undefinedFunction"), (3, "Strnig"), (4, "Consol"), (5, "nomodule"), (6, "nmae"), (7, "Missing")],
            tokens.Where(token => token.Type == "unresolved").Select(token => (token.Line, token.Text)));
        Assert.Contains((0, "p", "variable"), tokens);
        Assert.Contains((0, "name", "property"), tokens);
        Assert.Contains((5, "e", "variable"), tokens);
        Assert.Contains((6, "f", "variable"), tokens);
    }

    [Fact]
    public async Task A_module_with_a_type_error_elsewhere_keeps_its_tokens_and_marks_an_unknown_name()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("mismatch.nyx"));
        var source = """
            def twice = { (n: int) -> n * 2 }
            def wrong: string = twice(2)
            def lost = twcie(3)
            """.ReplaceLineEndings("\n") + "\n";
        Open(uri, source);
        Assert.Contains((await NextPublished(uri)).Diagnostics!, diagnostic => diagnostic.Code?.String == "NYX0201");

        var tokens = (await TokensIn(uri, source)).Select(token => (token.Line, token.Text, token.Type)).ToList();
        Assert.Contains((0, "twice", "function"), tokens);
        Assert.Contains((1, "twice", "function"), tokens);
        Assert.Contains((2, "twcie", "unresolved"), tokens);
    }

    [Fact]
    public async Task A_text_that_does_not_parse_keeps_the_last_good_texts_tokens_outside_what_was_edited()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("edited.nyx"));
        var source = "def one = 1\ndef two = one\n";
        Open(uri, source);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var edited = "def one = 1\ndef two = (\n";
        Change(uri, 2, edited);
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        Assert.Equal([(0, "one", "variable", "declaration readonly")], (await TokensIn(uri, edited)).Select(token => (token.Line, token.Text, token.Type, token.Modifiers)));
    }

    [Fact]
    public async Task Every_name_of_a_nominal_type_is_marked_and_a_private_ones_also_private()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("tokens.nyx"));
        var source = """
            type Email = protected string
            type UserId = private int
            type Point = (x: int, y: int)
            def Email.parse: string -> Email = { s -> Email(s) }
            def raw: Email -> string = { Email(s) -> s }
            def id: UserId = UserId(1)
            def p: Point = (x = 1, y = 2)
            """.ReplaceLineEndings("\n") + "\n";
        Open(uri, source);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.Equal(
            [
                (0, 5, "Email", "nominal"),
                (1, 5, "UserId", "nominal private"),
                (3, 4, "Email", "nominal"),
                (3, 27, "Email", "nominal"),
                (3, 42, "Email", "nominal"),
                (4, 9, "Email", "nominal"),
                (4, 29, "Email", "nominal"),
                (5, 8, "UserId", "nominal private"),
                (5, 17, "UserId", "nominal private")
            ],
            (await TokensIn(uri, source))
                .Where(token => token.Modifiers.Contains("nominal"))
                .Select(token => (token.Line, token.Character, token.Text, token.Modifiers.Replace("declaration ", ""))));
    }

    /// <summary>The document's tokens, decoded: each one's line, character, text and modifiers.</summary>
    private async Task<List<(int Line, int Character, string Text, string Type, string Modifiers)>> TokensIn(DocumentUri uri, string source)
    {
        var legend = _client.ServerSettings.Capabilities.SemanticTokensProvider!.Legend;
        var tokens = await _client.TextDocument.RequestSemanticTokensFull(
            new SemanticTokensParams { TextDocument = new TextDocumentIdentifier(uri) },
            _cancellation.Token);
        var lines = source.Split('\n');
        var decoded = new List<(int, int, string, string, string)>();
        var (line, character) = (0, 0);
        var data = tokens!.Data;
        for (var index = 0; index < data.Length; index += 5)
        {
            line += data[index];
            character = data[index] == 0 ? character + data[index + 1] : data[index + 1];
            var modifiers = legend.TokenModifiers
                .Where((_, bit) => (data[index + 4] & (1 << bit)) != 0)
                .Select(modifier => modifier.ToString());
            decoded.Add((line, character, lines[line].Substring(character, data[index + 2]), legend.TokenTypes.ElementAt(data[index + 3]).ToString(), string.Join(" ", modifiers)));
        }

        return decoded;
    }
}
