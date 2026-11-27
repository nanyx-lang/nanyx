/// What a module compiled as the core module of a WASI 0.3 component has that a module the runtime runs doesn't
/// (`WasmOptions.Component`): its entry exported under its world's name for it (see `ComponentEntry`),
/// `cabi_realloc`, and the runtime's own writes and exits on `wasi:cli` rather than on WASI preview 1.
///
/// The module follows wit-component's conventions, and the componentizer (`Nanyx.Runtime.Componentizer`) wraps it as a
/// component of a world that imports `wasi:cli/stdout` and `stderr`, all at `@0.3.0`, and exports `wasi:cli/run`,
/// importing `wasi:cli/exit` too, such as `wasi:cli/command`, or `wasi:http/handler`, such as `wasi:http/service`. Its
/// canonical ABI is written out here by hand, on `i32`s and `i64`s (see the Components page of the Deep dives).
module internal Transpiler.Wasm.ComponentAbi

open NyxCompiler
open Transpiler.Wasm.WasmAllocator
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.WatText

/// The version of WASI 0.3 the module imports and exports, which every 0.3 host accepts.
let wasiVersion = "0.3.0"

/// The interface whose `run` the module exports as `CliRun`.
let private runInterface = $"wasi:cli/run@{wasiVersion}"

/// The name the module exports `CliRun` under: `wasi:cli/run`'s `run`, which returns `result`, lifted async with a
/// callback (see `ComponentCallback`).
let runExport = $"[async-lift]{runInterface}#run"

/// The name of `run`'s callback, which the host calls with each event while `run` waits.
let runCallback = $"[callback]{runExport}"

/// `run`'s `task.return`, which gives its `result`: 0, `ok`.
let runTaskReturn = "$__nanyx_cli_task_return"

/// The interface whose `handle` the module exports as `HttpHandler`.
let private handlerInterface = $"wasi:http/handler@{wasiVersion}"

/// The name the module exports `HttpHandler` under: `wasi:http/handler`'s `handle`, which takes a `request` and returns
/// `result<response, error-code>`, lifted async with a callback.
let handleExport = $"[async-lift]{handlerInterface}#handle"

/// The allocator the host allocates in the module's memory through: `(old, oldSize, align, newSize) -> address`.
let cabiRealloc = "cabi_realloc"

/// Where the waits below have a waitable set's event written (a handle, then a code at 4), and a stream's future its
/// result (at 8). Below the string literals and `dbgStrNewlineOffset`, after the 12 bytes from 0 that `fd_write`'s
/// callers describe their buffer in.
let private scratchOffset = 16

/// The two streams the runtime writes to, each `wasi:cli`'s interface of that name.
type private Stream =
    | Stdout
    | Stderr

let private interfaceOf (stream: Stream) =
    match stream with
    | Stdout -> $"wasi:cli/stdout@{wasiVersion}"
    | Stderr -> $"wasi:cli/stderr@{wasiVersion}"

let private nameOf (stream: Stream) =
    match stream with
    | Stdout -> "stdout"
    | Stderr -> "stderr"

/// The function a stream's import is called by in the module, `what` being which of them.
let private importFunction (stream: Stream) (what: string) = $"$__nanyx_cli_{nameOf stream}_{what}"

/// The waitable-set built-ins, which come from wit-component's `$root`.
module private Waitables =
    let setNew = "$__nanyx_cli_waitable_set_new"
    let join = "$__nanyx_cli_waitable_join"
    let setWait = "$__nanyx_cli_waitable_set_wait"
    let setDrop = "$__nanyx_cli_waitable_set_drop"

let private exitWithCode = "$__nanyx_cli_exit_with_code"
let private waitFor = "$__nanyx_cli_wait_for"
let private writeTo (stream: Stream) = $"$__nanyx_cli_write_{nameOf stream}"

