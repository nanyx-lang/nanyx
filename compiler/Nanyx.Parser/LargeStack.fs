/// Running work that recurses once per level of the code it reads: the checker once per operator of a chain, the
/// formatter's comparison once per node, the backend's lowering once per statement of a block. Generated code can nest
/// thousands deep, more than the main thread's 1 MB stack holds, and a stack overflow cannot be caught.
module Nanyx.LargeStack

/// The stack `run` gives its work, reserved but only committed as it is used. The checker takes about 28 KB of it for
/// each operator of a chain in a Debug build (much less in Release), so this holds a chain of 10,000 with room over.
let private size = 1024 * 1024 * 1024

/// Runs `action` on a thread with a large stack, waiting for it, and gives its result or raises what it raised.
let run (action: unit -> 'T) : 'T =
    let mutable result = Unchecked.defaultof<'T>
    let mutable error: exn option = None
    let thread =
        System.Threading.Thread(
            (fun () ->
                try result <- action ()
                with ex -> error <- Some ex),
            size)
    thread.Start()
    thread.Join()
    match error with
    | Some ex -> raise ex
    | None -> result
