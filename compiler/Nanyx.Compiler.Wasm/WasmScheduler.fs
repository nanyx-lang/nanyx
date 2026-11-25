/// The runtime helpers of tasks (#1890, see "How tasks are compiled" in `docs/src/content/deep-dives/async.md`): the
/// scheduler that runs an instance's tasks on its one stack, as state machines (see `TaskLowering`), and the scopes
/// that keep them in the def that started them. `nanyx/async` is written on the intrinsics they implement.
///
/// A task is a block of `taskSize` bytes, which no reference counts: its state, the function it runs until that
/// returns, the frame it stopped in, the scope it belongs to, the next task in the queue, the tasks after and before it
/// in its scope, its cell, its own innermost scope, and the regions of the `memory` blocks it is in (see `WasmRegions`),
/// which the scheduler swaps in when it runs it. A scope is a block of its parent, the first of its tasks, and how many
/// of its tasks have finished. Each task has a scope of its own for the tasks it starts directly, so it finishes only
/// once they have; the main task, which runs the program's own code, is made when something first needs it, and freed
/// when its outermost scope ends.
///
/// A task's `Task` value holds its cell, not its block: an object with a closure's header, counted and dropped as a
/// closure is (its type is `() -> a`, though nothing calls it), which holds what the task gave back once it has, and the
/// task's block until it has finished. The block holds the cell too. When a task finishes, the scheduler takes it out
/// of its scope and frees its block, and the cell lives on for as long as a `Task` holds it, so a task can be awaited
/// again, and a scope holds only the tasks still running, however many it has started (#2666).
///
/// The queue is first in, first out, so the order tasks run in is fixed. A task that waits, for another task, for a
/// scope's tasks or on the host, stops and goes to the back of the queue, and looks again when it is resumed. A task that
/// looks again and is still waiting has stalled. When every task in the queue has stalled since the last one that didn't,
/// nothing can happen until the host has done something, so if a task waits on the host, the scheduler makes the host's
/// wait: the function the last task to wait on it gave, for the shortest timeout any gave. Then each task that waited on
/// the host carries on, and looks again at what it waits for.
///
/// A task can be cancelled, which cancels every task it started (`Names.taskCancel`). Nothing stops it: the code that
/// waits on the host asks whether its task is, and gives back `#cancelled`. A task waiting on the host carries on once,
/// at once, when it is cancelled meanwhile, so that it can.
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
    let hostWait = NyxCompiler.Intrinsics.hostWait
    let taskCancel = NyxCompiler.Intrinsics.taskCancel
    let taskCancelled = NyxCompiler.Intrinsics.taskCancelled
    let taskDone = NyxCompiler.Intrinsics.taskDone
    let scopeWaitAny = NyxCompiler.Intrinsics.scopeWaitAny
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
    /// The resume entries of the three waits.
    let waitResume = "__nanyx_task_wait_resume"
    let scopeExitResume = "__nanyx_scope_exit_resume"
    let hostWaitResume = "__nanyx_host_wait_resume"
    let scopeWaitAnyResume = "__nanyx_scope_wait_any_resume"
    /// Whether a task of a scope has given back its value.
    let anyDone = "__nanyx_scope_any_done"
    /// How many tasks the queue holds.
    let queued = "__nanyx_task_queued"
    /// Whether the task just resumed stalled, and how many have stalled in a row.
    let stalled = "__nanyx_task_stalled"
    let stalls = "__nanyx_task_stalls"
    /// The host's wait that the scheduler makes next, if a task waits on the host (0 if none does), the shortest timeout
    /// a task gave it, in milliseconds (-1 for none), and how many it has made: a task that waits on the host carries on
    /// once it has made one more.
    let hostWaiter = "__nanyx_host_waiter"
    let hostTimeout = "__nanyx_host_timeout"
    let hostRound = "__nanyx_host_round"
    /// Makes the host's wait, when every task has stalled.
    let idle = "__nanyx_task_idle"
    /// Counts a task that stalled, or starts the count again.
    let countStall = "__nanyx_task_count_stall"
    /// The helpers they share.
    let enqueue = "__nanyx_task_enqueue"
    let dequeue = "__nanyx_task_dequeue"
    let switchTo = "__nanyx_task_switch"
    let step = "__nanyx_task_step"
    let dropObject = "__nanyx_task_drop_object"
    let pending = "__nanyx_scope_pending"
    let close = "__nanyx_scope_close"
    let leave = "__nanyx_scope_leave"
    /// Takes a finished task out of its scope and frees its block.
    let retire = "__nanyx_task_retire"
    /// Cancels the task of a block, and every task it started.
    let cancelBlock = "__nanyx_task_cancel_block"
    /// What a cell's info word calls when the cell is dropped: it drops what the task gave back.
    let cellDrop = "__nanyx_task_cell_drop"

