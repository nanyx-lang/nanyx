/// A test build's module (see `NyxCompiler.Package.testResult`): every test of the package, each exported as
/// `CodeGen.testExport` of its index, and listed in the `nanyx.tests` section (see `WasmBinary.withTests`), so that the
/// runtime can run one by its index or its name.
module Transpiler.Wasm.TestBuild

open NyxCompiler
open Transpiler.Wasm.CodeGen

/// The module, in the binary format, of a test build whose root `typed` is, compiled with `options`, its `Tests` the
/// root's, and each test's place, from 1, its file named as `options.SourceRoot` names files (see `CoreIr.shownFile`).
let assemble (options: WasmOptions) (typed: TypedModule) : byte array =
    let wat, _ = transpileTypedModuleWithOptions { options with Tests = typed.Tests } typed
    let shown = CoreIr.shownFile options.SourceRoot
    WasmBinary.assemble wat
    |> WasmBinary.withTests (typed.Tests |> List.map (fun test -> test.Module, test.Name, shown test.Span.File, test.Span.StartLine + 1, test.Span.StartColumn + 1))
