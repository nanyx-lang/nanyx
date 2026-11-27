namespace Nanyx.Runtime

open System
open Wasmtime

/// What a module the runtime runs reports a crash through before the trap that ends it (see
/// `Transpiler.Wasm.CodeGen.CrashReport`): `nanyx:crash` `report`, given the address and the length of the message's
/// bytes, so that the runtime has the message as data, not only as the text the program wrote to stderr; and `deferred`,
/// which an error the compiler deferred to run time calls first, to say the crash is one.
module CrashImport =
    let moduleName = "nanyx:crash"

    /// Defines `report` in `linker`, which keeps the message it is given in `reported`, and `deferred`, which sets
    /// `deferred`. The message is read through the caller's memory, so a crash while the module is being instantiated,
    /// in its start function, reports it too.
    let define (linker: Linker) (reported: string option ref) (deferred: bool ref) =
        linker.DefineFunction(moduleName, "deferred", Action(fun () -> deferred.Value <- true))
        linker.DefineFunction(
            moduleName,
            "report",
            CallerAction<int, int>(fun caller address length ->
                let memory = caller.GetMemory("memory")
                reported.Value <- Some(Text.Encoding.UTF8.GetString(memory.GetSpan(int64 address, length)))))
