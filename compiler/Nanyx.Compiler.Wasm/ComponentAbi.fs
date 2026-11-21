/// What a module compiled as the core module of a WASI 0.3 component has that a module the runtime runs doesn't
/// (`WasmOptions.Component`): its entry exported under `wasi:cli/run`'s name, `cabi_realloc`, and the runtime's own
/// writes and exits on `wasi:cli` rather than on WASI preview 1.
///
/// The module follows wit-component's conventions, and the componentizer (`Nanyx.Runtime.Componentizer`) wraps it as a
/// component of a world that imports `wasi:cli/stdout`, `stderr` and `exit` and exports `wasi:cli/run`, all at
/// `@0.3.0`, such as `wasi:cli/command`. Its canonical ABI is written out here by hand, on `i32`s and `i64`s
/// (see the Components page under Work in progress).
module internal Transpiler.Wasm.ComponentAbi

open Transpiler.Wasm.WasmAllocator
open Transpiler.Wasm.WasmRuntime

/// The version of WASI 0.3 the module imports and exports, which every 0.3 host accepts.
let wasiVersion = "0.3.0"

/// The name the module exports its entry under: `wasi:cli/run`'s `run`, which returns `result`, lifted synchronously.
let runExport = $"wasi:cli/run@{wasiVersion}#run"

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

/// Every import of a component's core module that the runtime's own code needs.
let imports =
    [ yield! streamImports Stdout
      yield! streamImports Stderr
      $"""  (import "wasi:cli/exit@{wasiVersion}" "exit-with-code" (func {exitWithCode} (param i32)))"""
      $"""  (import "$root" "[waitable-set-new]" (func {Waitables.setNew} (result i32)))"""
      $"""  (import "$root" "[waitable-join]" (func {Waitables.join} (param i32 i32)))"""
      $"""  (import "$root" "[waitable-set-wait]" (func {Waitables.setWait} (param i32 i32) (result i32)))"""
      $"""  (import "$root" "[waitable-set-drop]" (func {Waitables.setDrop} (param i32)))""" ]
    |> String.concat "\n"

/// What an async built-in returns when it hasn't finished: the canonical ABI's BLOCKED.
let private blocked = -1

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
          "  block $done"
          "    loop $more"
          "      local.get $length"
          "      i32.eqz"
          "      br_if $done"
          "      local.get $writer"
          "      local.get $bytes"
          "      local.get $length"
          "      " + call "stream_write"
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
          "  end"
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

/// WASI preview 1's `proc_exit`, on `wasi:cli/exit`'s `exit-with-code`, which never returns either.
let private procExitFunction =
    lines
        [ $"(func ${Helpers.procExit} (param $code i32)"
          "  local.get $code"
          $"  call {exitWithCode}"
          ")" ]

/// `cabi_realloc` on the program's allocator: a new block of `$newSize` bytes, with the first bytes of the block at
/// `$old`, if there is one, copied into it and that block freed. Every block is aligned to 8 bytes, which is the most
/// any WIT type asks for.
let private cabiReallocFunction =
    lines
        [ $"(func $__nanyx_cli_cabi_realloc (export \"{cabiRealloc}\") (param $old i32) (param $oldSize i32) (param $align i32) (param $newSize i32) (result i32)"
          "  (local $new i32)"
          "  local.get $newSize"
          $"  call ${Helpers.alloc}"
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

/// The helpers `cabi_realloc` calls.
let reallocHelpers = [ Helpers.alloc; Helpers.free; Helpers.copyBytes ]

/// The functions a component's core module defines for the runtime: `fd_write` and `proc_exit` on `wasi:cli`, and
/// `cabi_realloc`.
let functions =
    [ waitForFunction
      writeFunction Stdout
      writeFunction Stderr
      fdWriteFunction
      procExitFunction
      cabiReallocFunction ]
    |> String.concat "\n"

/// The entry, exported under `runExport`: it runs the program's start function, if it has one, and returns `ok` (0).
/// A program that crashes or exits stops in `proc_exit` instead, with its exit code.
let runFunction (start: string option) =
    lines
        [ $"(func $__nanyx_cli_run (export \"{runExport}\") (result i32)"
          match start with
          | Some name -> $"  call ${name}"
          | None -> ()
          "  i32.const 0"
          ")" ]
