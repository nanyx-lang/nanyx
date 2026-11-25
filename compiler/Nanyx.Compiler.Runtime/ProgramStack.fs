namespace Nanyx.Runtime

open Wasmtime

/// The stacks a program runs on. Wasmtime runs WebAssembly on the native stack of the thread that calls into it, and
/// stops a program that recurses too deeply once it has used `guestStack` bytes of it, with a `StackOverflow` trap
/// (see `WasmRunner.trapped`). That check only comes first if the thread has more stack than that left. Otherwise the
/// program reaches the end of the thread's stack first, which Linux and macOS turn into the same trap, but which on
/// Windows stops the whole process (0xC00000FD, #2708). So a program runs on a thread of its own, of `threadStack`
/// bytes, well past the guest's limit, whichever thread called it: Windows gives its main thread 1 MB.
module ProgramStack =
    /// How much stack a program may use before it runs out: Wasmtime's own default, set here so that `threadStack`
    /// is sure to be past it.
    let guestStack = 512 * 1024

    /// The stack of the thread a program runs on: the guest's, and room past it for the runtime's own calls, the host
    /// functions a program calls among them.
    let threadStack = 8 * 1024 * 1024

    /// `config` with the guest's stack limit, `guestStack`.
    let configured (config: Config) : Config = config.WithMaximumStackSize guestStack

    /// A new engine with the guest's stack limit, configured by `configure` too.
    let engine (configure: Config -> Config) : Engine = new Engine(configure (configured (new Config())))

    /// Runs `action`, which runs a program, on a thread with a stack of `threadStack` bytes, waiting for it, and gives
    /// its result or raises what it raised.
    let run (action: unit -> 'T) : 'T = Nanyx.LargeStack.runWithStack threadStack action