/// The imports of one stream. `write-via-stream` takes the readable end of a `stream<u8>`, its type 0, and gives a
/// `future<result<_, error-code>>`, its type 1. The write and the future's read are lowered async, so that they run
/// with Wasmtime's default settings, and are waited on at once (see `waitFor`).
let private streamImports (stream: Stream) =
    let name = interfaceOf stream
    let import (field: string) (what: string) (signature: string) =
        $"""  (import "{name}" "{field}" (func {importFunction stream what}{signature}))"""
    [ import "[stream-new-0]write-via-stream" "stream_new" " (result i64)"
      import "write-via-stream" "write_via_stream" " (param i32) (result i32)"
      import "[async-lower][stream-write-0]write-via-stream" "stream_write" " (param i32 i32 i32) (result i32)"
      import "[stream-drop-writable-0]write-via-stream" "stream_drop_writable" " (param i32)"
      import "[async-lower][future-read-1]write-via-stream" "future_read" " (param i32 i32) (result i32)"
      import "[future-drop-readable-1]write-via-stream" "future_drop_readable" " (param i32)" ]

/// The backpressure built-ins: while it is raised, the host starts no other task in the instance, so that an entry lifted
/// with the callback ABI runs alone (see `ComponentCallback`).
let backpressureInc = "$__nanyx_backpressure_inc"
let backpressureDec = "$__nanyx_backpressure_dec"

/// The functions the handler's imports are called by in the module.
module private Handler =
    let taskReturn = "$__nanyx_http_task_return"
    let bodyWrite = "$__nanyx_http_body_write"
    let bodyDropWritable = "$__nanyx_http_body_drop_writable"
    let trailersWrite = "$__nanyx_http_trailers_write"
    let trailersDropWritable = "$__nanyx_http_trailers_drop_writable"

/// The imports `HttpHandler` needs: `handle`'s `task.return`, and the writes to the response's body and trailers, a `stream<u8>` and a
/// `future<result<option<trailers>, error-code>>`, the types 0 and 1 of `response.new`. `task.return` takes
/// `result<response, error-code>` flattened: its case, then the response's handle or the error's case, then the rest of
/// the error, which the module never gives.
let private handlerImports =
    let types = $"wasi:http/types@{wasiVersion}"
    [ $"""  (import "[export]{handlerInterface}" "[task-return]handle" (func {Handler.taskReturn} (param i32 i32 i32 i64 i32 i32 i32 i32)))"""
      $"""  (import "{types}" "[async-lower][stream-write-0][static]response.new" (func {Handler.bodyWrite} (param i32 i32 i32) (result i32)))"""
      $"""  (import "{types}" "[stream-drop-writable-0][static]response.new" (func {Handler.bodyDropWritable} (param i32)))"""
      $"""  (import "{types}" "[async-lower][future-write-1][static]response.new" (func {Handler.trailersWrite} (param i32 i32) (result i32)))"""
      $"""  (import "{types}" "[future-drop-writable-1][static]response.new" (func {Handler.trailersDropWritable} (param i32)))""" ]

/// Every import of a component's core module that the runtime's own code and its entry need.
let imports (entry: ComponentEntry) =
    [ yield! streamImports Stdout
      yield! streamImports Stderr
      match entry with
      | CliRun ->
          $"""  (import "wasi:cli/exit@{wasiVersion}" "exit-with-code" (func {exitWithCode} (param i32)))"""
          $"""  (import "[export]{runInterface}" "[task-return]run" (func {runTaskReturn} (param i32)))"""
      | Library -> $"""  (import "wasi:cli/exit@{wasiVersion}" "exit-with-code" (func {exitWithCode} (param i32)))"""
      | HttpHandler -> yield! handlerImports
      $"""  (import "$root" "[backpressure-inc]" (func {backpressureInc}))"""
      $"""  (import "$root" "[backpressure-dec]" (func {backpressureDec}))"""
      $"""  (import "$root" "[waitable-set-new]" (func {Waitables.setNew} (result i32)))"""
      $"""  (import "$root" "[waitable-join]" (func {Waitables.join} (param i32 i32)))"""
      $"""  (import "$root" "[waitable-set-wait]" (func {Waitables.setWait} (param i32 i32) (result i32)))"""
      $"""  (import "$root" "[waitable-set-drop]" (func {Waitables.setDrop} (param i32)))""" ]
    |> String.concat "\n"

