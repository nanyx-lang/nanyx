using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace Nanyx.LanguageServer.Tests;

/// <summary>Semantic tokens: the names of nominal types, marked so that the editor shows them apart.</summary>
[Collection(LanguageServerTestBase.Collection)]
public sealed class SemanticTokensTests : LanguageServerTestBase
{
    [Fact]
    public void Server_advertises_the_nominal_and_private_modifiers()
    {
        var legend = _client.ServerSettings.Capabilities.SemanticTokensProvider?.Legend;
        Assert.NotNull(legend);
        Assert.Equal(["type"], legend.TokenTypes.Select(type => type.ToString()));
        Assert.Equal(["nominal", "private"], legend.TokenModifiers.Select(modifier => modifier.ToString()));
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
            def old: $Email = $Email("a")
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
                (5, 17, "UserId", "nominal private"),
                (7, 10, "Email", "nominal"),
                (7, 19, "Email", "nominal")
            ],
            await TokensIn(uri, source));
    }

    /// <summary>The document's tokens, decoded: each one's line, character, text and modifiers.</summary>
    private async Task<List<(int Line, int Character, string Text, string Modifiers)>> TokensIn(DocumentUri uri, string source)
    {
        var legend = _client.ServerSettings.Capabilities.SemanticTokensProvider!.Legend;
        var tokens = await _client.TextDocument.RequestSemanticTokensFull(
            new SemanticTokensParams { TextDocument = new TextDocumentIdentifier(uri) },
            _cancellation.Token);
        var lines = source.Split('\n');
        var decoded = new List<(int, int, string, string)>();
        var (line, character) = (0, 0);
        var data = tokens!.Data;
        for (var index = 0; index < data.Length; index += 5)
        {
            line += data[index];
            character = data[index] == 0 ? character + data[index + 1] : data[index + 1];
            var modifiers = legend.TokenModifiers
                .Where((_, bit) => (data[index + 4] & (1 << bit)) != 0)
                .Select(modifier => modifier.ToString());
            decoded.Add((line, character, lines[line].Substring(character, data[index + 2]), string.Join(" ", modifiers)));
        }

        return decoded;
    }
}
