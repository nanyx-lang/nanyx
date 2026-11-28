/// What a module the emitter writes imports (see `CoreIrEmit`): a foreign function, by the name it is imported as, and
/// what a debug build takes from the runtime's `nanyx:debug`.
module Transpiler.Wasm.CoreIrEmitImports

open NyxCompiler
open Transpiler.Wasm.Identifiers

/// WASI's module, which the runtime's own imports come from.
let private wasi = "wasi_snapshot_preview1"

/// The name of the function the module imports a foreign function as. A WASI function is named as the runtime names
/// its own imports, so that a foreign function of one the runtime imports is that import.
let importName (declared: Extern) =
    if declared.Module = wasi then sanitizeIdentifier declared.Name
    else sanitizeIdentifier $"import_{declared.Module}_{declared.Name}"

/// WASI's `fd_write` and `proc_exit`, as the runtime imports them: what `dbg`, a deferred error and a failed memory
/// check write with, and exit with.
let wasiFdWrite =
    { Module = wasi; Name = "fd_write"; Parameters = List.replicate 4 ExternValue.I32; Result = Some ExternValue.I32 }

let wasiProcExit = { Module = wasi; Name = "proc_exit"; Parameters = [ ExternValue.I32 ]; Result = None }

/// The import of a foreign function, as the module declares it.
let importDeclaration (declared: Extern) =
    let valueType (value: ExternValue) =
        match value with
        | ExternValue.I32 -> "i32"
        | ExternValue.I64 -> "i64"
        | ExternValue.F32 -> "f32"
        | ExternValue.F64 -> "f64"
    let parameters = if declared.Parameters.IsEmpty then "" else $""" (param {declared.Parameters |> List.map valueType |> String.concat " "})"""
    let result = declared.Result |> Option.map (fun value -> $" (result {valueType value})") |> Option.defaultValue ""
    $"""  (import "{declared.Module}" "{declared.Name}" (func ${importName declared}{parameters}{result}))"""

/// What a debug build's probes import from the runtime, the module `nanyx:debug` (see `CoreIrProbes`): whether it is
/// probing, read into a global when the program starts; the probe, which takes a probe's number and gives whether to
/// write its locals; the writing of a local, its name and its text; and that the locals asked for are all written, so
/// that a debugger can stop there with them, before the statement runs.
let probeModule = "nanyx:debug"
let probingEnabled = "__nanyx_probing_enabled"
let probing = "__nanyx_probing"
let probeCall = "__nanyx_probe"
let probeLocal = "__nanyx_probe_local"
let probeWritten = "__nanyx_probe_written"
/// `dbg`, which a module the runtime runs imports from `nanyx:debug` (see `EmitContext.DbgToRuntime`).
let dbgImport = "__nanyx_dbg"
/// `value`, which a module built with `--show-values` gives each value it shows to, from `nanyx:debug`, as it gives a
/// `dbg` to `dbg` (see `ShownValues`).
let valueImport = "__nanyx_value"