/// What an async built-in returns when it hasn't finished: the canonical ABI's BLOCKED.
let private blocked = -1

/// What the scheduler and the callback share with `wait` (see `waitExtern`) while `run` is lifted with the callback ABI:
/// the event the callback was given, for `wait` to give the host's code (its code, 0 for none, and its two values); the
/// set that `wait` was asked to wait on when the scheduler can return to the host instead (0 for none); and whether it
/// can, which it can only when no other code is running.
module Callback =
    let pendingCode = "$__nanyx_cb_pending_code"
    let pendingFirst = "$__nanyx_cb_pending_first"
    let pendingSecond = "$__nanyx_cb_pending_second"
    let waitSet = "$__nanyx_cb_wait_set"
    let atTop = "$__nanyx_cb_at_top"
    let all = [ pendingCode; pendingFirst; pendingSecond; waitSet; atTop ]

/// Waits for the waitable `$handle` in a set of its own, and gives the code its event came with: for a stream write,
/// how many bytes it took (above the low 4 bits) and whether it went on (the low 4 bits, 0).
let private waitForFunction =
    lines
        [ $"(func {waitFor} (param $handle i32) (result i32)"
          "  (local $set i32)"
          $"  call {Waitables.setNew}"
          "  local.set $set"
          "  local.get $handle"
          "  local.get $set"
          $"  call {Waitables.join}"
          "  local.get $set"
          $"  i32.const {scratchOffset}"
          $"  call {Waitables.setWait}"
          "  drop"
          // Joining the set 0 takes the handle out of its set again, so that the set can be dropped.
          "  local.get $handle"
          "  i32.const 0"
          $"  call {Waitables.join}"
          "  local.get $set"
          $"  call {Waitables.setDrop}"
          $"  i32.const {scratchOffset + 4}"
          "  i32.load"
          ")" ]

/// Writes the `$length` bytes at `$bytes` to the stream's writable end `$writer` with `streamWrite`, a call of a
/// `stream-write` lowered async, waiting while it is blocked, until the host has taken them all or dropped its end.
/// The function it is in has the locals `$writer`, `$bytes`, `$length` and `$status`.
let private writeAllLines (streamWrite: string) =
    [ "  block $done"
      "    loop $more"
      "      local.get $length"
      "      i32.eqz"
      "      br_if $done"
      "      local.get $writer"
      "      local.get $bytes"
      "      local.get $length"
      "      " + streamWrite
      "      local.tee $status"
      $"      i32.const {blocked}"
      "      i32.eq"
      "      if"
      "        local.get $writer"
      $"        call {waitFor}"
      "        local.set $status"
      "      end"
      // The host dropped its end, or the write was cancelled: nothing more can be written.
      "      local.get $status"
      "      i32.const 15"
      "      i32.and"
      "      br_if $done"
      "      local.get $bytes"
      "      local.get $status"
      "      i32.const 4"
      "      i32.shr_u"
      "      local.tee $status"
      "      i32.add"
      "      local.set $bytes"
      "      local.get $length"
      "      local.get $status"
      "      i32.sub"
      "      local.set $length"
      "      br $more"
      "    end"
      "  end" ]

