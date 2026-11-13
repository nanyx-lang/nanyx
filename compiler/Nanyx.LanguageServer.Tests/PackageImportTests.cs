using OmniSharp.Extensions.LanguageServer.Protocol;
using Xunit;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer.Tests;

/// <summary>A source file of a package imports the package's modules by name, from whichever file declares them.</summary>
[Collection(LanguageServerTestBase.Collection)]
public sealed class PackageImportTests : LanguageServerTestBase
{
    [Fact]
    public async Task A_package_file_finds_a_module_declared_in_another_file_of_the_package()
    {
        File.WriteAllText(TempPath("acme.web.nyxp"), """
            [package]
            name = "acme.web"
            version = "0.1.0"
            sources = ["src/**/*.nyx"]
            """.ReplaceLineEndings("\n"));
        File.WriteAllText(TempPath("src", "lib", "hello.nyx"), """
            module greetings

            export def greet: string -> string = { name -> "Hello, " ++ name }
            """.ReplaceLineEndings("\n"));
        var uri = DocumentUri.FromFileSystemPath(TempPath("src", "main.nyx"));

        Open(uri, """
            module main

            import acme.web/greetings

            def hello = greet("Ada")
            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Change(uri, 2, """
            module main

            import salutations

            def hello = 1
            """.ReplaceLineEndings("\n"));
        var diagnostic = Assert.Single((await NextPublished(uri)).Diagnostics!);
        Assert.Equal("NYX0003", diagnostic.Code?.String);
        Assert.Contains("the package 'acme.web' has no module 'salutations'", diagnostic.Message.String);
    }

    [Fact]
    public async Task Definition_of_a_name_from_another_fragment_of_a_partial_module_is_in_that_file()
    {
        File.WriteAllText(TempPath("demo.nyxp"), """
            [package]
            name = "demo"
            version = "0.1.0"
            """.ReplaceLineEndings("\n"));
        var format = TempPath("format.nyx");
        File.WriteAllText(format, """
            @partial module utils

            def isNonEmpty: string -> bool = { s -> s.length > 0 }
            """.ReplaceLineEndings("\n"));
        var uri = DocumentUri.FromFileSystemPath(TempPath("validation.nyx"));

        Open(uri, """
            @partial module utils

            export def validate: string -> bool = { s -> s \isNonEmpty }
            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var definition = Assert.Single(await DefinitionsAt(uri, 2, 50));
        Assert.Equal(DocumentUri.FromFileSystemPath(format), definition.Location!.Uri);
        Assert.Equal(new Range(2, 4, 2, 14), definition.Location.Range);
    }
}
