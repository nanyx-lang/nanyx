/// A component's entry, `wasi:cli/run`'s `run` or `wasi:http/handler`'s `handle`, lifted with the component model's
/// callback ABI, for a program whose start function can stop (#2240, see "Running with the callback ABI" on the
/// Components page of the Deep dives). The start function is then a state machine (see `TaskLowering`), and its task,
/// the main task, is one the scheduler runs as it runs the others (`WasmScheduler`):
///
/// - The entry calls the start function. If it finishes, the entry tears the program's cached values down and gives
///   its result: `run` gives `ok` with `task.return`, and `handle` the response, whose body it then writes
///   (`ComponentAbi.handleFinish`); then it returns EXIT. If the start function stops, its frame goes to the main task,
///   which goes on the queue, and the scheduler runs (`Names.drive`).
/// - The scheduler runs the tasks in turn, as `WasmScheduler.Names.runOthers` does, resuming the main task's frame when
///   its turn comes, until the start function finishes. When every task waits on the host, it makes the host's wait,
///   as `runOthers` does, but with `ComponentAbi.Callback.atTop` set, so the component root's wait on its shared
///   waitable set doesn't block: `ComponentAbi`'s `wait` keeps the set instead, and the scheduler returns WAIT, with
///   the set, to the host.
/// - The host calls the callback with the event when one comes. The callback keeps it, and makes the host's wait the
///   scheduler last made again, whose `wait` gives it the event, so the root keeps it for the task that waits on it.
///   Then the scheduler runs again. TASK_CANCELLED cancels the main task, and so every task, whose waits on the host
///   cancel their subtasks.
///
/// The scheduler sets `atTop` only around its own wait, so a wait on the host under anything else that runs the tasks,
/// `blockOn` in a cached value's getter, would block in `[waitable-set-wait]`, which `run`, an `async func`, may. None
/// does: a cached value can't require a context, so its tasks can't wait on the host.
module internal Transpiler.Wasm.ComponentCallback

open Transpiler.Wasm.WasmAllocator

/// The names of the functions and globals.
module Names =
    /// Runs the tasks until the start function finishes, or every task waits on the host.
    let drive = "$__nanyx_component_drive"
    /// Gives the frame the main task stopped in to it, and puts it on the queue.
    let stopped = "$__nanyx_component_stopped"
    /// The main task, whose frame is the start function's.
    let main = "$__nanyx_component_main"
    /// The host's wait the scheduler made last, which the callback makes again with the event.
    let waiter = "$__nanyx_component_waiter"

/// The canonical ABI's codes: what the callback returns to wait on a set (with the set above the low 4 bits), and the
/// event that cancels the task.
let private wait = 2
let private taskCancelled = 6

