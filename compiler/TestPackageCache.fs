/// The package cache of a test run. Each test project that builds packages runs its tests with `NANYX_PACKAGES` set to
/// a cache of its own, made empty when the run starts and deleted when it ends, so that the shipped packages it seeds
/// are this build's, and not a copy an earlier build left in the user's cache (#3215). The processes the tests start
/// inherit it. A project on xunit v2 names `PackageCacheFramework` as its test framework:
///
///   [<assembly: TestPackageCache.Framework("Nanyx.TestPackageCache+PackageCacheFramework", "<the project's assembly>")>]
///   do ()
///
/// and one on xunit v3 makes `Cache` an assembly fixture: `[<assembly: AssemblyFixture(typeof<TestPackageCache.Cache>)>]`.
module Nanyx.TestPackageCache

open System
open System.IO

/// Sets `NANYX_PACKAGES` to a new, empty directory, which disposing it deletes.
type Cache() =
    let directory = Path.Combine(Path.GetTempPath(), "nanyx-test-packages-" + Guid.NewGuid().ToString "N")

    do
        Directory.CreateDirectory directory |> ignore
        Environment.SetEnvironmentVariable("NANYX_PACKAGES", directory)

    /// The cache's directory.
    member _.Directory = directory

    interface IDisposable with
        member _.Dispose() =
            try
                Directory.Delete(directory, true)
            with
            | :? IOException
            | :? UnauthorizedAccessException -> ()

/// xunit v2's `TestFramework` attribute, which names the test framework of an assembly by its type's name and its
/// assembly's. It is declared here because a project that references xunit v3 as well has two
/// `Xunit.TestFrameworkAttribute`s, and F# may read it as v3's.
[<Xunit.Sdk.TestFrameworkDiscoverer("Xunit.Sdk.TestFrameworkTypeDiscoverer", "xunit.execution.{Platform}")>]
[<AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)>]
type FrameworkAttribute(typeName: string, assemblyName: string) =
    inherit Attribute()
    member _.TypeName = typeName
    member _.AssemblyName = assemblyName
    interface Xunit.Sdk.ITestFrameworkAttribute

/// xunit v2's framework, run with a `Cache`, deleted when the test process exits: xunit v2 doesn't dispose its
/// framework reliably.
type PackageCacheFramework(sink: Xunit.Abstractions.IMessageSink) =
    inherit Xunit.Sdk.XunitTestFramework(sink)

    let cache = new Cache()

    do AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> (cache :> IDisposable).Dispose())