/// Writes the `$length` bytes at `$bytes` to `stream`: through a stream of its own, which it closes once the host has
/// taken every byte, and then waits until the host has written them, so that writes to stdout and stderr come out in
/// the order they were made.
let private writeFunction (stream: Stream) =
    let call what = $"call {importFunction stream what}"
    lines
        [ $"(func {writeTo stream} (param $bytes i32) (param $length i32)"
          "  (local $ends i64)"
          "  (local $writer i32)"
          "  (local $written i32)"
          "  (local $status i32)"
          "  " + call "stream_new"
          "  local.tee $ends"
          // The readable end is the low half, and goes to the host; the writable end is the high half.
          "  i32.wrap_i64"
          "  " + call "write_via_stream"
          "  local.set $written"
          "  local.get $ends"
          "  i64.const 32"
          "  i64.shr_u"
          "  i32.wrap_i64"
          "  local.set $writer"
          yield! writeAllLines (call "stream_write")
          "  local.get $writer"
          "  " + call "stream_drop_writable"
          "  local.get $written"
          $"  i32.const {scratchOffset + 8}"
          "  " + call "future_read"
          $"  i32.const {blocked}"
          "  i32.eq"
          "  if"
          "    local.get $written"
          $"    call {waitFor}"
          "    drop"
          "  end"
          "  local.get $written"
          "  " + call "future_drop_readable"
          ")" ]

/// WASI preview 1's `fd_write`, on `wasi:cli`'s stdout (fd 1) and stderr (fd 2), so that the runtime's helpers, and a
/// foreign function of `fd_write`, write as they do in a module the runtime runs. Any other descriptor is EBADF (8).
let private fdWriteFunction =
    lines
        [ "(func $fd_write (param $fd i32) (param $buffers i32) (param $count i32) (param $written i32) (result i32)"
          "  (local $total i32)"
          "  (local $length i32)"
          "  local.get $fd"
          "  i32.const 1"
          "  i32.ne"
          "  local.get $fd"
          "  i32.const 2"
          "  i32.ne"
          "  i32.and"
          "  if"
          "    i32.const 8"
          "    return"
          "  end"
          "  block $done"
          "    loop $each"
          "      local.get $count"
          "      i32.eqz"
          "      br_if $done"
          "      local.get $buffers"
          "      i32.load offset=4"
          "      local.set $length"
          "      local.get $fd"
          "      i32.const 1"
          "      i32.eq"
          "      if"
          "        local.get $buffers"
          "        i32.load"
          "        local.get $length"
          $"        call {writeTo Stdout}"
          "      else"
          "        local.get $buffers"
          "        i32.load"
          "        local.get $length"
          $"        call {writeTo Stderr}"
          "      end"
          "      local.get $total"
          "      local.get $length"
          "      i32.add"
          "      local.set $total"
          "      local.get $buffers"
          "      i32.const 8"
          "      i32.add"
          "      local.set $buffers"
          "      local.get $count"
          "      i32.const 1"
          "      i32.sub"
          "      local.set $count"
          "      br $each"
          "    end"
          "  end"
          "  local.get $written"
          "  local.get $total"
          "  i32.store"
          "  i32.const 0"
          ")" ]

/// WASI preview 1's `proc_exit`, which never returns either: on `wasi:cli/exit`'s `exit-with-code` for `CliRun` and
/// `Library`, and a
/// trap for `HttpHandler`, whose world can't exit, so that the host answers the request with an error.
let private procExitFunction (entry: ComponentEntry) =
    lines
        [ $"(func ${Helpers.procExit} (param $code i32)"
          match entry with
          | CliRun
          | Library ->
              "  local.get $code"
              $"  call {exitWithCode}"
          | HttpHandler -> "  unreachable"
          ")" ]

