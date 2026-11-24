/// How a function that can wait is compiled so that it can stop and carry on later (see "How tasks are compiled" in
/// `docs/src/content/deep-dives/async.md`). This is the one place that decides it: the emitter asks it for the code of
/// a function that is a state machine and of each of its split points (see `CoreIrSplitPoints`), and code generation
/// for the runtime's part. Compiling tasks on WebAssembly's stack switching (#2164) would be another lowering beside
/// this one, behind the same functions.
///
/// The lowering is a state machine, as C# and Rust compile async functions, kept to structured code:
/// - A call that stops sets `Names.suspending`, leaves its frame in `Names.suspended` and returns. The caller sees the
///   flag after the call, copies the variables it keeps there into a frame of its own on the heap, records its state
///   and the callee's frame in it, and returns the same way. Nothing is allocated unless a call does stop.
/// - A frame holds, in its header, the table slot of the function's resume entry, its state and the frame of the call
///   it stopped in. The resume entry passes the frame in `Names.resuming` and calls the function, whose first code
///   takes it back: it restores the variables and goes through its own code to the call it stopped in, skipping what
///   ran already (see `CoreIrEmit`), and resumes that call by calling the callee frame's resume entry. So resuming a
///   task resumes each function it stopped in, outermost first, as Rust polls a future, and a crash's stack still
///   shows them.
/// - Code that runs a state machine without being one, such as the start function, runs it to the end: while it
///   stops, the scheduler runs the other tasks (`blockOn`, `WasmScheduler`).
module internal Transpiler.Wasm.TaskLowering

open Transpiler.Wasm.Wat
open Transpiler.Wasm.WasmRuntime

/// The names of the runtime's part.
module Names =
    /// The global that says the call that just returned stopped: 1 until whoever resumes it clears it.
    let suspending = "__nanyx_suspending"
    /// The global that holds the frame of the call that stopped.
    let suspended = "__nanyx_suspended"
    /// The global a resume entry passes its frame in, and 0 otherwise.
    let resuming = "__nanyx_resuming"
    /// The helper that stops once, and carries on when resumed (see `WasmOptions.YieldAtEveryWait`), and its resume
    /// entry.
    let yieldOnce = "__nanyx_yield"
    let yieldResume = "__nanyx_yield_resume"
    /// The resume entry of the state machine `name`.
    let resumeEntry (name: string) = $"{name}__resume"
    /// The scheduler's helpers that `blockOn` calls (see `WasmScheduler`): the task running, made the main task if there
    /// is none, the one that runs the other tasks until the running one's turn comes again, and the one that frees the
    /// main task when it holds no scope, as when it was made only to wait in.
    let currentTask = "__nanyx_task_current"
    let runOthers = "__nanyx_task_run_others"
    let settleMain = "__nanyx_task_settle"

/// Where a task keeps the frame it stopped in (see `WasmScheduler`).
let taskFrameOffset = 8

/// A frame's header: the resume entry's table slot, the state, the frame of the call it stopped in, and padding, so
/// that what follows is 8-byte aligned.
let private headerSize = 16

/// Where a frame keeps the variable at `index` among those its state keeps: each in 8 bytes, whatever its type.
let private slotOffset (index: int) = headerSize + 8 * index

/// The code of each instruction, one to a line, in a block of code the emitter writes.
let private lines (code: string list) = code |> List.filter ((<>) "") |> String.concat "\n    "

/// The globals the runtime's part keeps.
let globals : string list =
    [ $"  (global ${Names.suspending} (mut i32) (i32.const 0))"
      $"  (global ${Names.suspended} (mut i32) (i32.const 0))"
      $"  (global ${Names.resuming} (mut i32) (i32.const 0))" ]

/// The helper that stops once, given the table slot of its resume entry, and that entry. A frame of its own is all it
/// keeps, which its resume entry frees.
let yieldHelpers (resumeSlot: int) : string list =
    [ $"  (func ${Names.yieldOnce} (result i32) (local $frame i32)"
      $"    i32.const {headerSize}"
      $"    call ${Helpers.alloc}"
      "    local.tee $frame"
      $"    i32.const {resumeSlot}"
      "    i32.store"
      "    local.get $frame"
      $"    global.set ${Names.suspended}"
      "    i32.const 1"
      $"    global.set ${Names.suspending}"
      "    i32.const 0\n  )"
      $"  (func ${Names.yieldResume} (param $frame i32) (result i32)"
      "    local.get $frame"
      $"    call ${Helpers.free}"
      "    i32.const 0\n  )" ]

