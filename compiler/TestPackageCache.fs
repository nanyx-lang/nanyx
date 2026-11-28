/// The package cache of a test run. Each test project that builds packages runs its tests with `NANYX_PACKAGES` set to
/// a cache of its own, made empty when the run starts and deleted when it ends, so that the shipped packages it seeds
/// are this build's, and not a copy an earlier build left in the user's cache (#3215). The processes the tests start
/// inherit it. A project uses it by naming `PackageCacheFramework` as its test framework:
///
///   [<assembly: Xunit.TestFramework("Nanyx.TestPackageCache+PackageCacheFramework", "<the project's assembly>")>]
///   do ()
module Nanyx.TestPackageCache

open System
open System.IO
open Xunit.Abstractions
open Xunit.Sdk

/// xunit's framework, run with `NANYX_PACKAGES` naming a new, empty directory, which is deleted when the test process
/// exits.
type PackageCacheFramework(sink: IMessageSink) =
    inherit XunitTestFramework(sink)

    let cache = Path.Combine(Path.GetTempPath(), "nanyx-test-packages-" + Guid.NewGuid().ToString "N")

    do
        Directory.CreateDirectory cache |> ignore
        Environment.SetEnvironmentVariable("NANYX_PACKAGES", cache)
        AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
            try
                Directory.Delete(cache, true)
            with
            | :? IOException
            | :? UnauthorizedAccessException -> ())