/// `cabi_realloc` on the program's allocator: a new block of `$newSize` bytes, with the first bytes of the block at
/// `$old`, if there is one, copied into it and that block freed. The canonical ABI asks for blocks aligned to `$align`,
/// at most 8 for any WIT type: `alloc`'s blocks are 4-byte aligned, so a larger `$align` takes an 8-byte aligned block
/// from `allocAligned8`.
let private cabiReallocFunction =
    lines
        [ $"(func $__nanyx_cli_cabi_realloc (export \"{cabiRealloc}\") (param $old i32) (param $oldSize i32) (param $align i32) (param $newSize i32) (result i32)"
          "  (local $new i32)"
          "  local.get $align"
          "  i32.const 4"
          "  i32.gt_u"
          "  if (result i32)"
          "    local.get $newSize"
          $"    call ${Helpers.allocAligned8}"
          "  else"
          "    local.get $newSize"
          $"    call ${Helpers.alloc}"
          "  end"
          "  local.set $new"
          "  local.get $old"
          "  if"
          "    local.get $new"
          "    local.get $old"
          "    local.get $oldSize"
          "    local.get $newSize"
          "    local.get $oldSize"
          "    local.get $newSize"
          "    i32.lt_u"
          "    select"
          $"    call ${Helpers.copyBytes}"
          "    local.get $old"
          $"    call ${Helpers.free}"
          "  end"
          "  local.get $new"
          ")" ]

/// The helpers `cabi_realloc` and the handler call.
let reallocHelpers = [ Helpers.alloc; Helpers.allocAligned8; Helpers.free; Helpers.copyBytes ]

/// The module the compiler defines the functions of for `HttpHandler`, which a host's component root declares as foreign
/// functions (see `Hosts.componentModule`).
let private componentExtern (name: string) (parameters: ExternValue list) (result: ExternValue option) =
    { Module = Hosts.componentModule; Name = name; Parameters = parameters; Result = result }

/// `argument`, which gives the handle of the `request` that `handle` was given.
let private argumentExtern = componentExtern "argument" [] (Some ExternValue.I32)

/// `result`, which takes the `response` that `handle` returns, the writable ends of its body's stream and of its
/// trailers' future, and the address and length of its body, which it copies, to write once `handle` has returned.
let private resultExtern = componentExtern "result" (List.replicate 5 ExternValue.I32) None

/// `wait`, which a component root's wait on its shared waitable set calls in place of `[waitable-set-wait]`, with the
/// set and where to write the event, and which gives the event's code: 0 (none) when the scheduler is to return to the
/// host instead, waiting on the set, and give the event to the callback (see `waitFunction`).
let private waitExtern = componentExtern "wait" [ ExternValue.I32; ExternValue.I32 ] (Some ExternValue.I32)

/// The foreign functions of `Hosts.componentModule` that the module defines for `entry`.
let externs (entry: ComponentEntry) =
    match entry with
    | CliRun
    | Library -> [ waitExtern ]
    | HttpHandler -> [ waitExtern; argumentExtern; resultExtern ]

/// `wait` (see `waitExtern`), named by `functionName`: the event the callback was given, if it has one that the root
/// hasn't taken yet; or none, when the scheduler is the one waiting, with nothing else running, and returns to the host
/// with the set instead of blocking; or else the event `[waitable-set-wait]` waits for. Only `run`'s scheduler, under
/// the callback ABI, sets `Callback.atTop`, so for any other entry this is `[waitable-set-wait]`.
let private waitFunction (functionName: Extern -> string) =
    lines
        [ for name in Callback.all do
              $"(global {name} (mut i32) (i32.const 0))"
          $"(func ${functionName waitExtern} (param $set i32) (param $event i32) (result i32)"
          "  (local $code i32)"
          $"  global.get {Callback.pendingCode}"
          "  local.tee $code"
          "  if"
          "    local.get $event"
          $"    global.get {Callback.pendingFirst}"
          "    i32.store"
          "    local.get $event"
          $"    global.get {Callback.pendingSecond}"
          "    i32.store offset=4"
          "    i32.const 0"
          $"    global.set {Callback.pendingCode}"
          "    local.get $code"
          "    return"
          "  end"
          $"  global.get {Callback.atTop}"
          "  if"
          "    local.get $set"
          $"    global.set {Callback.waitSet}"
          "    i32.const 0"
          "    return"
          "  end"
          "  local.get $set"
          "  local.get $event"
          $"  call {Waitables.setWait}"
          ")" ]

