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

/// <summary>Go to definition.</summary>
[Collection(LanguageServerTestBase.Collection)]
public sealed class DefinitionTests : LanguageServerTestBase
{
    [Fact]
    public void Server_advertises_definition()
    {
        Assert.NotNull(_client.ServerSettings.Capabilities.DefinitionProvider);
    }

    [Fact]
    public async Task Definition_of_locals_parameters_and_later_top_level_values_is_in_the_document()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("definition.nyx"));

        Open(uri, "def scale = { factor ->\r\n  def offset = 1\r\n  factor * 2 + offset + later\r\n}\r\ndef later = 3\r\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // A parameter.
        Assert.Equal(new Location { Uri = uri, Range = new Range(0, 14, 0, 20) }, await SingleDefinitionAt(uri, 2, 3));
        // A def in the block.
        Assert.Equal(new Location { Uri = uri, Range = new Range(1, 6, 1, 12) }, await SingleDefinitionAt(uri, 2, 16));
        // A top-level def written after its use.
        Assert.Equal(new Location { Uri = uri, Range = new Range(4, 4, 4, 9) }, await SingleDefinitionAt(uri, 2, 26));
    }

    [Fact]
    public async Task Definition_of_a_pattern_variable_is_where_the_pattern_binds_it()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("definition-pattern.nyx"));

        Open(uri, "def rest = { items -> match items\n  | [_, ...tail] -> tail\n  | _ -> items }\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.Equal(new Location { Uri = uri, Range = new Range(1, 11, 1, 15) }, await SingleDefinitionAt(uri, 1, 21));
    }

    [Fact]
    public async Task Definition_on_a_declaration_is_the_declaration_itself()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("definition-self.nyx"));

        Open(uri, "def scale = { factor -> factor * 2 }\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.Equal(new Location { Uri = uri, Range = new Range(0, 4, 0, 9) }, await SingleDefinitionAt(uri, 0, 6));
        Assert.Equal(new Location { Uri = uri, Range = new Range(0, 14, 0, 20) }, await SingleDefinitionAt(uri, 0, 14));
    }

    [Fact]
    public async Task Definition_of_what_is_not_a_name_returns_nothing()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("definition-nothing.nyx"));

        Open(uri, "def id = { x -> x }\ndef total = id(40) + 2\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // The literal `40`, inside a call whose function name is a reference.
        Assert.Empty(await DefinitionsAt(uri, 1, 16));
        // The `+`.
        Assert.Empty(await DefinitionsAt(uri, 1, 19));
    }

    [Fact]
    public async Task Definition_of_a_type_name_is_the_type_definition()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-3");

        Open(uri, "type Point = (x: int, y: int)\ndef origin: Point = (x = 0, y = 0)\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.Equal(new Location { Uri = uri, Range = new Range(0, 5, 0, 10) }, await SingleDefinitionAt(uri, 1, 14));
    }

    [Fact]
    public async Task Definition_of_a_context_member_is_its_declaration_in_the_context()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-4");

        Open(uri, "context Logger = (log: string -> ())\ndef greet: [Logger] string -> () = { name -> log(name) }\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.Equal(new Location { Uri = uri, Range = new Range(0, 18, 0, 21) }, await SingleDefinitionAt(uri, 1, 46));
    }

    [Fact]
    public async Task Definition_of_a_qualified_import_is_in_the_imported_file()
    {
        var directory = Directory.CreateTempSubdirectory("nanyx-definition-");
        try
        {
            var modulePath = Path.Combine(directory.FullName, "mod.nyx");
            File.WriteAllText(modulePath, "module mod\nexport def foo = 1\n");
            var uri = DocumentUri.FromFileSystemPath(Path.Combine(directory.FullName, "main.nyx"));

            Open(uri, "module main\nimport mod as m\ndef value = m.foo\n");
            Assert.Empty((await NextPublished(uri)).Diagnostics!);

            Assert.Equal(
                new Location { Uri = DocumentUri.FromFileSystemPath(modulePath), Range = new Range(1, 11, 1, 14) },
                await SingleDefinitionAt(uri, 2, 15));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Definition_in_the_standard_library_is_in_a_read_only_copy_of_its_source()
    {
        var root = Directory.CreateTempSubdirectory("nanyx-stdlib-");
        try
        {
            await RestartWithAsync(ServerSettings.Default with
            {
                StandardLibrarySource = new StandardLibrarySource(root.FullName)
            });
            var uri = DocumentUri.Parse("untitled:Untitled-2");

            Open(uri, "import nanyx/text as t\ndef shout = t.string.uppercase(\"hi\")\ndef n = int.fromString(\"1\")\n");
            Assert.Empty((await NextPublished(uri)).Diagnostics!);

            // A qualified import of nanyx/text, and a definition the prelude brings into scope, in one of its files.
            AssertDefinitionInStandardLibrary(await SingleDefinitionAt(uri, 1, 22), "text.nyx", "uppercase");
            AssertDefinitionInStandardLibrary(await SingleDefinitionAt(uri, 2, 13), "prelude-numbers.nyx", "fromString");
        }
        finally
        {
            foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                file.Attributes = FileAttributes.Normal;
            }

            root.Delete(recursive: true);
        }

        void AssertDefinitionInStandardLibrary(Location? location, string fileName, string name)
        {
            Assert.NotNull(location);
            var path = Path.Combine(root.FullName, "nanyx", fileName);
            Assert.Equal(DocumentUri.FromFileSystemPath(path), location.Uri);
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));

            var source = File.ReadAllText(path);
            Assert.Equal(NyxCompiler.StandardLibrary.tryLoadFile("nanyx/" + fileName).Value, source);

            var line = source.Replace("\r\n", "\n").Split('\n')[location.Range.Start.Line];
            Assert.EndsWith(name, line[location.Range.Start.Character..location.Range.End.Character]);
        }
    }
}
