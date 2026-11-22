/// Exports marked `@component` that require contexts (#2326): each runs under the contexts its host's root provides,
/// through the root's `provide`, as the entry point runs under its `run`; a context the root doesn't provide, or a root
/// with no `provide`, is NYX0237 at the export.
module TranspilerWasmTests.Features.Components.ProvidedExports

open System.IO
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

/// A package, `acme.time`, under `host`, the manifest's field that names it, or none if it is empty, with no `@entry`, whose `stamp.nyx` is `stamp`, written to a new directory.
let private package (host: string) (stamp: string) =
    let directory = new TempDirectory()
    Directory.CreateDirectory(Path.Combine(directory.Path, "src")) |> ignore
    let hostField = if host = "" then "" else $", {host}"
    let manifest = $"""(package = (name = "acme.time", version = "0.1.0", sources = ["src/**/*.nyx"]){hostField})"""
    File.WriteAllText(Path.Combine(directory.Path, "acme.time.nyxp"), manifest + "\n")
    File.WriteAllText(Path.Combine(directory.Path, "src", "stamp.nyx"), stamp)
    directory

/// The monotonic clock, at five milliseconds, in nanoseconds.
let private clock (import: Wasmtime.FunctionImport) =
    match import.ModuleName, import.Name with
    | "wasi:clocks/monotonic-clock@0.3.0", "now" ->
        Some (Wasmtime.Function.UntypedCallbackDelegate(fun _ _ results -> results.[0] <- Wasmtime.ValueBox.op_Implicit 5_000_000L))
    | _ -> None

[<Fact>]
let ``An export that requires Clock runs under the console component root's Clock`` () =
    use time = package consoleHostEntry "module stamp\n\n@component\nexport def later: [Clock] int -> int = { delay -> delay + ticks() }\n"
    let built = nanyxIn time.Path [ "build"; "--release" ]
    Assert.Equal("", built.StandardError)
    Assert.Equal(0, built.ExitCode)
    let wit = File.ReadAllText(Path.Combine(time.Path, "out", "wit", "component.wit"))
    Assert.Contains("later: func(delay: s64) -> s64;", wit)
    Assert.Equal(0, (nanyxIn time.Path [ "build"; "--release"; "--wat" ]).ExitCode)
    let engine, store, instance = instantiateCoreWith clock (File.ReadAllText(Path.Combine(time.Path, "out", "acme.time.wat")))
    use _ = engine
    use _ = store
    Assert.Equal(15L, instance.GetFunction<int64, int64>("acme:time/stamp@0.1.0#later").Invoke(10L))

[<Fact>]
let ``An export that requires a context the host's root doesn't provide is NYX0237 at the export`` () =
    let stamp =
        "module stamp\n\nexport context Calendar = (today: () -> int)\n\n@component\nexport def later: [Calendar, Clock] int -> int = { delay -> delay + today() + ticks() }\n"
    use time = package consoleHostEntry stamp
    let checkedPackage = nanyxIn time.Path [ "check" ]
    Assert.Equal(1, checkedPackage.ExitCode)
    Assert.Contains("NYX0237", checkedPackage.StandardOutput + checkedPackage.StandardError)
    Assert.Contains("'later' requires 'Calendar', which the host 'nanyx.console/console.host' doesn't provide", checkedPackage.StandardOutput + checkedPackage.StandardError)
    Assert.Contains("src/stamp.nyx:5:1", checkedPackage.StandardOutput + checkedPackage.StandardError)

[<Fact>]
let ``An export that requires contexts under a host whose root has no provide is NYX0237 at the export`` () =
    let host = $"host = (package = \"nanyx.http\", version = \"{Toolchain.version}\")"
    use time = package host "module stamp\n\n@component\nexport def later: [Clock] int -> int = { delay -> delay + ticks() }\n"
    let checkedPackage = nanyxIn time.Path [ "check" ]
    let output = checkedPackage.StandardOutput + checkedPackage.StandardError
    Assert.Equal(1, checkedPackage.ExitCode)
    Assert.Contains("NYX0237", output)
    Assert.Contains("its root has no 'provide'", output)

[<Fact>]
let ``An export that requires contexts in a package with no host is an error that says it has none`` () =
    let stamp = "export module stamp\n\nimport nanyx/clock as (Clock)\n\n@component\nexport def later: [Clock] int -> int = { delay -> delay + ticks() }\n"
    use time = package "" stamp
    let checkedPackage = nanyxIn time.Path [ "check" ]
    let output = checkedPackage.StandardOutput + checkedPackage.StandardError
    Assert.Equal(1, checkedPackage.ExitCode)
    Assert.Contains("NYX0301", output)
    Assert.Contains("its package names no host to provide them", output)

[<Fact>]
let ``An export that requires contexts, and the entry, import only what the contexts they use need`` () =
    let stamp =
        "module stamp\n\n@entry\ndef main = { println(\"hi\") }\n\n@component\nexport def later: [Clock] int -> int = { delay -> delay + ticks() }\n"
    use time = package consoleHostEntry stamp
    let built = nanyxIn time.Path [ "build"; "--release"; "--wat" ]
    Assert.Equal("", built.StandardError)
    let wat = File.ReadAllText(Path.Combine(time.Path, "out", "acme.time.wat"))
    Assert.Contains("(import \"wasi:clocks/monotonic-clock@0.3.0\" \"now\"", wat)
    Assert.Contains("(import \"wasi:cli/stdout@0.3.0\"", wat)
    for unused in [ "wasi:http"; "wasi:filesystem"; "wasi:random"; "wasi:cli/environment" ] do
        Assert.DoesNotContain($"(import \"{unused}", wat)