/// Where the handler keeps the request and what `result` was given, from `handle`'s call to its end.
module private HandlerState =
    let request = "$__nanyx_http_request"
    let response = "$__nanyx_http_response"
    let body = "$__nanyx_http_body"
    let trailers = "$__nanyx_http_trailers"
    let bytes = "$__nanyx_http_bytes"
    let length = "$__nanyx_http_length"
    let all = [ request; response; body; trailers; bytes; length ]

/// `argument` and `result` (see `externs`), each the function a call of its foreign function calls, named by
/// `functionName`, and the globals they keep what they are given in. `result`'s response is -1 until it is called.
let private componentFunctions (functionName: Extern -> string) =
    lines
        [ for name in HandlerState.all do
              $"(global {name} (mut i32) (i32.const 0))"
          $"(func ${functionName argumentExtern} (result i32)"
          $"  global.get {HandlerState.request}"
          ")"
          $"(func ${functionName resultExtern} (param $response i32) (param $body i32) (param $trailers i32) (param $bytes i32) (param $length i32)"
          "  (local $copy i32)"
          "  local.get $response"
          $"  global.set {HandlerState.response}"
          "  local.get $body"
          $"  global.set {HandlerState.body}"
          "  local.get $trailers"
          $"  global.set {HandlerState.trailers}"
          "  local.get $length"
          $"  global.set {HandlerState.length}"
          "  local.get $length"
          "  i32.const 1"
          "  i32.add"
          $"  call ${Helpers.alloc}"
          "  local.tee $copy"
          "  local.get $bytes"
          "  local.get $length"
          $"  call ${Helpers.copyBytes}"
          "  local.get $copy"
          $"  global.set {HandlerState.bytes}"
          ")" ]

/// Writes `ok(none)` to the trailers' future `$trailers`, waiting while it is blocked, and drops it: the response has
/// no trailers. A `result<option<trailers>, error-code>` is its case at 0 and, for `ok`, the option's at 8, in a block
/// aligned to 8 (`allocAligned8`).
let private writeNoTrailersFunction =
    lines
        [ "(func $__nanyx_http_write_no_trailers (param $trailers i32)"
          "  (local $value i32)"
          "  i32.const 16"
          $"  call ${Helpers.allocAligned8}"
          "  local.tee $value"
          "  i64.const 0"
          "  i64.store"
          "  local.get $value"
          "  i64.const 0"
          "  i64.store offset=8"
          "  local.get $trailers"
          "  local.get $value"
          $"  call {Handler.trailersWrite}"
          $"  i32.const {blocked}"
          "  i32.eq"
          "  if"
          "    local.get $trailers"
          $"    call {waitFor}"
          "    drop"
          "  end"
          "  local.get $trailers"
          $"  call {Handler.trailersDropWritable}"
          "  local.get $value"
          $"  call ${Helpers.free}"
          ")" ]

/// The name of `handle`'s callback, which the host calls with each event while `handle` waits.
let handleCallback = $"[callback]{handleExport}"

/// What `handle` does before it runs the program, once it has raised backpressure, so that the host gives the instance
/// no other request until it is done: keeps the request, `$request`, for `argument`.
let handleBegin =
    [ "local.get $request"
      $"global.set {HandlerState.request}"
      "i32.const -1"
      $"global.set {HandlerState.response}" ]

