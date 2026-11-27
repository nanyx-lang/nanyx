/// A component's entries lifted with the component model's callback ABI: `wasi:cli/run`'s `run` or
/// `wasi:http/handler`'s `handle`, for a program whose start function can stop (#2240), and each of the program's own
/// exports whose type requires `Async` (#2915); see "Running with the callback ABI" on the Components page of the Deep
/// dives. The function such an entry calls, the start function or the def it exports, is a state machine (see
/// `TaskLowering`), and its task, the main task, is one the scheduler runs as it runs the others (`WasmScheduler`):
///
/// - The entry raises backpressure, so that the host starts no other task in the instance until it is done, and calls
///   its function. If that finishes, the entry finishes (`Entry.Finish`): `run` tears the program's cached values down
///   and gives `ok` with `task.return`; `handle` the response, whose body it then writes (`ComponentAbi.handleFinish`);
///   an export its result. Then it lowers backpressure and returns EXIT. If the function stops, its frame goes to the
///   main task, which goes on the queue, and the scheduler runs (`Names.drive`), with `Names.entry` saying which entry
///   is in flight.
/// - The scheduler runs the tasks in turn, as `WasmScheduler.Names.runOthers` does, resuming the main task's frame when
///   its turn comes, until the function finishes. When every task waits on the host, it makes the host's wait, as
///   `runOthers` does, but with `ComponentAbi.Callback.atTop` set, so the component root's wait on its shared waitable
///   set doesn't block: `ComponentAbi`'s `wait` keeps the set instead, and the scheduler returns WAIT, with the set, to
///   the host.
/// - The host calls the entry's callback with the event when one comes. The callback keeps it, and makes the host's
///   wait the scheduler last made again, whose `wait` gives it the event, so the root keeps it for the task that waits
///   on it. Then the scheduler runs again. TASK_CANCELLED cancels the main task, and so every task, whose waits on the
///   host cancel their subtasks.
///
/// Backpressure keeps one entry in flight at a time, so one main task and one `Names.entry` serve them all. The
/// scheduler sets `atTop` only around its own wait, so a wait on the host under anything else that runs the tasks,
/// `blockOn` in a cached value's getter, would block in `[waitable-set-wait]`, which an `async func` may. None does: a
/// cached value can't require a context, so its tasks can't wait on the host.
module internal Transpiler.Wasm.ComponentCallback

open Transpiler.Wasm.WasmAllocator
open Transpiler.Wasm.WatText

/// The names of the functions and globals.
module Names =
    /// Runs the tasks until the entry's function finishes, or every task waits on the host.
    let drive = "$__nanyx_component_drive"
    /// Gives the frame the main task stopped in to it, and puts it on the queue.
    let stopped = "$__nanyx_component_stopped"
    /// The callback every entry's callback calls.
    let callback = "$__nanyx_component_callback"
    /// The main task, whose frame is the entry's function's.
    let main = "$__nanyx_component_main"
    /// The entry in flight, by its index.
    let entry = "$__nanyx_component_entry"
    /// The host's wait the scheduler made last, which the callback makes again with the event.
    let waiter = "$__nanyx_component_waiter"

/// An entry lifted with the callback ABI.
type Entry =
    { /// The globals it keeps what it needs across a wait in.
      Globals: string list
      /// The function's header, with its name, export, parameters and `(result i32)`, but not its locals.
      Header: string
      /// The locals its code uses, `Before` and `Call`'s.
      Locals: string list
      /// The name its callback is exported under.
      Callback: string
      /// What it does before it calls its function, after raising backpressure.
      Before: string list
      /// The call of its function, which leaves its value.
      Call: string list
      /// The WebAssembly type of that value.
      ValueType: string
      /// Whether the function is a state machine, and so can stop.
      CanStop: bool
      /// The indirect type of the function's resume entry, when it can stop.
      Resume: string
      /// What it does once its function has finished, with its value in `$value`, before lowering backpressure, and
      /// the locals that needs.
      Finish: string list
      FinishLocals: string list }

/// The canonical ABI's codes: what the callback returns to wait on a set (with the set above the low 4 bits), and the
/// event that cancels the task.
let private wait = 2
let private taskCancelled = 6


