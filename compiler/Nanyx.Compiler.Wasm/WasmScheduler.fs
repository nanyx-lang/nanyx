/// The runtime helpers of tasks (#1890, see "How tasks are compiled" in `docs/src/content/deep-dives/async.md`): the
/// scheduler that runs an instance's tasks on its one stack, as state machines (see `TaskLowering`), and the scopes
/// that keep them in the def that started them. `nanyx/async` is written on the intrinsics they implement.
///
/// A task is a block of `taskSize` bytes, which no reference counts: its state, the function it runs until that
/// returns, the frame it stopped in, the scope it belongs to, the next task in the queue and in its scope, the function
/// that gives its result, its own innermost scope, and the regions of the `memory` blocks it is in (see `WasmRegions`),
/// which the scheduler swaps in when it runs it. A scope is a block of its parent and the first of its tasks. Each task
/// has a scope of its own for the tasks it starts directly, so it finishes only once they have; the main task, which
/// runs the program's own code, is made when something first needs it, and freed when its outermost scope ends.
///
/// The queue is first in, first out, so the order tasks run in is fixed. A task that waits, for another task or for a
/// scope's tasks, stops and goes to the back of the queue, and looks again when it is resumed.
module internal Transpiler.Wasm.WasmScheduler

open Transpiler.Wasm.WasmRuntime

/// The names of the helpers and globals. Those `nanyx/async` calls are its intrinsics.
module Names =
    let taskStart = NyxCompiler.Intrinsics.taskStart
    let taskFinish = NyxCompiler.Intrinsics.taskFinish
    let taskWait = NyxCompiler.Intrinsics.taskWait
    let taskResult = NyxCompiler.Intrinsics.taskResult
    let scopeEnter = NyxCompiler.Intrinsics.scopeEnter
    let scopeExit = NyxCompiler.Intrinsics.scopeExit
    /// The task running, and the main task, the program's own code.
    let running = "__nanyx_task"
    let main = "__nanyx_task_main"
    /// The first and the last task of the queue.
    let queueHead = "__nanyx_task_head"
    let queueTail = "__nanyx_task_tail"
    /// The task running, made the main task if there is none.
    let current = TaskLowering.Names.currentTask
    /// Runs the other tasks until the running one's turn comes again.
    let runOthers = TaskLowering.Names.runOthers
    /// Frees the main task when it holds no scope.
    let settle = TaskLowering.Names.settleMain
    /// The resume entries of the two waits.
    let waitResume = "__nanyx_task_wait_resume"
    let scopeExitResume = "__nanyx_scope_exit_resume"
    /// The helpers they share.
    let enqueue = "__nanyx_task_enqueue"
    let dequeue = "__nanyx_task_dequeue"
    let switchTo = "__nanyx_task_switch"
    let step = "__nanyx_task_step"
    let dropObject = "__nanyx_task_drop_object"
    let pending = "__nanyx_scope_pending"
    let close = "__nanyx_scope_close"
    let leave = "__nanyx_scope_leave"

/// The names `nanyx/async` calls: a module that calls one has the scheduler.
let intrinsics = set [ Names.taskStart; Names.taskFinish; Names.taskWait; Names.taskResult; Names.scopeEnter; Names.scopeExit ]

/// The intrinsics that can stop the task running: split points (see `CoreIrSplitPoints`).
let waits = set [ Names.taskWait; Names.scopeExit ]

/// A task's fields, by their offsets.
let private state = 0
let private work = 4
let private frame = TaskLowering.taskFrameOffset
let private scope = 12
let private next = 16
let private sibling = 20
/// Where `taskResult`, which the emitter writes out where it is used, reads the function that gives a task's result.
let resultOffset = 24
let private ownScope = 28
let private innermostRegion = 32
let private outermostRegion = 36
let private taskSize = 40

/// A task's states: made, running (it has started), finishing (its function returned, and the tasks it started may
/// not have), and finished.
let private made = 0
let private started = 1
let private finishing = 2
let private finished = 3

/// A scope's fields.
let private parent = 0
let private first = 4
let private scopeSize = 8