/// What `handle` does once the program has answered: returns the response, then, while the host reads it, writes its
/// body, waiting as each write blocks, and its trailers. It leaves EXIT, 0, for `handle` to
/// return. It needs the locals `$writer`, `$bytes`, `$length` and `$status`.
let handleFinish =
    [ // A component root always answers: one that doesn't is a bug in its host.
      $"global.get {HandlerState.response}"
      "i32.const -1"
      "i32.eq"
      "if"
      "  unreachable"
      "end"
      "i32.const 0"
      $"global.get {HandlerState.response}"
      "i32.const 0"
      "i64.const 0"
      "i32.const 0"
      "i32.const 0"
      "i32.const 0"
      "i32.const 0"
      $"call {Handler.taskReturn}"
      $"global.get {HandlerState.body}"
      "local.set $writer"
      $"global.get {HandlerState.bytes}"
      "local.set $bytes"
      $"global.get {HandlerState.length}"
      "local.set $length"
      yield! writeAllLines $"call {Handler.bodyWrite}" |> List.map (fun line -> line.Substring 2)
      "local.get $writer"
      $"call {Handler.bodyDropWritable}"
      $"global.get {HandlerState.bytes}"
      $"call ${Helpers.free}"
      $"global.get {HandlerState.trailers}"
      "call $__nanyx_http_write_no_trailers"
      // EXIT: the task is done.
      "i32.const 0" ]

/// The locals `handleFinish` needs.
let handleLocals = "(local $writer i32) (local $bytes i32) (local $length i32) (local $status i32)"

/// `handle`, lifted async with a callback (see `HttpHandler`), for a program whose start function, `start`, if it has
/// one, never stops (see `ComponentCallback` for one that does): it runs the start function, which answers the request
/// through `argument` and `result`, and then returns the response and writes its body (`handleFinish`). The callback is
/// never called.
let private handleFunctions (start: string option) =
    lines
        [ $"(func $__nanyx_http_handle (export \"{handleExport}\") (param $request i32) (result i32) {handleLocals}"
          $"  call {backpressureInc}"
          yield! handleBegin |> List.map (fun line -> "  " + line)
          match start with
          | Some name -> $"  call ${name}"
          | None -> ()
          yield! handleFinish |> List.map (fun line -> "  " + line)
          $"  call {backpressureDec}"
          ")"
          $"(func $__nanyx_http_callback (export \"{handleCallback}\") (param $event i32) (param $handle i32) (param $code i32) (result i32)"
          "  unreachable"
          ")" ]

/// The functions a component's core module defines for the runtime: `fd_write` and `proc_exit` on `wasi:cli`, and
/// `cabi_realloc`, and for `HttpHandler`, the functions of `externs`, named by `functionName`.
let functions (entry: ComponentEntry) (functionName: Extern -> string) =
    [ waitForFunction
      writeFunction Stdout
      writeFunction Stderr
      fdWriteFunction
      procExitFunction entry
      cabiReallocFunction
      waitFunction functionName
      match entry with
      | CliRun
      | Library -> ()
      | HttpHandler ->
          componentFunctions functionName
          writeNoTrailersFunction ]
    |> String.concat "\n"

/// The entry, exported under its name for `entry`, for a program whose start function, if it has one, never stops (see
/// `ComponentCallback` for one that does): `run` runs the start function, gives `ok` (0) with `task.return`, and returns
/// EXIT, so its callback is never called; a program that crashes or exits stops in `proc_exit` instead, with its exit
/// code. `handle` is `handleFunctions`. A library has none: the root `nanyx build` makes for it only imports modules, so
/// it has no start function, and each export builds the top-level values it reads when it first reads them.
let entryFunction (entry: ComponentEntry) (start: string option) =
    match entry with
    | CliRun ->
        lines
            [ $"(func $__nanyx_cli_run (export \"{runExport}\") (result i32)"
              match start with
              | Some name -> $"  call ${name}"
              | None -> ()
              "  i32.const 0"
              $"  call {runTaskReturn}"
              // EXIT: the task is done.
              "  i32.const 0"
              ")"
              $"(func $__nanyx_cli_callback (export \"{runCallback}\") (param $event i32) (param $first i32) (param $second i32) (result i32)"
              "  unreachable"
              ")" ]
    | HttpHandler -> handleFunctions start
    | Library -> ""