/// The functions of each entry of `entries`, and, when one can stop, the scheduler they run. A host's wait is a function
/// value of the indirect type `waiterType`.
let functions (entries: Entry list) (waiterType: string) =
    let scheduler (name: string) = "$" + name
    let canStop = entries |> List.exists _.CanStop
    let finishName (index: int) = $"$__nanyx_entry_{index}_finish"
    let resumeName (index: int) = $"$__nanyx_entry_{index}_resume"
    let perEntry (index: int) (entry: Entry) =
        let finishLocals = String.concat " " entry.FinishLocals
        let locals = String.concat " " entry.Locals
        [ yield! entry.Globals
          // Finishes the entry, with its function's value, and lowers backpressure.
          $"(func {finishName index} (param $value {entry.ValueType}) {finishLocals}"
          yield! indent entry.Finish
          $"  call {ComponentAbi.backpressureDec}"
          ")"
          $"{entry.Header} (local $value {entry.ValueType}) {locals}"
          $"  call {ComponentAbi.backpressureInc}"
          yield! indent entry.Before
          yield! indent entry.Call
          "  local.set $value"
          if entry.CanStop then
              $"  global.get {scheduler TaskLowering.Names.suspending}"
              "  if"
              $"    i32.const {index}"
              $"    global.set {Names.entry}"
              $"    call {Names.stopped}"
              $"    call {Names.drive}"
              "    return"
              "  end"
          "  local.get $value"
          $"  call {finishName index}"
          // EXIT: the task is done.
          "  i32.const 0"
          ")"
          $"(func (export \"{entry.Callback}\") (param $event i32) (param $first i32) (param $second i32) (result i32)"
          if canStop then
              "  local.get $event"
              "  local.get $first"
              "  local.get $second"
              $"  call {Names.callback}"
          else
              // It never returns WAIT, so the host never calls it.
              "  unreachable"
          ")"
          if entry.CanStop then
              // Resumes the main task's frame, and gives 1 when the function has finished, which finishes the entry.
              $"(func {resumeName index} (result i32) (local $f i32) (local $value {entry.ValueType})"
              $"  call {scheduler WasmScheduler.Names.current}"
              $"  i32.load offset={TaskLowering.taskFrameOffset}"
              "  local.tee $f"
              "  local.get $f"
              "  i32.load"
              $"  call_indirect (type ${entry.Resume})"
              "  local.set $value"
              $"  call {scheduler TaskLowering.Names.settleMain}"
              $"  global.get {scheduler TaskLowering.Names.suspending}"
              "  if"
              "    i32.const 0"
              "    return"
              "  end"
              "  local.get $value"
              $"  call {finishName index}"
              "  i32.const 1"
              ")" ]
    let scheduling =
        [ $"(global {Names.main} (mut i32) (i32.const 0))"
          $"(global {Names.entry} (mut i32) (i32.const 0))"
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
          $"(func {Names.drive} (result i32) (local $t i32) (local $done i32)"
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
          "    i32.const 0"
          "    local.set $done"
          for index, entry in List.indexed entries do
              if entry.CanStop then
                  $"    global.get {Names.entry}"
                  $"    i32.const {index}"
                  "    i32.eq"
                  "    if"
                  $"      call {resumeName index}"
                  "      local.set $done"
                  "    end"
          "    local.get $done"
          "    if"
          // EXIT: the task is done.
          "      i32.const 0"
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
          $"(func {Names.callback} (param $event i32) (param $first i32) (param $second i32) (result i32) (local $w i32)"
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
    lines [ if canStop then yield! scheduling; for index, entry in List.indexed entries do yield! perEntry index entry ]

/// The entry `run` or `handle`, `entry`, whose function is the start function `start`, a state machine whose resume
/// entry has the indirect type `startResume`, and whose cached values `teardown` tears down.
let startEntry (entry: ComponentEntry) (start: string) (startResume: string) (teardown: string list) : Entry =
    let header, callback, before, finishLocals, finish =
        match entry with
        | HttpHandler ->
            $"(func $__nanyx_http_handle (export \"{ComponentAbi.handleExport}\") (param $request i32) (result i32)",
            ComponentAbi.handleCallback,
            ComponentAbi.handleBegin,
            [ ComponentAbi.handleLocals ],
            // `handleFinish` leaves EXIT, which the entry returns itself.
            ComponentAbi.handleFinish @ [ "drop" ]
        | CliRun
        | Library ->
            $"(func $__nanyx_cli_run (export \"{ComponentAbi.runExport}\") (result i32)",
            ComponentAbi.runCallback,
            [],
            [],
            [ "i32.const 0"; $"call {ComponentAbi.runTaskReturn}" ]
    { Globals = []
      Header = header
      Locals = []
      Callback = callback
      Before = before
      Call = [ $"call ${start}" ]
      ValueType = "i32"
      CanStop = true
      Resume = startResume
      Finish = teardown @ finish
      FinishLocals = finishLocals }
