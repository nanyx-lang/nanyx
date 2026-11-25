/// A state machine's frame can be freed without being resumed, with every block it and the frames of the calls it
/// stopped in hold (#2426, see "How tasks are compiled" in docs/src/content/deep-dives/async.md). Nothing in the
/// language drops a frame yet, so the programs are compiled with `WasmOptions.FrameDrops`, and with every call of a
/// function value that can wait stopping once first (`WasmOptions.YieldAtEveryWait`), so that a call stops; the tests
/// call the frame drop helper themselves.
module TranspilerWasmTests.Features.Async.FrameDrop

open Xunit
open Transpiler.Wasm.CodeGen
open TranspilerWasmTestHelpers

/// The functions the programs wait through: `call` calls the function it is given, which can wait.
let private waiting =
    normalized
        """import nanyx/async as (Async)

def call: [Async] ([Async] () -> a) -> a = { f -> f() }
"""

// The names of the runtime's part (see `TaskLowering.Names`).
let private suspending = "__nanyx_suspending"
let private suspended = "__nanyx_suspended"
let private frameDrop = "__nanyx_frame_drop"
let private resumeEntry (name: string) = $"{name}__resume"
let private dropEntry (name: string) = $"{name}__drop"

let private dropping = { checkedOptions with Inline = false; FrameDrops = true; YieldAtEveryWait = true }

/// `source` compiled with `options`, instantiated with the frame drop helper, the globals a stopped call leaves its
/// frame in and the resume entries of the state machines `resumed` exported.
let private instantiate (options: WasmOptions) (resumed: string list) (source: string) =
    let wat = transpileWatWith options source
    let exported =
        [ suspending, "global"
          suspended, "global"
          frameDrop, "func"
          yield! resumed |> List.map (fun name -> resumeEntry name, "func") ]
        |> List.map (fun (name, kind) -> $"  (export \"{name}\" ({kind} ${name}))")
        |> String.concat "\n"
    instantiateWat (wat.Substring(0, wat.LastIndexOf ')') + exported + "\n)") []

/// Calls `name` with `argument` in `program`, which must stop, and drops the frame it stopped in instead of resuming it.
let private stopAndDrop (program: WasmProgram) (name: string) (argument: int) =
    program.Call(name, argument) |> ignore
    Assert.Equal(1, program.Global suspending)
    program.Run(frameDrop, program.Global suspended)

[<Fact>]
let ``Dropping a stopped function's frame frees what it keeps and the frames of the calls it stopped in`` () =
    let source =
        waiting
        + normalized
            """
def kept: [Async] int -> int = { n ->
  def items = [n, n + 1, n + 2]
  def text = "number {n}"
  def got = call({ n })
  list.length(items) + string.length(text) + got
}
"""
    use program = instantiate dropping [] source
    let liveBefore = program.LiveBlocks
    stopAndDrop program "kept" 4
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``Dropping a frame frees what the function keeps in the state it stopped in`` () =
    let source =
        waiting
        + normalized
            """
def twice: [Async] int -> int = { n ->
  def first = [n, n]
  def got = call({ list.length(first) })
  def second = "after {got}"
  def more = call({ string.length(second) })
  list.length(first) + more
}
"""
    use program = instantiate dropping [ "twice" ] source
    let liveBefore = program.LiveBlocks
    // It stops at the first call, is resumed, and stops at the second.
    program.Call("twice", 2) |> ignore
    program.SetGlobal(suspending, 0)
    stopAndDrop program (resumeEntry "twice") (program.Global suspended)
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``Dropping a frame chain gives up what a function borrows only in the frame of the function that owns it`` () =
    let source =
        waiting
        + normalized
            """
def counted: [Async] list(int) -> int = { items ->
  def got = call({ 1 })
  list.length(items) + got
}

def outer: [Async] int -> int = { n ->
  def items = [n, n]
  def got = counted(items)
  list.length(items) + got
}
"""
    use program = instantiate dropping [] source
    let liveBefore = program.LiveBlocks
    stopAndDrop program "outer" 3
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``A program compiled without frame drops has no drop entries`` () =
    let wat = transpileWatWith { checkedOptions with YieldAtEveryWait = true } (waiting + "\ndef kept: [Async] int -> int = { n -> call({ n }) + 1 }\n")
    Assert.DoesNotContain(frameDrop, wat)
    Assert.DoesNotContain(dropEntry "kept", wat)
