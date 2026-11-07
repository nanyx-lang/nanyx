using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nanyx.LanguageServer.Handlers;
using OmniSharp.Extensions.LanguageServer.Server;

namespace Nanyx.LanguageServer;

/// <summary>
/// The Nanyx language server: a Language Server Protocol endpoint that calls the compiler as a library.
/// The <c>nanyx lsp</c> command runs it over stdio; tests run it over in-memory streams.
/// </summary>
/// <remarks>
/// The server publishes diagnostics, shows inferred types on hover, in lenses above unannotated top-level defs and
/// in inlay hints after other unannotated names, goes to definitions, and completes the names in scope. Each editor
/// feature is one handler class in <c>Handlers/</c>, registered in <see cref="Configure"/>, reading the document's
/// last compile from <see cref="DocumentStore"/>. To add a feature, add a handler that derives from the matching
/// OmniSharp base class and register it: <c>textDocument/references</c>, for one, would be a
/// <c>ReferencesHandlerBase</c>. It is not implemented yet (#113).
/// </remarks>
public static class NanyxLanguageServer
{
    /// <summary>Runs the server over the process's standard input and output until the client asks it to exit.</summary>
    public static async Task RunStdioAsync(ServerSettings? settings = null, CancellationToken cancellationToken = default)
    {
        // The protocol owns stdout: nothing else in the process may write to it while the server runs.
        await RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), settings, cancellationToken);
    }

    /// <summary>Runs the server over the given streams until the client asks it to exit.</summary>
    public static async Task RunAsync(
        Stream input,
        Stream output,
        ServerSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        var server = await OmniSharp.Extensions.LanguageServer.Server.LanguageServer.From(
            options => Configure(options, settings ?? ServerSettings.Default).WithInput(input).WithOutput(output),
            cancellationToken);

        await server.WaitForExit;
    }

    /// <summary>Registers the services and handlers that make up the server.</summary>
    public static LanguageServerOptions Configure(LanguageServerOptions options, ServerSettings settings) =>
        options
            .WithServices(services =>
            {
                services.AddSingleton(settings);
                services.AddSingleton<DocumentStore>();
                services.AddSingleton<DiagnosticsPublisher>();
            })
            .ConfigureLogging(logging =>
            {
                // Logs go to the client as window/logMessage (VS Code shows them in the Output panel), never to stdout.
                logging.AddLanguageProtocolLogging();
                logging.SetMinimumLevel(LogLevel.Warning);
            })
            .WithHandler<TextDocumentSyncHandler>()
            .WithHandler<HoverHandler>()
            .WithHandler<DefinitionHandler>()
            .WithHandler<CodeLensHandler>()
            .WithHandler<InlayHintHandler>()
            .WithHandler<CompletionHandler>();
}