/// The names `nanyx/async` calls: a module that calls one has the scheduler.
let intrinsics =
    set
        [ Names.taskStart; Names.taskFinish; Names.taskWait; Names.taskResult; Names.scopeEnter; Names.scopeExit; Names.hostWait
          Names.taskCancel; Names.taskCancelled; Names.taskDone; Names.scopeWaitAny ]

/// The intrinsics that can stop the task running: split points (see `CoreIrSplitPoints`).
let waits = set [ Names.taskWait; Names.scopeExit; Names.hostWait; Names.scopeWaitAny ]

/// A task's fields, by their offsets.
let private state = 0
let private work = 4
let private frame = TaskLowering.taskFrameOffset
let private scope = 12
let private next = 16
let private sibling = 20
let private cell = 24
let private ownScope = 28
let private innermostRegion = 32
let private outermostRegion = 36
/// 1 once the task is cancelled (see `Names.taskCancel`).
let private cancelled = 40
/// The task before it in its scope, whose `sibling` it is: 0 for the first.
let private before = 44
let private taskSize = 48

/// A cell's fields, after the word a closure's function is in, which is 0: what the task gave back, as a function of
/// nothing that gives it (0 until it has), and the task's block (0 once it has finished).
let private cellResult = 4
let private cellTask = 8
let private cellSize = 12

/// A task's states: made, running (it has started), and finishing (its function returned, and the tasks it started may
/// not have). Once they have, it has finished, and its block is freed (see `Names.retire`).
let private made = 0
let private started = 1
let private finishing = 2

/// A scope's fields: its parent, its first task still running, and how many of its tasks have finished.
let private parent = 0
let private first = 4
let private retired = 8
let private scopeSize = 12

