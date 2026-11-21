namespace Nanyx.Runtime

open System
open System.IO
open System.Security.Cryptography
open Wasmtime

/// Wraps a core module that follows wit-component's conventions, such as one the backend writes with
/// `WasmOptions.Component`, as a WASI 0.3 component.
///
/// The wrapping is wit-component's own: `Nanyx.Componentizer`, a small Rust crate in the repository, built to
/// `wasm32-wasip1` when this assembly is built and embedded in it, run here as a WASI command on the embedded Wasmtime.
/// It validates the component it writes. Compiling it takes a second or two, so the compiled module is kept in the
/// user's cache (`cacheDirectory`) and read back from there.
module Componentizer =
    /// The name the componentizer's WebAssembly is embedded under (see `Nanyx.Compiler.Runtime.fsproj`).
    let private resourceName = "nanyx-componentizer.wasm"

    /// The componentizer's WebAssembly, if this assembly was built with it.
    let private componentizerBytes =
        lazy
            (match Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream resourceName with
             | null -> None
             | stream ->
                 use stream = stream
                 use bytes = new MemoryStream()
                 stream.CopyTo bytes
                 Some (bytes.ToArray()))

    /// Where the compiled componentizer is kept: `NANYX_CACHE`, if it is set, or else `.nanyx/cache` in the user's home
    /// directory.
    let cacheDirectory () =
        match Environment.GetEnvironmentVariable "NANYX_CACHE" with
        | null
        | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".nanyx", "cache")
        | directory -> directory

    /// The componentizer compiled for `engine`: read from the cache when it holds a compilation of these bytes that
    /// this Wasmtime can load, or else compiled, and cached for next time if the cache can be written.
    let private load (engine: Engine) (bytes: byte array) =
        let hash = Convert.ToHexString(SHA256.HashData bytes).Substring(0, 16).ToLowerInvariant()
        let cached = Path.Combine(cacheDirectory (), $"componentizer-{hash}.cwasm")
        let fromCache () =
            if File.Exists cached then
                try Some (Module.DeserializeFile(engine, "componentizer", cached)) with :? WasmtimeException -> None
            else None
        match fromCache () with
        | Some module' -> module'
        | None ->
            let module' = Module.FromBytes(engine, "componentizer", ReadOnlySpan bytes)
            try
                Directory.CreateDirectory(Path.GetDirectoryName cached) |> ignore
                // Written beside the cache and moved into place, so that another build never reads half of it.
                let partial = cached + "." + Guid.NewGuid().ToString("N")
                File.WriteAllBytes(partial, module'.Serialize())
                File.Move(partial, cached, true)
            with
            | :? IOException
            | :? UnauthorizedAccessException -> ()
            module'

    /// `core` wrapped as a component of the world `world`, whose WIT is the package in the directory `wit` (with the
    /// packages it uses under `wit/deps`), or why it can't be: wit-component's message.
    let componentize (core: byte array) (wit: string) (world: string) : Result<byte array, string> =
        match componentizerBytes.Force() with
        | None -> Error $"This build of nanyx has no componentizer ({resourceName}): it was built without Rust's wasm32-wasip1 target"
        | Some componentizer ->
            let work = Directory.CreateTempSubdirectory "nanyx-componentize-"
            try
                let inside name = Path.Combine(work.FullName, name)
                File.WriteAllBytes(inside "core.wasm", core)
                // The store holds error.txt open until it is disposed, and Windows won't let it be read or deleted
                // until then, so the run is over, and the store gone, before either.
                let run () =
                    use engine = new Engine()
                    use module' = load engine componentizer
                    use linker = new Linker(engine)
                    use store = new Store(engine)
                    linker.DefineWasi()
                    store.SetWasiConfiguration(
                        WasiConfiguration()
                            .WithArgs([ "nanyx-componentizer"; "/work/core.wasm"; "/wit"; world; "/work/component.wasm" ])
                            .WithPreopenedDirectory(work.FullName, "/work", true)
                            .WithPreopenedDirectory(Path.GetFullPath wit, "/wit", false)
                            .WithStandardError(inside "error.txt"))
                    try
                        linker.Instantiate(store, module').GetAction("_start").Invoke()
                        0
                    with :? WasmtimeException as ex when ex.ExitCode.HasValue -> ex.ExitCode.Value
                let exitCode = run ()
                let error () = if File.Exists(inside "error.txt") then File.ReadAllText(inside "error.txt").Trim() else ""
                if exitCode = 0 then Ok (File.ReadAllBytes(inside "component.wasm"))
                else Error (error ())
            finally
                work.Delete true