/// Spike (#2427, not for merging): a generator's helpers. `genYield` keeps its value and stops with a frame of its own,
/// which its resume entry frees. `genStart` calls a function of nothing (a closure) and `genResume` carries on a frame;
/// each clears the flag and gives the frame the function stopped in when it stopped, and 0 when it returned.
let genHelpers (yieldResumeSlot: int) (oneToOne: string) : string list =
    let afterRun =
        [ "drop"
          $"global.get ${Names.suspending}"
          "if"
          "i32.const 0"
          $"global.set ${Names.suspending}"
          $"global.get ${Names.suspended}"
          "return"
          "end"
          "i32.const 0" ]
    [ "  (global $__nanyx_gen_value_cell (mut i64) (i64.const 0))"
      lines
          [ $"  (func ${NyxCompiler.Intrinsics.genYield} (param $x i64) (local $frame i32)"
            "local.get $x"
            "global.set $__nanyx_gen_value_cell"
            $"i32.const {headerSize}"
            $"call ${Helpers.alloc}"
            "local.tee $frame"
            $"i32.const {yieldResumeSlot}"
            "i32.store"
            "local.get $frame"
            $"global.set ${Names.suspended}"
            "i32.const 1"
            $"global.set ${Names.suspending}\n  )" ]
      lines
          [ "  (func $__nanyx_gen_yield_resume (param $frame i32) (result i32)"
            "local.get $frame"
            $"call ${Helpers.free}"
            "i32.const 0\n  )" ]
      lines [ $"  (func ${NyxCompiler.Intrinsics.genPrepare} (param $f i32) (result i32)"; "local.get $f\n  )" ]
      lines [ $"  (func ${NyxCompiler.Intrinsics.genValue} (result i64)"; "global.get $__nanyx_gen_value_cell\n  )" ]
      lines
          [ $"  (func ${NyxCompiler.Intrinsics.genStart} (param $f i32) (result i32)"
            "local.get $f"
            "local.get $f"
            "i32.load"
            $"call_indirect (type ${oneToOne})"
            yield! afterRun
            "\n  )" ]
      lines
          [ $"  (func ${NyxCompiler.Intrinsics.genResume} (param $k i32) (result i32)"
            "local.get $k"
            "local.get $k"
            "i32.load"
            $"call_indirect (type ${oneToOne})"
            yield! afterRun
            "\n  )" ] ]

/// The resume entry of the state machine `name`, which takes `parameters` (their WebAssembly types) and gives a value of
/// type `result`: it passes the frame on, and calls the function with nothing in its parameters, which the frame
/// gives back.
let resumeEntry (name: string) (parameters: string list) (result: string) : string =
    lines
        [ $"  (func ${Names.resumeEntry name} (param $frame i32) (result {result})"
          "local.get $frame"
          $"global.set ${Names.resuming}"
          yield! parameters |> List.map (fun valueType -> $"{valueType}.const 0")
          $"return_call ${name}\n  )" ]

/// The code a state machine starts with: when a resume entry passed it a frame, it takes the state and the frame of the
/// call it stopped in into `resume` and `child`, restores the variables `saved` says each state keeps (each by its
/// local and WebAssembly type), and frees the frame, held in `frame` meanwhile.
let prologue (resume: string) (child: string) (frame: string) (saved: Map<int, (string * string) list>) : string =
    let restore (state: int, kept: (string * string) list) =
        [ $"local.get ${resume}"
          $"i32.const {state}"
          "i32.eq"
          "if"
          yield!
              kept
              |> List.mapi (fun index (local, valueType) ->
                  lines [ $"local.get ${frame}"; $"{valueType}.load offset={slotOffset index}"; $"local.set ${local}" ])
          "end" ]
    lines
        [ $"global.get ${Names.resuming}"
          "if"
          $"global.get ${Names.resuming}"
          $"local.set ${frame}"
          "i32.const 0"
          $"global.set ${Names.resuming}"
          $"local.get ${frame}"
          "i32.load offset=4"
          $"local.set ${resume}"
          $"local.get ${frame}"
          "i32.load offset=8"
          $"local.set ${child}"
          yield! saved |> Map.toList |> List.filter (snd >> List.isEmpty >> not) |> List.collect restore
          $"local.get ${frame}"
          $"call ${Helpers.free}"
          "end" ]

