namespace Transpiler.Wasm

/// The entry a component's core module exports, as its host's world says (see `ComponentAbi`).
type ComponentEntry =
    /// `wasi:cli/run`'s `run`, lifted synchronously: the program runs, and `run` returns `ok`.
    | CliRun
    /// `wasi:http/handler`'s `handle`, lifted with the callback ABI: the program answers the request, `handle` returns
    /// the response, and the component then writes its body as the host reads it.
    | HttpHandler

module ComponentEntry =
    /// The entry a component of the world `world`, as WIT names it, exports: a `wasi:http/service` world's is the
    /// handler, and any other's is `run`.
    let ofWorld (world: string) =
        if world.StartsWith "wasi:http/service@" then HttpHandler else CliRun
