/// A test build's module (see `NyxCompiler.Package.testResult`): its program, whose entry point runs the test at the
/// index its host gives it, with each test listed at its index in the `nanyx.tests` section (see `WasmBinary.withTests`),
/// so that the runtime can choose one by its name, and say where it is.
module Transpiler.Wasm.TestBuild

open NyxCompiler
open Transpiler.Wasm.CodeGen

/// The module, in the binary format, of a test build whose root `typed` is, compiled with `options`, its `dbg`s and
/// crashes given to the runtime, listing the root's `Tests`, each test's place from 1 and its file named as
/// `options.SourceRoot` names files (see `PackageModules.shownFile`).
let assemble (options: WasmOptions) (typed: TypedModule) : byte array =
    // The runner takes each `dbg` a test reaches, and keeps it with what the test prints, and a crash's message, which
    // it reports (see `Nanyx.Runtime.WasmTester`).
    let wat, _ = transpileTypedModuleWithOptions { options with DbgToEmbedder = true; CrashToRuntime = true } typed
    let shown = NyxCompiler.PackageModules.shownFile options.SourceRoot
    WasmBinary.assemble wat
    |> WasmBinary.withTests (
        typed.Tests
        |> List.map (fun test ->
            { Module = test.Module
              Name = test.Name
              File = shown test.Span.File
              Line = test.Span.StartLine + 1
              Column = test.Span.StartColumn + 1
              Io = test.Io }))
