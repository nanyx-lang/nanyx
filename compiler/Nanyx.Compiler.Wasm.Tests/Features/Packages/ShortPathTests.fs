/// A path written with Windows' short 8.3 names, as `C:\Users\RICHAR~1\...`, names the same directory as its long form,
/// and the compiler started through one finds its shipped packages as it does through the other (#3116). Only Windows
/// has short names, and only on a volume that makes them, so elsewhere these tests are skipped.
module TranspilerWasmTests.Features.Packages.ShortPaths

open System
open System.IO
open System.Runtime.InteropServices
open System.Text
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

[<DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)>]
extern uint32 private GetShortPathNameW(string longPath, StringBuilder shortPath, uint32 bufferLength)

/// The short form of `path`, an existing directory, or why there is none: not on Windows, or a volume that makes no
/// short names, where the short form is the path itself.
let private shortForm (path: string) : Result<string, string> =
    if not (OperatingSystem.IsWindows()) then
        Result.Error "only Windows has 8.3 short names"
    else
        let buffer = StringBuilder(path.Length + 260)
        let length = int (GetShortPathNameW(path, buffer, uint32 buffer.Capacity))
        if length = 0 || length >= buffer.Capacity then Result.Error $"'{path}' has no short form"
        elif buffer.ToString() = path then Result.Error $"the volume of '{path}' makes no 8.3 short names"
        else Result.Ok(buffer.ToString())

/// `shortForm path`, or the test skipped, saying why.
let private shortOrSkip (path: string) =
    match shortForm path with
    | Result.Ok short -> short
    | Result.Error reason -> raise (Xunit.Sdk.SkipException.ForSkip reason)

[<Fact>]
let ``A path written with short names is given back with its long names`` () =
    use directory = new TempDirectory()
    let long = Directory.CreateDirectory(Path.Combine(directory.Path, "a directory with a long name")).FullName
    let short = shortOrSkip long
    Assert.Contains("~", short)
    Assert.Equal(long, LongPaths.ofPath short)

[<Fact>]
let ``A path with no short names is given back as it is`` () =
    use directory = new TempDirectory()
    Assert.Equal(directory.Path, LongPaths.ofPath directory.Path)

[<Fact>]
let ``The compiler started through the short path of its directory builds a package under a shipped host`` () =
    let cliDirectory = shortOrSkip (Path.TrimEndingDirectorySeparator AppContext.BaseDirectory)
    use directory = new TempDirectory()
    File.WriteAllText(
        Path.Combine(directory.Path, "demo.nyxp"),
        $"""(package = (name = "demo", version = "0.1.0"), host = (package = "{Hosts.consolePackage}", version = "{Toolchain.version}"))"""
    )
    Directory.CreateDirectory(Path.Combine(directory.Path, "src")) |> ignore
    File.WriteAllText(Path.Combine(directory.Path, "src", "main.nyx"), "module main\n\n@entry\ndef main = {\n  println(\"Hello, World!\")\n}\n")
    let outcome = nanyxExecutableFrom cliDirectory directory.Path [ "build" ]
    Assert.True(outcome.ExitCode = 0, $"{outcome.StandardOutput}\n{outcome.StandardError}")
