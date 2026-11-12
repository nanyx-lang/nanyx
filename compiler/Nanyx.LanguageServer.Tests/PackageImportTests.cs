using OmniSharp.Extensions.LanguageServer.Protocol;
using Xunit;

namespace Nanyx.LanguageServer.Tests;

/// <summary>A source file of a package imports the package's modules by name, from whichever file declares them.</summary>
[Collection(LanguageServerTestBase.Collection)]
public sealed class PackageImportTests : LanguageServerTestBase
{
    [Fact]
    public async Task A_package_file_finds_a_module_declared_in_another_file_of_the_package()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "src", "lib"));
        try
        {
            File.WriteAllText(Path.Combine(directory, "acme.web.nyxp"), """
                [package]
                name = "acme.web"
                version = "0.1.0"
                sources = ["src/**/*.nyx"]
                """.ReplaceLineEndings("\n"));
            File.WriteAllText(Path.Combine(directory, "src", "lib", "hello.nyx"), """
                module greetings

                export def greet: string -> string = { name -> "Hello, " ++ name }
                """.ReplaceLineEndings("\n"));
            var uri = DocumentUri.FromFileSystemPath(Path.Combine(directory, "src", "main.nyx"));

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
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
