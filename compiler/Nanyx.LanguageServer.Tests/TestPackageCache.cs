using System.Runtime.CompilerServices;

namespace Nanyx.LanguageServer.Tests;

/// <summary>
/// The package cache of a test run, as the compiler's F# test projects have (see <c>TestPackageCache.fs</c>): the
/// tests run with <c>NANYX_PACKAGES</c> naming a new, empty directory, seeded before any test runs and deleted when the
/// test process exits, so that the shipped packages the server reads are this build's, and not a copy an earlier build
/// left in the user's cache (#3215).
/// </summary>
internal static class TestPackageCache
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var cache = Path.Combine(Path.GetTempPath(), "nanyx-test-packages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        Environment.SetEnvironmentVariable("NANYX_PACKAGES", cache);
        // Seeding packs the packages the compiler ships, which takes seconds: done here, before any test runs, rather
        // than in whichever test first reads a package, where it could take the test past a timeout. A store that
        // can't be seeded is reported by the tests that read a package.
        NyxCompiler.SeededPackages.seed(NyxCompiler.SeededPackages.user());
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(cache, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        };
    }
}
