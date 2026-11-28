/// A lint build's module: a linter's `lint`, passed to the linter host's `run`, which asks the runtime's `nanyx:lint`
/// about the module it lints (see `Nanyx.Runtime.WasmLinter`).
module Transpiler.Wasm.LintBuild

open NyxCompiler
open Transpiler.Wasm.CodeGen

/// The module, in the binary format, of a lint build whose root `typed` is, compiled with `options`, its crashes given
/// to the runtime with their messages, so that a linter that fails says why.
let assemble (options: WasmOptions) (typed: TypedModule) : byte array =
    let wat, _ = transpileTypedModuleWithOptions { options with CrashToRuntime = true } typed
    WasmBinary.assemble wat