/// The code after the call at `state` that stops the function when the call stopped: it keeps `kept` (each variable's
/// local and WebAssembly type) in a new frame, held in `frame`, with the table slot of the function's resume entry
/// `resumeSlot`, and returns a value of type `result` that no one reads.
let stopAfter (frame: string) (resumeSlot: int) (state: int) (kept: (string * string) list) (result: string) : string =
    lines
        [ $"global.get ${Names.suspending}"
          "if"
          $"i32.const {slotOffset kept.Length}"
          $"call ${Helpers.alloc}"
          $"local.set ${frame}"
          $"local.get ${frame}"
          $"i32.const {resumeSlot}"
          "i32.store"
          $"local.get ${frame}"
          $"i32.const {state}"
          "i32.store offset=4"
          $"local.get ${frame}"
          $"global.get ${Names.suspended}"
          "i32.store offset=8"
          yield!
              kept
              |> List.mapi (fun index (local, valueType) ->
                  lines [ $"local.get ${frame}"; $"local.get ${local}"; $"{valueType}.store offset={slotOffset index}" ])
          $"local.get ${frame}"
          $"global.set ${Names.suspended}"
          $"{result}.const 0"
          "return"
          "end" ]

/// The code of the call at `state`, whose code is `call`, giving a value of type `result`: when the function is
/// resuming at that state (`resume`), it resumes the call's frame (`child`) instead, through its resume entry, of the
/// indirect type `resumeType`, and is no longer resuming.
let resumable (resume: string) (child: string) (state: int) (resumeType: string) (result: string) (call: string) : string =
    lines
        [ $"local.get ${resume}"
          $"i32.const {state}"
          "i32.eq"
          $"(if (result {result})"
          "(then"
          "i32.const 0"
          $"local.set ${resume}"
          $"local.get ${child}"
          $"local.get ${child}"
          "i32.load"
          $"call_indirect (type ${resumeType}))"
          "(else"
          call
          "))" ]

/// The code after a call of a state machine, from code that is not one, that runs it to the end: while it stops, the
/// other tasks run until the running one's turn comes again (see `WasmScheduler`), and then it is resumed, through its
/// frame's resume entry, of the indirect type `resumeType`. Its value, of type `result`, is kept in the local `value`
/// meanwhile, and the frame in `frame`. The running task keeps the frame while the others run: the main task, made
/// for it when there is none, is freed again after each turn if it holds no scope.
let blockOn (value: string) (frame: string) (resumeType: string) : string =
    lines
        [ $"local.set ${value}"
          $"block $__blocked_{value}"
          $"loop $__resumed_{value}"
          $"global.get ${Names.suspending}"
          "i32.eqz"
          $"br_if $__blocked_{value}"
          "i32.const 0"
          $"global.set ${Names.suspending}"
          $"call ${Names.currentTask}"
          $"global.get ${Names.suspended}"
          $"i32.store offset={taskFrameOffset}"
          $"call ${Names.runOthers}"
          $"call ${Names.currentTask}"
          $"i32.load offset={taskFrameOffset}"
          $"local.tee ${frame}"
          $"local.get ${frame}"
          "i32.load"
          $"call_indirect (type ${resumeType})"
          $"local.set ${value}"
          $"call ${Names.settleMain}"
          $"br $__resumed_{value}"
          "end"
          "end"
          $"local.get ${value}" ]

/// The indirect type of a resume entry that gives a value of type `result`.
let resumeTypeOf (result: string) : string list * string = [ "i32" ], result

/// The name of that type, as the module declares it.
let resumeTypeName (result: string) : string = indirectTypeName [ "i32" ] result

/// Whether code whose states are `range` runs: the code that leaves 1 when the state in `resume` is 0, so the function
/// is not resuming, or one of the states in `range`, from the lowest to the highest, so it resumes there.
let runsAt (resume: string) (range: (int * int) option) : string =
    match range with
    | None -> lines [ $"local.get ${resume}"; "i32.eqz" ]
    | Some (lowest, highest) ->
        lines
            [ $"local.get ${resume}"
              "i32.eqz"
              $"local.get ${resume}"
              $"i32.const {lowest}"
              "i32.sub"
              $"i32.const {highest - lowest + 1}"
              "i32.lt_u"
              "i32.or" ]

/// Whether the state in `resume` is one of the states from `lowest` to `highest`.
let resumesIn (resume: string) (lowest: int, highest: int) : string =
    lines [ $"local.get ${resume}"; $"i32.const {lowest}"; "i32.sub"; $"i32.const {highest - lowest + 1}"; "i32.lt_u" ]

/// `code`, which leaves the stack as it found it, run only when `condition` leaves 1.
let onlyWhen (condition: string) (code: string) : string =
    if code = "" then "" else lines [ condition; "if"; code; "end" ]