/// The entry `entry`, its callback and the scheduler they run, for the start function `start`, a state machine whose
/// resume entry has the indirect type `startResume`, and whose cached values `teardown` tears down. A host's wait is a
/// function value of the indirect type `waiterType`.
let functions (entry: ComponentEntry) (start: string) (startResume: string) (waiterType: string) (teardown: string list) =
    let scheduler (name: string) = "$" + name
    // The entry's header, what it does before the start function, and the locals what it does after needs.
    let header, callback, before, locals =
        match entry with
        | HttpHandler ->
            $"(func $__nanyx_http_handle (export \"{ComponentAbi.handleExport}\") (param $request i32) (result i32) {ComponentAbi.handleLocals}",
            ComponentAbi.handleCallback,
            ComponentAbi.handleBegin,
            " " + ComponentAbi.handleLocals
        | CliRun
        | Library -> $"(func $__nanyx_cli_run (export \"{ComponentAbi.runExport}\") (result i32)", ComponentAbi.runCallback, [], ""
    // What the entry does once the start function has finished, leaving EXIT to return.
    let finish =
        [ yield! teardown
          match entry with
          | HttpHandler -> yield! ComponentAbi.handleFinish
          | CliRun
          | Library ->
              "i32.const 0"
              $"call {ComponentAbi.runTaskReturn}"
              // EXIT: the task is done.
              "i32.const 0" ]
        |> List.map (fun line -> "  " + line)
    lines
        [ $"(global {Names.main} (mut i32) (i32.const 0))"
          $"(global {Names.waiter} (mut i32) (i32.const 0))"
          $"(func {Names.stopped}"
          "  i32.const 0"
          $"  global.set {scheduler TaskLowering.Names.suspending}"
          $"  call {scheduler WasmScheduler.Names.current}"
          $"  global.get {scheduler TaskLowering.Names.suspended}"
          $"  i32.store offset={TaskLowering.taskFrameOffset}"
          $"  global.get {scheduler WasmScheduler.Names.stalled}"
          $"  call {scheduler WasmScheduler.Names.countStall}"
          $"  call {scheduler WasmScheduler.Names.current}"
          $"  global.set {Names.main}"
          $"  global.get {Names.main}"
          $"  call {scheduler WasmScheduler.Names.enqueue}"
          ")"
          $"(func {Names.drive} (result i32) (local $t i32) (local $f i32){locals}"
          "  loop $each"
          // The host's wait, if the idle tasks make it, is the one the callback makes again.
          $"  global.get {scheduler WasmScheduler.Names.hostWaiter}"
          "  if"
          $"    global.get {scheduler WasmScheduler.Names.hostWaiter}"
          $"    global.set {Names.waiter}"
          "  end"
          "  i32.const 1"
          $"  global.set {ComponentAbi.Callback.atTop}"
          $"  call {scheduler WasmScheduler.Names.idle}"
          "  i32.const 0"
          $"  global.set {ComponentAbi.Callback.atTop}"
          $"  global.get {ComponentAbi.Callback.waitSet}"
          "  if"
          $"    global.get {ComponentAbi.Callback.waitSet}"
          "    i32.const 4"
          "    i32.shl"
          $"    i32.const {wait}"
          "    i32.or"
          "    i32.const 0"
          $"    global.set {ComponentAbi.Callback.waitSet}"
          "    return"
          "  end"
          $"  call {scheduler WasmScheduler.Names.dequeue}"
          "  local.tee $t"
          $"  global.get {Names.main}"
          "  i32.eq"
          "  if"
          "    local.get $t"
          $"    call {scheduler WasmScheduler.Names.switchTo}"
          "    i32.const 0"
          $"    global.set {scheduler WasmScheduler.Names.stalled}"
          "    local.get $t"
          $"    i32.load offset={TaskLowering.taskFrameOffset}"
          "    local.tee $f"
          "    local.get $f"
          "    i32.load"
          $"    call_indirect (type ${startResume})"
          "    drop"
          $"    call {scheduler TaskLowering.Names.settleMain}"
          $"    global.get {scheduler TaskLowering.Names.suspending}"
          "    i32.eqz"
          "    if"
          yield! finish |> List.map (fun line -> "    " + line)
          "      return"
          "    end"
          $"    call {Names.stopped}"
          "  else"
          "    local.get $t"
          $"    call {scheduler WasmScheduler.Names.step}"
          "  end"
          "  br $each"
          "  end"
          "  unreachable"
          ")"
          header
          yield! before |> List.map (fun line -> "  " + line)
          $"  call ${start}"
          "  drop"
          $"  global.get {scheduler TaskLowering.Names.suspending}"
          "  if"
          $"    call {Names.stopped}"
          $"    call {Names.drive}"
          "    return"
          "  end"
          yield! finish
          ")"
          $"(func $__nanyx_component_callback (export \"{callback}\") (param $event i32) (param $first i32) (param $second i32) (result i32) (local $w i32)"
          "  local.get $event"
          $"  i32.const {taskCancelled}"
          "  i32.eq"
          "  if"
          $"    global.get {Names.main}"
          $"    call {scheduler WasmScheduler.Names.cancelBlock}"
          "  else"
          "    local.get $event"
          "    if"
          "      local.get $event"
          $"      global.set {ComponentAbi.Callback.pendingCode}"
          "      local.get $first"
          $"      global.set {ComponentAbi.Callback.pendingFirst}"
          "      local.get $second"
          $"      global.set {ComponentAbi.Callback.pendingSecond}"
          $"      global.get {Names.waiter}"
          "      local.tee $w"
          "      i32.const -1"
          "      local.get $w"
          "      i32.load"
          $"      call_indirect (type ${waiterType})"
          "      drop"
          $"      global.get {scheduler WasmScheduler.Names.hostRound}"
          "      i32.const 1"
          "      i32.add"
          $"      global.set {scheduler WasmScheduler.Names.hostRound}"
          "    end"
          "  end"
          $"  call {Names.drive}"
          ")" ]