/// The helpers' code, given the function table slots of the two waits' resume entries, and the indirect type of a
/// function of one `i32` that gives one, as a task's function and a resume entry are.
let helpers (waitResumeSlot: int) (scopeExitResumeSlot: int) (oneToOne: string) : string list =
    let alloc = Helpers.alloc
    let free = Helpers.free
    let zeroed (local: string) (size: int) =
        [ $"i32.const {size}"; $"call ${alloc}"; $"local.set {local}" ]
        @ (List.init (size / 4) (fun index -> [ $"local.get {local}"; "i32.const 0"; $"i32.store offset={index * 4}" ]) |> List.concat)
    // The wait that stops: a frame of its own, with its resume entry and what it waits on, left for whoever resumes it.
    let stopWith (resumeSlot: int) (waitedOn: string list) =
        [ "i32.const 24"
          $"call ${alloc}"
          "local.tee $frame"
          $"i32.const {resumeSlot}"
          "i32.store"
          "local.get $frame"
          yield! waitedOn
          "i32.store offset=16"
          "local.get $frame"
          $"global.set ${TaskLowering.Names.suspended}"
          "i32.const 1"
          $"global.set ${TaskLowering.Names.suspending}" ]
    // A resume entry that looks again, and stops again with the same frame while `stillWaiting` leaves 1.
    let resumeWhile (name: string) (stillWaiting: string list) (finally': string list) =
        [ $"(func ${name} (param $frame i32) (result i32)"
          yield! stillWaiting
          "if"
          "  local.get $frame"
          $"  global.set ${TaskLowering.Names.suspended}"
          "  i32.const 1"
          $"  global.set ${TaskLowering.Names.suspending}"
          "  i32.const 0"
          "  return"
          "end"
          "local.get $frame"
          $"call ${free}"
          yield! finally'
          "i32.const 0"
          ")" ]
    [ [ $"(global ${Names.running} (mut i32) (i32.const 0))"
        $"(global ${Names.main} (mut i32) (i32.const 0))"
        $"(global ${Names.queueHead} (mut i32) (i32.const 0))"
        $"(global ${Names.queueTail} (mut i32) (i32.const 0))" ]
      [ $"(func ${Names.current} (result i32) (local $t i32)"
        $"global.get ${Names.running}"
        "local.tee $t"
        "i32.eqz"
        "if"
        yield! zeroed "$t" taskSize
        "local.get $t"
        $"i32.const {started}"
        $"i32.store offset={state}"
        "local.get $t"
        $"global.set ${Names.running}"
        "local.get $t"
        $"global.set ${Names.main}"
        "end"
        "local.get $t"
        ")" ]
      [ $"(func ${Names.enqueue} (param $t i32)"
        "local.get $t"
        "i32.const 0"
        $"i32.store offset={next}"
        $"global.get ${Names.queueTail}"
        "if"
        $"global.get ${Names.queueTail}"
        "local.get $t"
        $"i32.store offset={next}"
        "else"
        "local.get $t"
        $"global.set ${Names.queueHead}"
        "end"
        "local.get $t"
        $"global.set ${Names.queueTail}"
        ")" ]
      [ $"(func ${Names.dequeue} (result i32) (local $t i32)"
        $"global.get ${Names.queueHead}"
        "local.tee $t"
        "if"
        "local.get $t"
        $"i32.load offset={next}"
        $"global.set ${Names.queueHead}"
        $"global.get ${Names.queueHead}"
        "i32.eqz"
        "if"
        "i32.const 0"
        $"global.set ${Names.queueTail}"
        "end"
        "end"
        "local.get $t"
        ")" ]
      [ $"(func ${Names.taskStart} (param $f i32) (result i32) (local $t i32) (local $s i32) (local $cur i32)"
        $"call ${Names.current}"
        "local.set $cur"
        yield! zeroed "$t" taskSize
        yield! zeroed "$s" scopeSize
        "local.get $t"
        "local.get $f"
        $"i32.store offset={work}"
        "local.get $t"
        "local.get $s"
        $"i32.store offset={ownScope}"
        // It belongs to the running task's innermost scope, first among its tasks.
        "local.get $cur"
        $"i32.load offset={ownScope}"
        "local.set $s"
        "local.get $t"
        "local.get $s"
        $"i32.store offset={scope}"
        "local.get $t"
        "local.get $s"
        $"i32.load offset={first}"
        $"i32.store offset={sibling}"
        "local.get $s"
        "local.get $t"
        $"i32.store offset={first}"
        "local.get $t"
        $"call ${Names.enqueue}"
        "local.get $t"
        ")" ]
      [ $"(func ${Names.dropObject} (param $p i32)"
        "local.get $p"
        $"i32.const {Header.withInfo}"
        "i32.sub"
        $"call ${Helpers.release}"
        "i32.eqz"
        "if"
        "return"
        "end"
        "local.get $p"
        "local.get $p"
        $"i32.const {Header.withInfo - Header.infoOffset}"
        "i32.sub"
        "i32.load"
        $"call_indirect (type ${oneToOne})"
        "drop"
        "local.get $p"
        $"i32.const {Header.withInfo}"
        "i32.sub"
        $"call ${free}"
        ")" ]
      [ $"(func ${Names.taskFinish} (param $r i32)"
        $"call ${Names.current}"
        "local.get $r"
        $"i32.store offset={resultOffset}"
        ")" ]
      [ $"(func ${Names.scopeEnter} (local $cur i32) (local $s i32)"
        $"call ${Names.current}"
        "local.set $cur"
        yield! zeroed "$s" scopeSize
        "local.get $s"
        "local.get $cur"
        $"i32.load offset={ownScope}"
        $"i32.store offset={parent}"
        "local.get $cur"
        "local.get $s"
        $"i32.store offset={ownScope}"
        ")" ]
      // Whether a task of the scope has not finished.
      [ $"(func ${Names.pending} (param $s i32) (result i32) (local $t i32)"
        "local.get $s"
        $"i32.load offset={first}"
        "local.set $t"
        "block $done"
        "loop $each"
        "local.get $t"
        "i32.eqz"
        "br_if $done"
        "local.get $t"
        $"i32.load offset={state}"
        $"i32.const {finished}"
        "i32.ne"
        "if"
        "i32.const 1"
        "return"
        "end"
        "local.get $t"
        $"i32.load offset={sibling}"
        "local.set $t"
        "br $each"
        "end"
        "end"
        "i32.const 0"
        ")" ]
      // Drops what each task of the scope gave back, and frees them and the scope.
      [ $"(func ${Names.close} (param $s i32) (local $t i32) (local $after i32)"
        "local.get $s"
        $"i32.load offset={first}"
        "local.set $t"
        "block $done"
        "loop $each"
        "local.get $t"
        "i32.eqz"
        "br_if $done"
        "local.get $t"
        $"i32.load offset={sibling}"
        "local.set $after"
        "local.get $t"
        $"i32.load offset={resultOffset}"
        "if"
        "local.get $t"
        $"i32.load offset={resultOffset}"
        $"call ${Names.dropObject}"
        "end"
        "local.get $t"
        $"call ${free}"
        "local.get $after"
        "local.set $t"
        "br $each"
        "end"
        "end"
        "local.get $s"
        $"call ${free}"
        ")" ]
      // Frees the main task when it is running and holds no scope, so nothing is left of it: it is made again if
      // anything needs it later.
      [ $"(func ${Names.settle} (local $t i32)"
        $"global.get ${Names.main}"
        "local.tee $t"
        $"global.get ${Names.running}"
        "i32.eq"
        "local.get $t"
        "i32.const 0"
        "i32.ne"
        "i32.and"
        "if"
        "local.get $t"
        $"i32.load offset={ownScope}"
        "i32.eqz"
        "if"
        "local.get $t"
        $"call ${free}"
        "i32.const 0"
        $"global.set ${Names.main}"
        "i32.const 0"
        $"global.set ${Names.running}"
        "end"
        "end"
        ")" ]
      // Ends the running task's innermost scope, whose tasks have all finished. The main task is freed with its
      // outermost scope.
      [ $"(func ${Names.leave} (local $cur i32) (local $s i32)"
        $"call ${Names.current}"
        "local.tee $cur"
        $"i32.load offset={ownScope}"
        "local.set $s"
        "local.get $cur"
        "local.get $s"
        $"i32.load offset={parent}"
        $"i32.store offset={ownScope}"
        "local.get $s"
        $"call ${Names.close}"
        $"call ${Names.settle}"
        ")" ]
      [ $"(func ${Names.scopeExit} (local $frame i32)"
        $"call ${Names.current}"
        $"i32.load offset={ownScope}"
        $"call ${Names.pending}"
        "if"
        yield! stopWith scopeExitResumeSlot [ "i32.const 0" ]
        "return"
        "end"
        $"call ${Names.leave}"
        ")" ]
      resumeWhile Names.scopeExitResume [ $"call ${Names.current}"; $"i32.load offset={ownScope}"; $"call ${Names.pending}" ] [ $"call ${Names.leave}" ]
      [ $"(func ${Names.taskWait} (param $h i32) (local $frame i32)"
        "local.get $h"
        $"i32.load offset={state}"
        $"i32.const {finished}"
        "i32.eq"
        "if"
        "return"
        "end"
        yield! stopWith waitResumeSlot [ "local.get $h" ]
        ")" ]
      resumeWhile
          Names.waitResume
          [ "local.get $frame"; "i32.load offset=16"; $"i32.load offset={state}"; $"i32.const {finished}"; "i32.ne" ]
          []
      // Makes `t` the task running, keeping the regions of the one that was.
      [ $"(func ${Names.switchTo} (param $t i32) (local $was i32)"
        $"global.get ${Names.running}"
        "local.tee $was"
        "if"
        "local.get $was"
        $"global.get {WasmRegions.innermostGlobal}"
        $"i32.store offset={innermostRegion}"
        "local.get $was"
        $"global.get {WasmRegions.outermostGlobal}"
        $"i32.store offset={outermostRegion}"
        "end"
        "local.get $t"
        $"global.set ${Names.running}"
        "local.get $t"
        $"i32.load offset={innermostRegion}"
        $"global.set {WasmRegions.innermostGlobal}"
        "local.get $t"
        $"i32.load offset={outermostRegion}"
        $"global.set {WasmRegions.outermostGlobal}"
        ")" ]
      // Runs `t` until it stops or finishes: it starts its function, or resumes the frame it stopped in, or, once its
      // function has returned, looks whether the tasks it started have finished. It goes back on the queue unless it
      // has finished, when its own scope ends.
      [ $"(func ${Names.step} (param $t i32) (local $f i32)"
        "local.get $t"
        $"call ${Names.switchTo}"
        "local.get $t"
        $"i32.load offset={state}"
        $"i32.const {made}"
        "i32.eq"
        "if"
        "local.get $t"
        $"i32.const {started}"
        $"i32.store offset={state}"
        "local.get $t"
        $"i32.load offset={work}"
        "local.tee $f"
        "local.get $f"
        "i32.load"
        $"call_indirect (type ${oneToOne})"
        "drop"
        "else"
        "local.get $t"
        $"i32.load offset={state}"
        $"i32.const {started}"
        "i32.eq"
        "if"
        "local.get $t"
        $"i32.load offset={frame}"
        "local.tee $f"
        "local.get $f"
        "i32.load"
        $"call_indirect (type ${oneToOne})"
        "drop"
        "end"
        "end"
        $"global.get ${TaskLowering.Names.suspending}"
        "if"
        "i32.const 0"
        $"global.set ${TaskLowering.Names.suspending}"
        "local.get $t"
        $"global.get ${TaskLowering.Names.suspended}"
        $"i32.store offset={frame}"
        "local.get $t"
        $"call ${Names.enqueue}"
        "return"
        "end"
        // Its function has returned: it gives it up.
        "local.get $t"
        $"i32.load offset={state}"
        $"i32.const {started}"
        "i32.eq"
        "if"
        "local.get $t"
        $"i32.const {finishing}"
        $"i32.store offset={state}"
        "local.get $t"
        $"i32.load offset={work}"
        $"call ${Names.dropObject}"
        "local.get $t"
        "i32.const 0"
        $"i32.store offset={work}"
        "end"
        "local.get $t"
        $"i32.load offset={ownScope}"
        $"call ${Names.pending}"
        "if"
        "local.get $t"
        $"call ${Names.enqueue}"
        "return"
        "end"
        "local.get $t"
        $"i32.load offset={ownScope}"
        $"call ${Names.close}"
        "local.get $t"
        "i32.const 0"
        $"i32.store offset={ownScope}"
        "local.get $t"
        $"i32.const {finished}"
        $"i32.store offset={state}"
        ")" ]
      [ $"(func ${Names.runOthers} (local $me i32) (local $t i32)"
        $"call ${Names.current}"
        "local.tee $me"
        $"call ${Names.enqueue}"
        "loop $each"
        $"call ${Names.dequeue}"
        "local.tee $t"
        "local.get $me"
        "i32.eq"
        "if"
        "local.get $me"
        $"call ${Names.switchTo}"
        "return"
        "end"
        "local.get $t"
        $"call ${Names.step}"
        "br $each"
        "end"
        ")" ] ]
    |> List.map (fun code ->
        match code with
        | header :: rest when header.StartsWith "(func" -> "  " + header + "\n" + (rest |> List.map (fun line -> "    " + line) |> String.concat "\n")
        | lines -> lines |> List.map (fun line -> "  " + line) |> String.concat "\n")

/// The helpers the scheduler calls.
let dependencies = [ Helpers.alloc; Helpers.free; Helpers.release; WasmRegions.Names.regionGlobals ]

/// The code that reads what the finished task of the handle on the stack gave back, a value of WebAssembly type
/// `result`: it calls the function the task keeps it as, through `function`, a local, and the indirect type `type'` of
/// a function of one `i32` that gives a `result`.
let readResult (function': string) (type': string) : string list =
    [ $"i32.load offset={resultOffset}"; $"local.tee ${function'}"; $"local.get ${function'}"; "i32.load"; $"call_indirect (type ${type'})" ]
