namespace Nanyx.Runtime

open System.Threading
open Wasmtime

/// The runtime's own import for the scheduler's wait, `nanyx:async`, on which the console and HTTP hosts declare the
/// wait the scheduler makes when every task is waiting on the host (see "How tasks take turns" in the Async page of the
/// Deep dives):
///
/// - `wait(timeout)` blocks until something the program started through the runtime, such as a request
///   (`HttpImport`'s `start`), may have finished, or `timeout` milliseconds have passed, or for good with `-1`.
///
/// What finishes signals the store's `Waits`. A signal the program has already seen, such as one for a request it polled
/// before it waited, makes a wait return at once: the tasks look again, and wait again.
module AsyncImport =
    /// The import module's name.
    [<Literal>]
    let moduleName = "nanyx:async"

    /// What a store's `wait` waits on: a signal from each thing that finished.
    type Waits() =
        let finished = new SemaphoreSlim(0)

        /// Says that something the program started has finished.
        member _.Signal() = finished.Release() |> ignore

        /// Waits for a signal, or for `timeout` milliseconds, or for good with a negative timeout.
        member _.Wait(timeout: int) = finished.Wait(if timeout < 0 then Timeout.Infinite else timeout) |> ignore

    /// Defines `wait` on `linker`, for one store, on its `waits`.
    let define (linker: Linker) (waits: Waits) =
        linker.DefineFunction(moduleName, "wait", System.Action<int>(fun timeout -> waits.Wait timeout))