/// The helpers' code, given the function table slots of the four waits' resume entries and of `Names.cellDrop`, the indirect type of a
/// function of one `i32` that gives one, as a task's function and a resume entry are, and that of a function of two,
/// as the host's wait is, which is called with its closure and the timeout.
let helpers
    (waitResumeSlot: int)
    (scopeExitResumeSlot: int)
    (hostWaitResumeSlot: int)
    (scopeWaitAnyResumeSlot: int)
    (cellDropSlot: int)
    (oneToOne: string)
    (twoToOne: string)
    : string list =
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
          "  i32.const 1"
          $"  global.set ${Names.stalled}"
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
        $"(global ${Names.queueTail} (mut i32) (i32.const 0))"
        $"(global ${Names.queued} (mut i32) (i32.const 0))"
        $"(global ${Names.stalled} (mut i32) (i32.const 0))"
        $"(global ${Names.stalls} (mut i32) (i32.const 0))"
        $"(global ${Names.hostWaiter} (mut i32) (i32.const 0))"
        $"(global ${Names.hostTimeout} (mut i32) (i32.const -1))"
        $"(global ${Names.hostRound} (mut i32) (i32.const 0))" ]
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
        $"global.get ${Names.queued}"
        "i32.const 1"
        "i32.add"
        $"global.set ${Names.queued}"
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
        $"global.get ${Names.queued}"
        "i32.const 1"
        "i32.sub"
        $"global.set ${Names.queued}"
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
      // Starts a task of the function `f`, and gives its cell, with a count of two: the caller's and the block's.
      [ $"(func ${Names.taskStart} (param $f i32) (result i32) (local $t i32) (local $s i32) (local $cur i32) (local $c i32)"
        $"call ${Names.current}"
        "local.set $cur"
        yield! zeroed "$t" taskSize
        yield! zeroed "$s" scopeSize
        "local.get $t"
        "local.get $f"
        $"i32.store offset={work}"
        // A task a cancelled task starts is cancelled from the start.
        "local.get $t"
        "local.get $cur"
        $"i32.load offset={cancelled}"
        $"i32.store offset={cancelled}"
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
        $"i32.load offset={first}"
        "if"
        "local.get $s"
        $"i32.load offset={first}"
        "local.get $t"
        $"i32.store offset={before}"
        "end"
        "local.get $s"
        "local.get $t"
        $"i32.store offset={first}"
        "local.get $t"
        $"call ${Names.enqueue}"
        $"i32.const {cellSize}"
        $"i32.const {cellDropSlot}"
        $"call ${Helpers.newObject}"
        "local.tee $c"
        $"i32.const {Header.withInfo}"
        "i32.sub"
        "i32.const 2"
        "i32.store"
        "local.get $c"
        "i32.const 0"
        "i32.store"
        "local.get $c"
        "i32.const 0"
        $"i32.store offset={cellResult}"
        "local.get $c"
        "local.get $t"
        $"i32.store offset={cellTask}"
        "local.get $t"
        "local.get $c"
        $"i32.store offset={cell}"
        "local.get $c"
        ")" ]
      // Drops what the task of the cell `p` gave back, when the cell is dropped.
      [ $"(func ${Names.cellDrop} (param $p i32) (result i32)"
        "local.get $p"
        $"i32.load offset={cellResult}"
        "if"
        "local.get $p"
        $"i32.load offset={cellResult}"
        $"call ${Names.dropObject}"
        "end"
        "i32.const 0"
        ")" ]
      // Takes the finished task `t` out of its scope, which counts it, gives up its block's reference to its cell, and
      // frees the block. It is no longer the task running, if it was.
      [ $"(func ${Names.retire} (param $t i32) (local $s i32) (local $c i32)"
        "local.get $t"
        $"i32.load offset={scope}"
        "local.set $s"
        "local.get $t"
        $"i32.load offset={before}"
        "if"
        "local.get $t"
        $"i32.load offset={before}"
        "local.get $t"
        $"i32.load offset={sibling}"
        $"i32.store offset={sibling}"
        "else"
        "local.get $s"
        "local.get $t"
        $"i32.load offset={sibling}"
        $"i32.store offset={first}"
        "end"
        "local.get $t"
        $"i32.load offset={sibling}"
        "if"
        "local.get $t"
        $"i32.load offset={sibling}"
        "local.get $t"
        $"i32.load offset={before}"
        $"i32.store offset={before}"
        "end"
        "local.get $s"
        "local.get $s"
        $"i32.load offset={retired}"
        "i32.const 1"
        "i32.add"
        $"i32.store offset={retired}"
        "local.get $t"
        $"i32.load offset={cell}"
        "local.tee $c"
        "i32.const 0"
        $"i32.store offset={cellTask}"
        "local.get $c"
        $"call ${Names.dropObject}"
        $"global.get ${Names.running}"
        "local.get $t"
        "i32.eq"
        "if"
        "i32.const 0"
        $"global.set ${Names.running}"
        "end"
        "local.get $t"
        $"call ${free}"
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
        $"i32.load offset={cell}"
        "local.get $r"
        $"i32.store offset={cellResult}"
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
      // Whether a task of the scope has not finished: it holds only those.
      [ $"(func ${Names.pending} (param $s i32) (result i32)"
        "local.get $s"
        $"i32.load offset={first}"
        "i32.const 0"
        "i32.ne"
        ")" ]
      // Frees the scope, whose tasks have all finished, and so have been taken out of it (see `Names.retire`).
      [ $"(func ${Names.close} (param $s i32)"
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
      // Waits until the task of the cell `c` has finished: until its block is gone.
      [ $"(func ${Names.taskWait} (param $c i32) (local $frame i32)"
        "local.get $c"
        $"i32.load offset={cellTask}"
        "i32.eqz"
        "if"
        "return"
        "end"
        yield! stopWith waitResumeSlot [ "local.get $c" ]
        ")" ]
      resumeWhile
          Names.waitResume
          [ "local.get $frame"; "i32.load offset=16"; $"i32.load offset={cellTask}"; "i32.const 0"; "i32.ne" ]
          []
      // Waits on the host: keeps `wait`, the host's function that waits, to make when every task has stalled, for the
      // shortest timeout given, and stops until it has made it.
      [ $"(func ${Names.hostWait} (param $wait i32) (param $timeout i32) (local $frame i32)"
        "local.get $wait"
        $"global.set ${Names.hostWaiter}"
        "local.get $timeout"
        "i32.const 0"
        "i32.ge_s"
        "if"
        $"global.get ${Names.hostTimeout}"
        "i32.const 0"
        "i32.lt_s"
        $"global.get ${Names.hostTimeout}"
        "local.get $timeout"
        "i32.gt_s"
        "i32.or"
        "if"
        "local.get $timeout"
        $"global.set ${Names.hostTimeout}"
        "end"
        "end"
        yield! stopWith hostWaitResumeSlot [ $"global.get ${Names.hostRound}" ]
        // Whether the task was cancelled when it stopped: one that is cancelled meanwhile carries on at once, once.
        "local.get $frame"
        $"call ${Names.current}"
        $"i32.load offset={cancelled}"
        "i32.store offset=20"
        ")" ]
      resumeWhile
          Names.hostWaitResume
          [ "local.get $frame"
            "i32.load offset=16"
            $"global.get ${Names.hostRound}"
            "i32.eq"
            "local.get $frame"
            "i32.load offset=20"
            $"call ${Names.current}"
            $"i32.load offset={cancelled}"
            "i32.eq"
            "i32.and" ]
          []
      // Makes the host's wait when every task in the queue has stalled since one last didn't, and a task waits on the
      // host.
      [ $"(func ${Names.idle} (local $w i32)"
        $"global.get ${Names.stalls}"
        $"global.get ${Names.queued}"
        "i32.ge_u"
        $"global.get ${Names.hostWaiter}"
        "i32.const 0"
        "i32.ne"
        "i32.and"
        "if"
        $"global.get ${Names.hostWaiter}"
        "local.tee $w"
        $"global.get ${Names.hostTimeout}"
        "local.get $w"
        "i32.load"
        $"call_indirect (type ${twoToOne})"
        "drop"
        $"global.get ${Names.hostRound}"
        "i32.const 1"
        "i32.add"
        $"global.set ${Names.hostRound}"
        "i32.const 0"
        $"global.set ${Names.hostWaiter}"
        "i32.const -1"
        $"global.set ${Names.hostTimeout}"
        "i32.const 0"
        $"global.set ${Names.stalls}"
        "end"
        ")" ]
      // Counts the task just resumed as one more that stalled, or starts the count again.
      [ $"(func ${Names.countStall} (param $stalled i32)"
        "local.get $stalled"
        "if (result i32)"
        $"global.get ${Names.stalls}"
        "i32.const 1"
        "i32.add"
        "else"
        "i32.const 0"
        "end"
        $"global.set ${Names.stalls}"
        "i32.const 0"
        $"global.set ${Names.stalled}"
        ")" ]
      // Cancels the task of the cell `c`, unless it has finished.
      [ $"(func ${Names.taskCancel} (param $c i32)"
        "local.get $c"
        $"i32.load offset={cellTask}"
        "if"
        "local.get $c"
        $"i32.load offset={cellTask}"
        $"call ${Names.cancelBlock}"
        "end"
        ")" ]
      // Cancels the task of the block `t` and every task it started, in each of its scopes, and theirs in turn.
      [ $"(func ${Names.cancelBlock} (param $t i32) (local $s i32) (local $u i32)"
        "local.get $t"
        "i32.const 1"
        $"i32.store offset={cancelled}"
        "local.get $t"
        $"i32.load offset={ownScope}"
        "local.set $s"
        "block $scopes"
        "loop $eachScope"
        "local.get $s"
        "i32.eqz"
        "br_if $scopes"
        "local.get $s"
        $"i32.load offset={first}"
        "local.set $u"
        "block $tasks"
        "loop $eachTask"
        "local.get $u"
        "i32.eqz"
        "br_if $tasks"
        "local.get $u"
        $"i32.load offset={cancelled}"
        "i32.eqz"
        "if"
        "local.get $u"
        $"call ${Names.cancelBlock}"
        "end"
        "local.get $u"
        $"i32.load offset={sibling}"
        "local.set $u"
        "br $eachTask"
        "end"
        "end"
        "local.get $s"
        $"i32.load offset={parent}"
        "local.set $s"
        "br $eachScope"
        "end"
        "end"
        ")" ]
      // Whether the task running is cancelled: none is, when there is none yet, which this doesn't make.
      [ $"(func ${Names.taskCancelled} (result i32) (local $t i32)"
        $"global.get ${Names.running}"
        "local.tee $t"
        "if (result i32)"
        "local.get $t"
        $"i32.load offset={cancelled}"
        "else"
        "i32.const 0"
        "end"
        ")" ]
      // Whether the task of the cell `c` has given back its value: its function has returned.
      [ $"(func ${Names.taskDone} (param $c i32) (result i32)"
        "local.get $c"
        $"i32.load offset={cellResult}"
        "i32.const 0"
        "i32.ne"
        ")" ]
      // Whether a task of the scope `s` has given back its value: one has finished, or one still in it has returned.
      [ $"(func ${Names.anyDone} (param $s i32) (result i32) (local $t i32)"
        "local.get $s"
        $"i32.load offset={retired}"
        "if"
        "i32.const 1"
        "return"
        "end"
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
        $"i32.const {finishing}"
        "i32.ge_u"
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
      // Waits until a task of the running task's innermost scope has given back its value.
      [ $"(func ${Names.scopeWaitAny} (local $frame i32)"
        $"call ${Names.current}"
        $"i32.load offset={ownScope}"
        $"call ${Names.anyDone}"
        "if"
        "return"
        "end"
        yield! stopWith scopeWaitAnyResumeSlot [ "i32.const 0" ]
        ")" ]
      resumeWhile
          Names.scopeWaitAnyResume
          [ $"call ${Names.current}"; $"i32.load offset={ownScope}"; $"call ${Names.anyDone}"; "i32.eqz" ]
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
      [ $"(func ${Names.step} (param $t i32) (local $f i32) (local $was i32)"
        "local.get $t"
        $"call ${Names.switchTo}"
        "local.get $t"
        $"i32.load offset={state}"
        "local.set $was"
        "i32.const 0"
        $"global.set ${Names.stalled}"
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
        $"global.get ${Names.stalled}"
        $"call ${Names.countStall}"
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
        // It stalled if it was finishing already and its tasks still haven't.
        "local.get $t"
        $"i32.load offset={ownScope}"
        $"call ${Names.pending}"
        "if"
        "local.get $was"
        $"i32.const {finishing}"
        "i32.eq"
        $"call ${Names.countStall}"
        "local.get $t"
        $"call ${Names.enqueue}"
        "return"
        "end"
        "i32.const 0"
        $"call ${Names.countStall}"
        "local.get $t"
        $"i32.load offset={ownScope}"
        $"call ${Names.close}"
        "local.get $t"
        $"call ${Names.retire}"
        ")" ]
      // The task running stopped: it has stalled if it was resumed and is still waiting. It goes to the back of the
      // queue, and the others run, until its turn comes again.
      [ $"(func ${Names.runOthers} (local $me i32) (local $t i32)"
        $"global.get ${Names.stalled}"
        $"call ${Names.countStall}"
        $"call ${Names.current}"
        "local.tee $me"
        $"call ${Names.enqueue}"
        "loop $each"
        $"call ${Names.idle}"
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
let dependencies = [ Helpers.alloc; Helpers.free; Helpers.release; Helpers.newObject; WasmRegions.Names.regionGlobals ]

/// The code that reads what the finished task of the cell on the stack gave back, a value of WebAssembly type
/// `result`: it calls the function the cell keeps it as, through `function`, a local, and the indirect type `type'` of
/// a function of one `i32` that gives a `result`.
let readResult (function': string) (type': string) : string list =
    [ $"i32.load offset={cellResult}"; $"local.tee ${function'}"; $"local.get ${function'}"; "i32.load"; $"call_indirect (type ${type'})" ]
