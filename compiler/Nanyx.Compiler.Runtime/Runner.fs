namespace Nanyx.Runtime

open System
open Wasmtime

type RunError =
    /// The WebAssembly text could not be parsed or validated.
    | InvalidModule of string
    /// The program trapped (for example `unreachable` from a match with no matching arm) or failed to instantiate.
    | Trapped of string
    /// The program ended itself with a non-zero exit code, through WASI `proc_exit`: a deferred error does, once it
    /// has written itself to stderr.
    | Exited of int

/// Runs WebAssembly text emitted by the Nanyx backend on an embedded Wasmtime engine.
///
/// The backend turns top-level expressions into a WebAssembly start function, so instantiating the module
/// runs the program. The program's output goes to this process's stdout: through WASI `fd_write` for
/// `nanyx/console` (the `__nanyx_write_stdout` intrinsic) and `dbg` of strings and integers, and through
/// host functions for the `env.dbg_tag_*` imports that `dbg(#tag)` emits.
module WasmRunner =
    let private stripImportPrefix (prefix: string) (name: string) = name.Substring prefix.Length

    let private writeLine (line: string) =
        // Match WASI's output byte for byte: a bare "\n", never the platform newline.
        Console.Out.Write(line + "\n")
        Console.Out.Flush()

    /// Defines a host function for each `env` import the module declares.
    let private defineHostImports (linker: Linker) (module': Module) =
        for import in module'.Imports do
            match import with
            | :? FunctionImport as fn when fn.ModuleName = "env" ->
                let name = fn.Name
                if name.StartsWith "dbg_tag_payload_" then
                    let tag = stripImportPrefix "dbg_tag_payload_" name
                    linker.DefineFunction(fn.ModuleName, name, Action<int>(fun payload -> writeLine $"dbg: #{tag}({payload})"))
                elif name.StartsWith "dbg_tag_" then
                    let tag = stripImportPrefix "dbg_tag_" name
                    linker.DefineFunction(fn.ModuleName, name, Action(fun () -> writeLine $"dbg: #{tag}"))
                else
                    failwith $"The program imports an unknown host function: env.{name}"
            | _ -> ()

    /// Whether `ex` is the program ending itself through WASI `proc_exit` with exit code 0.
    let private exitedCleanly (ex: WasmtimeException) = ex.ExitCode.HasValue && ex.ExitCode.Value = 0

    /// Instantiates the module described by `wat`, which runs its start function, then calls its export `start` if it
    /// is given and the module exports it, and gives what `read` makes of the instance afterwards: `None` when the program ended itself while it
    /// was being instantiated, so there is no instance.
    let private execute (wat: string) (start: string option) (read: Instance option -> 'a) : Result<'a, RunError> =
        use engine = new Engine()
        let moduleResult =
            try Ok (Module.FromText(engine, "nanyx", wat))
            with :? WasmtimeException as ex -> Error (InvalidModule ex.Message)
        match moduleResult with
        | Error err -> Error err
        | Ok module' ->
            use module' = module'
            use linker = new Linker(engine)
            use store = new Store(engine)
            linker.DefineWasi()
            store.SetWasiConfiguration(
                WasiConfiguration()
                    .WithInheritedStandardInput()
                    .WithInheritedStandardOutput()
                    .WithInheritedStandardError())
            try
                defineHostImports linker module'
                let instance = linker.Instantiate(store, module')
                // A program with no top-level code has no start function to export.
                match start |> Option.map instance.GetAction with
                | Some null
                | None -> ()
                | Some action ->
                    try action.Invoke()
                    with :? WasmtimeException as ex when exitedCleanly ex -> ()
                Ok (read (Some instance))
            with
            | :? WasmtimeException as ex when exitedCleanly ex -> Ok (read None)
            | :? WasmtimeException as ex when ex.ExitCode.HasValue -> Error (Exited ex.ExitCode.Value)
            | :? WasmtimeException as ex -> Error (Trapped ex.Message)

    /// Instantiates (and therefore runs) the module described by `wat`.
    let run (wat: string) : Result<unit, RunError> = execute wat None ignore

    /// Runs the module described by `wat`, which exports its start function as `start` rather than running it when
    /// instantiated, and gives the values of its exported `i32` globals `globals` once `start` has returned. A global
    /// the module does not export counts as 0: a module that never allocates has no allocation counters.
    let runReading (start: string) (globals: string array) (wat: string) : Result<int array, RunError> =
        execute wat (Some start) (fun instance ->
            globals |> Array.map (fun name ->
                match instance |> Option.map _.GetGlobal(name) with
                | Some null | None -> 0
                | Some global' -> global'.GetValue() :?> int))
